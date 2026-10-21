//! Motor de traducción:
//!  - tres traductores: Google (gratis, sin clave, no oficial), DeepL y Microsoft Translator (con clave);
//!  - agrupa varias líneas en UNA petición;
//!  - caché LRU para no repetir peticiones;
//!  - un `RateGate` por traductor con espera progresiva y respeto del Retry-After;
//!  - orden: el elegido, luego los otros que tengan clave (Microsoft, DeepL) y por último Google
//!    (respaldo que se puede desactivar). Si uno falla, el mismo lote pasa enseguida al siguiente;
//!  - cuando se acaba la cuota gratuita de Microsoft/DeepL, se vuelve a probar cada hora;
//!  - abreviaturas de chat escritas completas y palabras protegidas que nunca se traducen.

use crate::gate::RateGate;
use crate::terms::{expand_slang, ProtectedTerms};
use lru::LruCache;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;
use std::fmt;
use std::num::NonZeroUsize;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::time::{Duration, Instant};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum Provider {
    Google,
    DeepL,
    Microsoft,
}

impl Provider {
    pub fn name(self) -> &'static str {
        match self {
            Provider::Google => "Google",
            Provider::DeepL => "DeepL",
            Provider::Microsoft => "Microsoft",
        }
    }

    pub fn parse(s: &str) -> Option<Provider> {
        match s.to_ascii_lowercase().as_str() {
            "google" => Some(Provider::Google),
            "deepl" => Some(Provider::DeepL),
            "microsoft" => Some(Provider::Microsoft),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProviderState {
    Ok,
    NoKey,
    InvalidKey,
    QuotaExhausted,
}

#[derive(Clone, Debug)]
pub enum TrError {
    /// El traductor nos está limitando (429 o bloqueo). `retry_after` = pausa aplicada.
    RateLimited { provider: Provider, retry_after: Option<Duration> },
    /// Fallo pasajero (sin red, tiempo agotado, 5xx): merece reintento.
    Transient(String),
    /// Un traductor con clave no se puede usar. `quota` = se acabó la cuota gratuita (vuelve sola);
    /// si no, la clave no es válida (hay que corregirla en Ajustes).
    Unavailable { provider: Provider, message: String, quota: bool },
    /// Ningún traductor puede encargarse ahora (los de pago fallan y el respaldo con Google está desactivado).
    NoProvider(String),
    /// La traducción agrupada de Google no devolvió el mismo número de líneas.
    SplitMismatch,
    Other(String),
}

impl fmt::Display for TrError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            TrError::RateLimited { provider, .. } => write!(f, "{} ha limitado las peticiones", provider.name()),
            TrError::Transient(m) | TrError::NoProvider(m) | TrError::Other(m) => f.write_str(m),
            TrError::Unavailable { message, .. } => f.write_str(message),
            TrError::SplitMismatch => f.write_str("la respuesta agrupada no coincide con el número de líneas"),
        }
    }
}

impl std::error::Error for TrError {}

#[derive(Default)]
struct KeyState {
    key: Option<String>,
    region: Option<String>,
    /// Clave no válida: no se usa hasta que se compruebe otra.
    disabled: Option<String>,
    /// Cuota agotada: no se usa hasta esta hora.
    exhausted_until: Option<Instant>,
    quota_notified: bool,
}

/// Direcciones de los servicios (se cambian en las pruebas).
#[derive(Clone)]
pub struct Endpoints {
    pub google: String,
    pub microsoft: String,
    pub deepl_free: String,
    pub deepl_pro: String,
}

impl Default for Endpoints {
    fn default() -> Self {
        Self {
            google: "https://translate.googleapis.com/translate_a/single".into(),
            microsoft: "https://api.cognitive.microsofttranslator.com/translate".into(),
            deepl_free: "https://api-free.deepl.com".into(),
            deepl_pro: "https://api.deepl.com".into(),
        }
    }
}

type NoticeFn = Arc<dyn Fn(String, bool) + Send + Sync>;
type CharsFn = Arc<dyn Fn(Provider, usize) + Send + Sync>;

const MAX_ITEMS_PER_REQUEST: usize = 40;
const MAX_QUERY_CHARS: usize = 6000; // tamaño máximo (ya codificado) del texto en la URL de Google
const QUOTA_RETRY: Duration = Duration::from_secs(3600);
const KEYED_ORDER: [Provider; 2] = [Provider::Microsoft, Provider::DeepL];
const KEYED_ERROR_PAUSE: Duration = Duration::from_secs(20);
const PAUSED_THRESHOLD: Duration = Duration::from_secs(2);

pub struct Translator {
    http: reqwest::Client,
    endpoints: Endpoints,
    cache: Mutex<LruCache<String, String>>,
    // Google es un servicio público no oficial: una petición por segundo como mucho (las líneas que
    // lleguen mientras tanto viajan juntas en la siguiente), para no provocar bloqueos.
    google_gate: RateGate,
    deepl_gate: RateGate,
    microsoft_gate: RateGate,
    deepl: Mutex<KeyState>,
    microsoft: Mutex<KeyState>,
    preferred: Mutex<Provider>,
    google_fallback: AtomicBool,
    terms: RwLock<Arc<ProtectedTerms>>,
    on_notice: RwLock<Option<NoticeFn>>,
    on_chars: RwLock<Option<CharsFn>>,
}

impl Default for Translator {
    fn default() -> Self {
        Self::new(Endpoints::default())
    }
}

impl Translator {
    pub fn new(endpoints: Endpoints) -> Self {
        let http = reqwest::Client::builder()
            .user_agent("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36")
            .connect_timeout(Duration::from_secs(8))
            .timeout(Duration::from_secs(15))
            .pool_idle_timeout(Duration::from_secs(90))
            .build()
            .expect("cliente HTTP");
        Self {
            http,
            endpoints,
            cache: Mutex::new(LruCache::new(NonZeroUsize::new(5000).unwrap())),
            google_gate: RateGate::new(Duration::from_millis(1000)),
            deepl_gate: RateGate::new(Duration::from_millis(200)),
            microsoft_gate: RateGate::new(Duration::from_millis(200)),
            deepl: Mutex::default(),
            microsoft: Mutex::default(),
            preferred: Mutex::new(Provider::Microsoft),
            google_fallback: AtomicBool::new(true),
            terms: RwLock::new(Arc::new(ProtectedTerms::default())),
            on_notice: RwLock::new(None),
            on_chars: RwLock::new(None),
        }
    }

    // ---------------- Configuración ----------------

    /// Avisos para el usuario: (texto, es_aviso). P. ej. "Microsoft sin cuota, se usa Google".
    pub fn on_notice(&self, f: impl Fn(String, bool) + Send + Sync + 'static) {
        *self.on_notice.write().unwrap() = Some(Arc::new(f));
    }

    /// Caracteres enviados a un traductor en una traducción correcta (para el contador de uso).
    pub fn on_characters_used(&self, f: impl Fn(Provider, usize) + Send + Sync + 'static) {
        *self.on_chars.write().unwrap() = Some(Arc::new(f));
    }

    fn notice(&self, text: String, warn: bool) {
        let f = self.on_notice.read().unwrap().clone();
        if let Some(f) = f {
            f(text, warn);
        }
    }

    pub fn preferred(&self) -> Provider {
        *self.preferred.lock().unwrap()
    }

    pub fn set_preferred(&self, p: Provider) {
        *self.preferred.lock().unwrap() = p;
    }

    pub fn google_fallback(&self) -> bool {
        self.google_fallback.load(Ordering::Relaxed)
    }

    pub fn set_google_fallback(&self, on: bool) {
        self.google_fallback.store(on, Ordering::Relaxed);
    }

    /// Orden completo en que se prueban: el elegido, los otros con clave y Google.
    pub fn chain(&self) -> Vec<Provider> {
        let preferred = self.preferred();
        let mut v = vec![preferred];
        for p in KEYED_ORDER {
            if p != preferred {
                v.push(p);
            }
        }
        if preferred != Provider::Google && self.google_fallback() {
            v.push(Provider::Google);
        }
        v
    }

    /// El orden, solo con los que se pueden usar (Google o con clave guardada).
    pub fn configured_chain(&self) -> Vec<Provider> {
        self.chain().into_iter().filter(|p| *p == Provider::Google || self.key(*p).is_some()).collect()
    }

    fn state_of(&self, p: Provider) -> &Mutex<KeyState> {
        if p == Provider::Microsoft {
            &self.microsoft
        } else {
            &self.deepl
        }
    }

    fn gate(&self, p: Provider) -> &RateGate {
        match p {
            Provider::DeepL => &self.deepl_gate,
            Provider::Microsoft => &self.microsoft_gate,
            Provider::Google => &self.google_gate,
        }
    }

    pub fn set_key(&self, p: Provider, key: Option<&str>, region: Option<&str>) {
        if p == Provider::Google {
            return;
        }
        let clean = |s: Option<&str>| s.map(str::trim).filter(|s| !s.is_empty()).map(String::from);
        *self.state_of(p).lock().unwrap() = KeyState { key: clean(key), region: clean(region), ..Default::default() };
    }

    pub fn key(&self, p: Provider) -> Option<String> {
        if p == Provider::Google {
            return None;
        }
        self.state_of(p).lock().unwrap().key.clone()
    }

    pub fn state(&self, p: Provider) -> ProviderState {
        if p == Provider::Google {
            return ProviderState::Ok;
        }
        let st = self.state_of(p).lock().unwrap();
        if st.key.is_none() {
            ProviderState::NoKey
        } else if st.disabled.is_some() {
            ProviderState::InvalidKey
        } else if st.exhausted_until.is_some_and(|u| Instant::now() < u) {
            ProviderState::QuotaExhausted
        } else {
            ProviderState::Ok
        }
    }

    pub fn is_usable(&self, p: Provider) -> bool {
        self.state(p) == ProviderState::Ok
    }

    /// ¿Está en pausa por exceso de peticiones?
    pub fn is_paused(&self, p: Provider) -> bool {
        self.gate(p).cooldown_remaining() > PAUSED_THRESHOLD
    }

    pub fn set_protected_terms<I: IntoIterator<Item = S>, S: AsRef<str>>(&self, terms: I) {
        *self.terms.write().unwrap() = Arc::new(ProtectedTerms::new(terms));
        self.cache.lock().unwrap().clear(); // las traducciones guardadas pueden haber traducido esas palabras
    }

    /// `None` = ningún traductor disponible (solo si el respaldo con Google está desactivado).
    pub fn active_provider(&self) -> Option<Provider> {
        self.choose_provider(&[])
    }

    pub fn cooldown_remaining(&self) -> Duration {
        self.active_provider().map_or(Duration::ZERO, |p| self.gate(p).cooldown_remaining())
    }

    pub fn time_until_ready(&self) -> Duration {
        self.active_provider().map_or(Duration::ZERO, |p| self.gate(p).time_until_ready())
    }

    /// El primero del orden que funcione y no esté en pausa. Si todos los utilizables están en
    /// pausa, el que antes vuelva a estar libre. `None` si no queda ninguno utilizable.
    fn choose_provider(&self, exclude: &[Provider]) -> Option<Provider> {
        let mut best: Option<(Provider, Duration)> = None;
        for p in self.chain() {
            if exclude.contains(&p) || !self.is_usable(p) {
                continue;
            }
            let w = self.gate(p).cooldown_remaining();
            if w <= PAUSED_THRESHOLD {
                return Some(p);
            }
            if best.is_none_or(|(_, bw)| w < bw) {
                best = Some((p, w));
            }
        }
        best.map(|(p, _)| p)
    }

    /// El siguiente del orden que esté libre ya (para pasar el mismo lote sin esperar).
    fn next_ready(&self, tried: &[Provider]) -> Option<Provider> {
        self.chain()
            .into_iter()
            .find(|p| !tried.contains(p) && self.is_usable(*p) && self.gate(*p).cooldown_remaining() <= PAUSED_THRESHOLD)
    }

    fn no_provider_message(&self) -> String {
        let reasons: Vec<String> = KEYED_ORDER
            .iter()
            .map(|p| {
                let why = match self.state(*p) {
                    ProviderState::NoKey => "sin clave",
                    ProviderState::InvalidKey => "clave no válida",
                    ProviderState::QuotaExhausted => "cuota agotada",
                    ProviderState::Ok => "no responde",
                };
                format!("{}: {}", p.name(), why)
            })
            .collect();
        format!(
            "No hay ningún traductor disponible ({}) y el respaldo con Google está desactivado.",
            reasons.join("; ")
        )
    }

    // ---------------- Uso ----------------

    fn needs_translation(text: &str) -> bool {
        // Sin letras ("...", ":)", "123", "<3") no hay nada que traducir.
        text.chars().any(char::is_alphabetic)
    }

    fn cache_key(text: &str, source: &str, target: &str) -> String {
        format!("{source}>{target}\u{1}{text}")
    }

    pub fn try_get_cached(&self, text: &str, source: &str, target: &str) -> Option<String> {
        if !Self::needs_translation(text) {
            return Some(text.to_string());
        }
        self.cache.lock().unwrap().get(&Self::cache_key(text, source, target)).cloned()
    }

    /// Guarda una traducción conocida (el eco en el log de lo que acabamos de enviar: así no se
    /// gasta una petición en traducir de vuelta nuestro propio mensaje).
    pub fn remember(&self, text: &str, translated: &str, source: &str, target: &str) {
        if Self::needs_translation(text) {
            self.cache.lock().unwrap().put(Self::cache_key(text, source, target), translated.to_string());
        }
    }

    /// Traduce varios textos manteniendo el orden.
    pub async fn translate(&self, texts: &[String], source: &str, target: &str) -> Result<Vec<String>, TrError> {
        let mut results: Vec<Option<String>> = vec![None; texts.len()];
        let mut pending: Vec<String> = Vec::new();
        let mut where_: HashMap<String, Vec<usize>> = HashMap::new();
        for (i, t) in texts.iter().enumerate() {
            if let Some(c) = self.try_get_cached(t, source, target) {
                results[i] = Some(c);
                continue;
            }
            where_.entry(t.clone()).or_insert_with(|| {
                pending.push(t.clone());
                Vec::new()
            });
            where_.get_mut(t).unwrap().push(i);
        }

        for chunk in chunk(&pending) {
            let translated = self.translate_chunk(chunk.to_vec(), source, target).await?;
            for (j, t) in chunk.iter().enumerate() {
                for &i in &where_[t] {
                    results[i] = Some(translated[j].clone());
                }
            }
        }
        Ok(results.into_iter().map(Option::unwrap_or_default).collect())
    }

    fn translate_chunk<'a>(
        &'a self,
        chunk: Vec<String>,
        source: &'a str,
        target: &'a str,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<Vec<String>, TrError>> + Send + 'a>> {
        Box::pin(async move {
            // Lo que se manda al traductor: abreviaturas completas (solo en inglés) y palabras protegidas.
            let terms = self.terms.read().unwrap().clone();
            let mut prepared = Vec::with_capacity(chunk.len());
            let mut tokens = Vec::with_capacity(chunk.len());
            for t in &chunk {
                let t = if source.eq_ignore_ascii_case("en") { expand_slang(t) } else { t.clone() };
                let (p, tok) = terms.protect(&t);
                prepared.push(p);
                tokens.push(tok);
            }

            let mut provider = self.choose_provider(&[]).ok_or_else(|| TrError::NoProvider(self.no_provider_message()))?;
            let mut tried: Vec<Provider> = Vec::with_capacity(3);
            loop {
                tried.push(provider);
                let last = match self.call_provider(provider, &prepared, source, target).await {
                    Ok(result) => {
                        let mut out = Vec::with_capacity(chunk.len());
                        let mut cache = self.cache.lock().unwrap();
                        for (i, original) in chunk.iter().enumerate() {
                            let mut r = ProtectedTerms::restore(result.get(i).map_or("", String::as_str), &tokens[i]);
                            if r.trim().is_empty() {
                                r = original.clone();
                            }
                            cache.put(Self::cache_key(original, source, target), r.clone());
                            out.push(r);
                        }
                        return Ok(out);
                    }
                    Err(TrError::SplitMismatch) if chunk.len() > 1 => {
                        // La petición funcionó, solo falló el reparto por líneas: se parte el lote en dos.
                        let half = chunk.len() / 2;
                        let mut a = self.translate_chunk(chunk[..half].to_vec(), source, target).await?;
                        let b = self.translate_chunk(chunk[half..].to_vec(), source, target).await?;
                        a.extend(b);
                        return Ok(a);
                    }
                    Err(e @ TrError::Unavailable { .. }) => {
                        self.mark_unavailable(&e);
                        e
                    }
                    Err(e @ (TrError::RateLimited { .. } | TrError::Transient(_))) => e,
                    Err(e) => return Err(e),
                };

                // El mismo lote, enseguida, con el siguiente del orden que esté libre.
                if let Some(next) = self.next_ready(&tried) {
                    provider = next;
                    continue;
                }
                // Nadie libre ahora mismo.
                if matches!(last, TrError::RateLimited { .. } | TrError::Transient(_)) {
                    return Err(last);
                }
                if let Some(waiting) = self.choose_provider(&[]) {
                    return Err(TrError::RateLimited {
                        provider: waiting,
                        retry_after: Some(self.gate(waiting).cooldown_remaining()),
                    });
                }
                return Err(TrError::NoProvider(self.no_provider_message()));
            }
        })
    }

    /// Una petición a un traductor concreto, con su control de ritmo y el registro de éxitos y fallos.
    async fn call_provider(&self, p: Provider, prepared: &[String], source: &str, target: &str) -> Result<Vec<String>, TrError> {
        let gate = self.gate(p);
        gate.acquire().await;
        let result = match p {
            Provider::DeepL => self.deepl_request(prepared, source, target).await,
            Provider::Microsoft => {
                let (key, region) = {
                    let st = self.microsoft.lock().unwrap();
                    (st.key.clone(), st.region.clone())
                };
                match key {
                    Some(k) => self.microsoft_request(prepared, source, target, &k, region.as_deref()).await,
                    None => Err(TrError::Unavailable { provider: p, message: "sin clave".into(), quota: false }),
                }
            }
            Provider::Google => self.google_request(prepared, source, target).await,
        };
        match result {
            Ok(r) => {
                gate.report_success();
                self.on_provider_worked(p);
                let f = self.on_chars.read().unwrap().clone();
                if let Some(f) = f {
                    f(p, prepared.iter().map(|t| t.chars().count()).sum());
                }
                Ok(r)
            }
            Err(TrError::RateLimited { retry_after, .. }) => {
                let backoff = gate.report_rate_limited(retry_after);
                Err(TrError::RateLimited { provider: p, retry_after: Some(backoff) })
            }
            Err(e @ TrError::Transient(_)) => {
                // Un traductor con clave que falla se aparta un rato para que se encargue el siguiente.
                gate.report_transient_error(if p == Provider::Google { None } else { Some(KEYED_ERROR_PAUSE) });
                Err(e)
            }
            Err(TrError::SplitMismatch) => {
                gate.report_success();
                Err(TrError::SplitMismatch)
            }
            Err(e) => Err(e),
        }
    }

    fn mark_unavailable(&self, e: &TrError) {
        let TrError::Unavailable { provider, message, quota } = e else { return };
        let name = provider.name();
        if *quota {
            let first = {
                let mut st = self.state_of(*provider).lock().unwrap();
                st.exhausted_until = Some(Instant::now() + QUOTA_RETRY);
                !std::mem::replace(&mut st.quota_notified, true)
            };
            if first {
                self.notice(
                    format!(
                        "Se ha acabado la cuota gratuita de {name}. Mientras tanto {}; cada hora se vuelve a probar {name} y se usa en cuanto se renueve.",
                        self.who_translates_now()
                    ),
                    true,
                );
            }
        } else {
            self.state_of(*provider).lock().unwrap().disabled = Some(message.clone());
            self.notice(
                format!("{name} no acepta la clave ({message}). Hasta que la corrijas en Ajustes, {}.", self.who_translates_now()),
                true,
            );
        }
    }

    fn who_translates_now(&self) -> String {
        match self.choose_provider(&[]) {
            Some(p) => format!("se traduce con {}", p.name()),
            None => "no hay otro traductor disponible y los mensajes se muestran sin traducir".into(),
        }
    }

    fn on_provider_worked(&self, p: Provider) {
        if p == Provider::Google {
            return;
        }
        let was_notified = {
            let mut st = self.state_of(p).lock().unwrap();
            st.exhausted_until = None;
            std::mem::replace(&mut st.quota_notified, false)
        };
        if was_notified {
            self.notice(format!("{0} vuelve a estar disponible: se traduce otra vez con {0}.", p.name()), false);
        }
    }

    // ---------------- Comprobar claves ----------------

    /// Comprueba una clave de DeepL y devuelve el consumo del mes (usados, límite).
    pub async fn check_deepl_key(&self, key: &str) -> Result<(u64, u64), TrError> {
        let req = self
            .http
            .get(format!("{}/v2/usage", self.deepl_base(key)))
            .header("Authorization", format!("DeepL-Auth-Key {key}"));
        let body = self.send(req, Provider::DeepL).await?;
        let v: Value = serde_json::from_str(&body).unwrap_or(Value::Null);
        Ok((
            v.get("character_count").and_then(Value::as_u64).unwrap_or(0),
            v.get("character_limit").and_then(Value::as_u64).unwrap_or(0),
        ))
    }

    /// Comprueba una clave de Microsoft traduciendo una palabra.
    pub async fn check_microsoft_key(&self, key: &str, region: Option<&str>) -> Result<(), TrError> {
        self.microsoft_request(&["Hello".to_string()], "en", "es", key.trim(), region).await.map(|_| ())
    }

    // ---------------- Peticiones ----------------

    fn deepl_base(&self, key: &str) -> &str {
        // Las claves gratuitas terminan en ":fx" y van a api-free; las de pago, a api.deepl.com.
        if key.to_ascii_lowercase().ends_with(":fx") {
            &self.endpoints.deepl_free
        } else {
            &self.endpoints.deepl_pro
        }
    }

    /// Endpoint público no oficial (client=gtx). Varias líneas viajan juntas separadas por "\n"
    /// y se vuelven a separar en la respuesta.
    async fn google_request(&self, texts: &[String], source: &str, target: &str) -> Result<Vec<String>, TrError> {
        let joined = texts.join("\n");
        let url = url::Url::parse_with_params(
            &self.endpoints.google,
            &[("client", "gtx"), ("sl", source), ("tl", target), ("dt", "t"), ("q", &joined)],
        )
        .map_err(|e| TrError::Other(e.to_string()))?;

        let resp = self.http.get(url).send().await.map_err(net_error)?;
        // Redirección a la página "sorry" (captcha) = bloqueo temporal.
        if resp.url().path().to_ascii_lowercase().contains("/sorry") {
            return Err(TrError::RateLimited { provider: Provider::Google, retry_after: None });
        }
        let body = Self::check_status(resp, Provider::Google).await?;

        let trimmed = body.trim_start();
        if !trimmed.starts_with('[') {
            // Una página HTML en vez de datos es el aviso de "tráfico inusual": bloqueo, no un fallo puntual.
            if trimmed.starts_with('<') || trimmed.to_ascii_lowercase().contains("unusual traffic") {
                return Err(TrError::RateLimited { provider: Provider::Google, retry_after: None });
            }
            return Err(TrError::Transient("Google devolvió una respuesta inesperada.".into()));
        }

        let v: Value = serde_json::from_str(trimmed).map_err(|_| TrError::Transient("Respuesta de Google no válida.".into()))?;
        let segments = v
            .get(0)
            .and_then(Value::as_array)
            .ok_or_else(|| TrError::Transient("Google devolvió una respuesta sin traducción.".into()))?;
        let mut full = String::with_capacity(joined.len() + 16);
        for seg in segments {
            if let Some(s) = seg.get(0).and_then(Value::as_str) {
                full.push_str(s);
            }
        }

        if texts.len() == 1 {
            return Ok(vec![full.trim().to_string()]);
        }
        let parts: Vec<String> = full.trim_end_matches(['\r', '\n']).split('\n').map(|p| p.trim().to_string()).collect();
        if parts.len() != texts.len() {
            return Err(TrError::SplitMismatch);
        }
        Ok(parts)
    }

    async fn deepl_request(&self, texts: &[String], source: &str, target: &str) -> Result<Vec<String>, TrError> {
        let key = self
            .key(Provider::DeepL)
            .ok_or(TrError::Unavailable { provider: Provider::DeepL, message: "sin clave".into(), quota: false })?;
        let mut form: Vec<(&str, &str)> = texts.iter().map(|t| ("text", t.as_str())).collect();
        // Lo que escribes tú está siempre en español: indicarlo evita que DeepL confunda frases cortas
        // ("vale", "venga") con otro idioma. Lo que llega del juego se deja en automático porque cada
        // jugador puede escribir en un idioma distinto.
        if source.eq_ignore_ascii_case("es") {
            form.push(("source_lang", "ES"));
        }
        form.push(("target_lang", if target.eq_ignore_ascii_case("es") { "ES" } else { "EN-US" }));

        let req = self
            .http
            .post(format!("{}/v2/translate", self.deepl_base(&key)))
            .header("Authorization", format!("DeepL-Auth-Key {key}"))
            .form(&form);
        let body = self.send(req, Provider::DeepL).await?;
        let v: Value = serde_json::from_str(&body).map_err(|_| TrError::Transient("Respuesta de DeepL no válida.".into()))?;
        let arr = v
            .get("translations")
            .and_then(Value::as_array)
            .ok_or_else(|| TrError::Transient("Respuesta de DeepL no válida.".into()))?;
        if arr.len() != texts.len() {
            return Err(TrError::Transient("DeepL devolvió un número de traducciones distinto.".into()));
        }
        Ok(arr
            .iter()
            .zip(texts)
            .map(|(t, orig)| t.get("text").and_then(Value::as_str).unwrap_or(orig).to_string())
            .collect())
    }

    /// Microsoft Translator v3: una lista de textos en JSON y una traducción por texto, en el mismo
    /// orden. Lo que llega del juego va con detección automática; lo que escribes tú, como español.
    async fn microsoft_request(
        &self,
        texts: &[String],
        source: &str,
        target: &str,
        key: &str,
        region: Option<&str>,
    ) -> Result<Vec<String>, TrError> {
        let mut params = vec![("api-version", "3.0"), ("to", target)];
        if source.eq_ignore_ascii_case("es") {
            params.push(("from", "es"));
        }
        let url = url::Url::parse_with_params(&self.endpoints.microsoft, &params).map_err(|e| TrError::Other(e.to_string()))?;
        let payload: Vec<Value> = texts.iter().map(|t| serde_json::json!({ "Text": t })).collect();
        let mut req = self.http.post(url).header("Ocp-Apim-Subscription-Key", key).json(&payload);
        if let Some(r) = region.map(str::trim).filter(|r| !r.is_empty() && !r.eq_ignore_ascii_case("global")) {
            req = req.header("Ocp-Apim-Subscription-Region", r);
        }
        let body = self.send(req, Provider::Microsoft).await?;
        let v: Value = serde_json::from_str(&body).map_err(|_| TrError::Transient("Respuesta de Microsoft no válida.".into()))?;
        let arr = v.as_array().ok_or_else(|| TrError::Transient("Respuesta de Microsoft no válida.".into()))?;
        if arr.len() != texts.len() {
            return Err(TrError::Transient("Microsoft devolvió un número de traducciones distinto.".into()));
        }
        arr.iter()
            .zip(texts)
            .map(|(item, orig)| {
                item.get("translations")
                    .and_then(|t| t.get(0))
                    .and_then(|t| t.get("text"))
                    .map(|t| t.as_str().unwrap_or(orig).to_string())
                    .ok_or_else(|| TrError::Transient("Respuesta de Microsoft no válida.".into()))
            })
            .collect()
    }

    async fn send(&self, req: reqwest::RequestBuilder, p: Provider) -> Result<String, TrError> {
        let resp = req.send().await.map_err(net_error)?;
        Self::check_status(resp, p).await
    }

    /// Traduce los códigos HTTP a nuestros errores.
    async fn check_status(resp: reqwest::Response, p: Provider) -> Result<String, TrError> {
        let status = resp.status().as_u16();
        if resp.status().is_success() {
            return resp.text().await.map_err(net_error);
        }
        let retry_after = retry_after(&resp);
        let detail = resp.text().await.unwrap_or_default();

        if status == 429 {
            return Err(TrError::RateLimited { provider: p, retry_after });
        }
        let unavailable = |message: &str, quota: bool| TrError::Unavailable { provider: p, message: message.into(), quota };
        match p {
            // Google también bloquea con 403 o 503.
            Provider::Google if status == 403 || status == 503 => return Err(TrError::RateLimited { provider: p, retry_after }),
            Provider::DeepL if status == 456 => return Err(unavailable("cuota de caracteres agotada", true)),
            Provider::DeepL if status == 401 || status == 403 => return Err(unavailable("clave no válida", false)),
            Provider::Microsoft if status == 401 => return Err(unavailable("clave o región no válidas", false)),
            Provider::Microsoft if status == 403 => {
                // 403001 = se acabó la cuota gratuita (plan F0): no cobra, solo deja de traducir hasta que se renueva.
                let code = serde_json::from_str::<Value>(&detail)
                    .ok()
                    .and_then(|v| v.get("error")?.get("code")?.as_i64());
                let quota = code == Some(403001) || (code.is_none() && detail.to_ascii_lowercase().contains("quota"));
                return Err(if quota {
                    unavailable("cuota gratuita agotada", true)
                } else {
                    unavailable("la clave no tiene permiso o la suscripción está desactivada", false)
                });
            }
            _ => {}
        }
        if status >= 500 {
            return Err(TrError::Transient(format!("el servidor respondió {status}")));
        }
        let short: String = detail.chars().take(200).collect();
        Err(TrError::Other(format!("{} respondió {status}: {short}", p.name())))
    }
}

// Sin la URL: en la de Google va el texto del mensaje.
fn net_error(e: reqwest::Error) -> TrError {
    if e.is_timeout() {
        TrError::Transient("tiempo de espera agotado".into())
    } else if e.is_connect() {
        TrError::Transient("sin conexión con el traductor".into())
    } else {
        TrError::Transient(format!("fallo de red ({})", e.without_url()))
    }
}

fn retry_after(resp: &reqwest::Response) -> Option<Duration> {
    let v = resp.headers().get(reqwest::header::RETRY_AFTER)?.to_str().ok()?.trim();
    let d = if let Ok(secs) = v.parse::<u64>() {
        Duration::from_secs(secs)
    } else {
        let date = chrono::DateTime::parse_from_rfc2822(v).ok()?;
        (date.with_timezone(&chrono::Utc) - chrono::Utc::now()).to_std().unwrap_or(Duration::ZERO)
    };
    Some(d.min(Duration::from_secs(300)))
}

/// Parte la lista en lotes: como mucho 40 textos y ~6000 caracteres (ya codificados) por petición.
fn chunk(items: &[String]) -> Vec<&[String]> {
    let mut out = Vec::new();
    let mut start = 0;
    let mut size = 0;
    for (i, item) in items.iter().enumerate() {
        let cost = url::form_urlencoded::byte_serialize(item.as_bytes()).map(str::len).sum::<usize>() + 3;
        let count = i - start;
        if count > 0 && (count >= MAX_ITEMS_PER_REQUEST || size + cost > MAX_QUERY_CHARS) {
            out.push(&items[start..i]);
            start = i;
            size = 0;
        }
        size += cost;
    }
    if start < items.len() {
        out.push(&items[start..]);
    }
    out
}

#[cfg(test)]
mod tests;
