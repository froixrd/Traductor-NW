//! Une las piezas: log del juego → traducción → interfaz, y lo que escribes → inglés → juego.

use crate::events::*;
use crate::gamefiles::{self, ChatLogging};
use crate::logwatch::LogWatcher;
use crate::outgoing;
use crate::parser::{ChatParser, LineKind};
use crate::pipeline::{self, Item, Output, Pipeline};
use crate::settings::{Settings, THEMES};
use crate::terms::ProtectedTerms;
use crate::translate::{Provider, ProviderState, TrError, Translator};
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot, Notify};
use tokio_util::sync::CancellationToken;

/// Caracteres gratis al mes del plan F0 de Microsoft Translator.
pub const MICROSOFT_FREE_CHARS: u64 = 2_000_000;
const RECENT_SENT_TTL: Duration = Duration::from_secs(60);

// ---------- Ajustes tal como los ve la interfaz ----------

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UiSettings {
    pub provider: &'static str,
    pub microsoft_key: String,
    pub microsoft_region: String,
    pub deepl_key: String,
    pub google_fallback: bool,
    pub no_translate: Vec<String>,
    pub filter_system: bool,
    pub send_delay_ms: i32,
    pub return_to_app: bool,
    pub hotkey: bool,
    pub alert_private: bool,
    pub theme: String,
    pub always_on_top: bool,
    pub compact: bool,
    pub inactive_opacity: i32,
    pub upper_case: bool,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsPatch {
    pub provider: Option<String>,
    pub google_fallback: Option<bool>,
    pub no_translate: Option<Vec<String>>,
    pub filter_system: Option<bool>,
    pub send_delay_ms: Option<i32>,
    pub return_to_app: Option<bool>,
    pub hotkey: Option<bool>,
    pub alert_private: Option<bool>,
    pub theme: Option<String>,
    pub always_on_top: Option<bool>,
    pub compact: Option<bool>,
    pub inactive_opacity: Option<i32>,
    pub upper_case: Option<bool>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProviderInfo {
    pub state: &'static str, // "ok" | "noKey" | "invalidKey" | "quotaExhausted"
    pub usage_count: Option<u64>,
    pub usage_limit: Option<u64>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyCheck {
    pub ok: bool,
    pub message: String,
    pub usage_count: Option<u64>,
    pub usage_limit: Option<u64>,
}

// ---------- Mensajes que escribes ----------

struct OutItem {
    text: String,
    raw: bool,
    return_to_app: bool,
    cancel: CancellationToken,
    committed: AtomicBool,
    last: Mutex<OwnEvent>,
}

pub struct EngineConfig {
    pub game_dirs: Vec<PathBuf>,
    /// None = %AppData%\TraductorNWN\config.json
    pub settings_path: Option<PathBuf>,
    pub incoming_max_wait: Duration,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self { game_dirs: gamefiles::user_directories(), settings_path: None, incoming_max_wait: Duration::from_secs(15) }
    }
}

struct Inner {
    cfg: EngineConfig,
    settings: Mutex<Settings>,
    save: Notify,
    tr: Arc<Translator>,
    ui: Arc<dyn Ui>,
    game: Arc<dyn Game>,
    pipeline: Pipeline,
    runner: Mutex<Option<pipeline::Runner>>,
    parser: Mutex<ChatParser>,
    ids: AtomicU64,
    started: AtomicBool,
    log_found: AtomicBool,
    last_raw: Mutex<Option<(String, Instant)>>,
    recent_sent: Mutex<Vec<(String, Instant)>>,
    awaiting_echo: Mutex<Vec<(u64, String, Option<oneshot::Sender<()>>)>>,
    pending_out: Mutex<Vec<Arc<OutItem>>>,
    out_tx: mpsc::UnboundedSender<Arc<OutItem>>,
    out_rx: Mutex<Option<mpsc::UnboundedReceiver<Arc<OutItem>>>>,
    last_alert: Mutex<Option<Instant>>,
    last_status: Mutex<Option<StatusEvent>>,
    deepl_usage: Mutex<Option<(u64, u64)>>,
    cancel: CancellationToken,
}

#[derive(Clone)]
pub struct Engine {
    inner: Arc<Inner>,
}

static SPACES: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\s+").unwrap());

fn normalize(s: &str) -> String {
    SPACES.replace_all(s.trim(), " ").to_uppercase()
}

/// Tu mensaje en el log puede llevar algo delante (p. ej. el servidor añade una etiqueta):
/// basta con que lo contenga entero.
fn echo_matches(log_key: &str, sent_key: &str) -> bool {
    log_key == sent_key || (sent_key.chars().count() >= 4 && log_key.contains(sent_key))
}

fn shorten(s: &str) -> String {
    if s.chars().count() <= 40 {
        s.to_string()
    } else {
        s.chars().take(39).collect::<String>() + "…"
    }
}

fn state_name(s: ProviderState) -> &'static str {
    match s {
        ProviderState::Ok => "ok",
        ProviderState::NoKey => "noKey",
        ProviderState::InvalidKey => "invalidKey",
        ProviderState::QuotaExhausted => "quotaExhausted",
    }
}

struct PipelineOut(Arc<dyn Ui>, Arc<AtomicU64>);

impl Output for PipelineOut {
    fn translated(&self, item: &Item, text: String) {
        self.0.translation(TranslationEvent { id: item.id, text, untranslated: None, echo: item.echo });
    }
    fn untranslated(&self, item: &Item, reason: String) {
        self.0.translation(TranslationEvent { id: item.id, text: item.line.text.clone(), untranslated: Some(reason), echo: item.echo });
    }
    fn notice(&self, text: String, warn: bool) {
        let id = self.1.fetch_add(1, Ordering::Relaxed);
        self.0.notice(NoticeEvent { id, text, tone: if warn { Tone::Warn } else { Tone::Ok } });
    }
}

impl Engine {
    pub fn new(settings: Settings, cfg: EngineConfig, tr: Translator, ui: Arc<dyn Ui>, game: Arc<dyn Game>) -> Self {
        let (pipeline, runner) = pipeline::new(cfg.incoming_max_wait, 60);
        let (out_tx, out_rx) = mpsc::unbounded_channel();
        let inner = Arc::new(Inner {
            cfg,
            settings: Mutex::new(settings),
            save: Notify::new(),
            tr: Arc::new(tr),
            ui,
            game,
            pipeline,
            runner: Mutex::new(Some(runner)),
            parser: Mutex::new(ChatParser::new()),
            ids: AtomicU64::new(1),
            started: AtomicBool::new(false),
            log_found: AtomicBool::new(false),
            last_raw: Mutex::new(None),
            recent_sent: Mutex::new(Vec::new()),
            awaiting_echo: Mutex::new(Vec::new()),
            pending_out: Mutex::new(Vec::new()),
            out_tx,
            out_rx: Mutex::new(Some(out_rx)),
            last_alert: Mutex::new(None),
            last_status: Mutex::new(None),
            deepl_usage: Mutex::new(None),
            cancel: CancellationToken::new(),
        });
        let engine = Engine { inner };
        engine.apply_settings_to_translator();

        let weak = Arc::downgrade(&engine.inner);
        engine.inner.tr.on_notice(move |text, warn| {
            if let Some(inner) = weak.upgrade() {
                Engine { inner }.notice(text, if warn { Tone::Warn } else { Tone::Ok });
            }
        });
        let weak = Arc::downgrade(&engine.inner);
        engine.inner.tr.on_characters_used(move |p, chars| {
            if p != Provider::Microsoft {
                return;
            }
            if let Some(inner) = weak.upgrade() {
                inner.settings.lock().unwrap().add_microsoft_usage(chars);
                inner.save.notify_one();
            }
        });
        engine
    }

    pub fn translator(&self) -> &Translator {
        &self.inner.tr
    }

    fn next_id(&self) -> u64 {
        self.inner.ids.fetch_add(1, Ordering::Relaxed)
    }

    pub fn notice(&self, text: impl Into<String>, tone: Tone) {
        self.inner.ui.notice(NoticeEvent { id: self.next_id(), text: text.into(), tone });
    }

    fn apply_settings_to_translator(&self) {
        let s = self.inner.settings.lock().unwrap();
        let tr = &self.inner.tr;
        // Las dos claves se cargan siempre: si un traductor falla, se usa el otro mientras tanto.
        tr.set_key(Provider::DeepL, s.deepl_key.as_deref(), None);
        tr.set_key(Provider::Microsoft, s.microsoft_key.as_deref(), s.microsoft_region.as_deref());
        tr.set_google_fallback(s.google_de_respaldo);
        tr.set_preferred(s.proveedor.as_deref().and_then(Provider::parse).unwrap_or(Provider::Microsoft));
        tr.set_protected_terms(&s.no_traducir);
    }

    // ======================================================================
    //  Arranque y cierre
    // ======================================================================

    /// Se llama cuando la interfaz ya escucha los eventos. Arranca el lector del log, la cola de
    /// traducción, el envío al juego, el indicador de estado y el guardado de ajustes.
    pub fn start(&self, rt: &tokio::runtime::Handle) {
        if self.inner.started.swap(true, Ordering::SeqCst) {
            // La interfaz se ha recargado: basta con volver a mandarle el estado.
            *self.inner.last_status.lock().unwrap() = None;
            self.emit_status();
            return;
        }
        let inner = &self.inner;

        // Lector del log (hilo propio: lee el disco cada 150 ms).
        let stop = Arc::new(AtomicBool::new(false));
        {
            let e1 = self.clone();
            let e2 = self.clone();
            let watcher = LogWatcher::new(
                &inner.cfg.game_dirs,
                move |lines| e1.on_log_lines(lines),
                move |msg, found| {
                    if found {
                        e2.inner.log_found.store(true, Ordering::SeqCst);
                    }
                    e2.notice(msg, if found { Tone::Accent } else { Tone::Info });
                },
            );
            let stop2 = stop.clone();
            std::thread::Builder::new().name("lector-log".into()).spawn(move || watcher.run(stop2)).ok();
            let cancel = inner.cancel.clone();
            rt.spawn(async move {
                cancel.cancelled().await;
                stop.store(true, Ordering::SeqCst);
            });
        }

        // Cola de lo que llega del juego.
        if let Some(runner) = inner.runner.lock().unwrap().take() {
            let out: Arc<dyn Output> = Arc::new(PipelineOut(inner.ui.clone(), Arc::new(AtomicU64::new(1u64 << 40))));
            rt.spawn(runner.run(inner.tr.clone(), out, inner.cancel.clone()));
        }

        // Envío al juego, de uno en uno y en orden.
        if let Some(mut rx) = inner.out_rx.lock().unwrap().take() {
            let e = self.clone();
            rt.spawn(async move {
                loop {
                    let item = tokio::select! {
                        _ = e.inner.cancel.cancelled() => return,
                        i = rx.recv() => match i { Some(i) => i, None => return },
                    };
                    e.send_one(&item).await;
                    e.inner.pending_out.lock().unwrap().retain(|x| !Arc::ptr_eq(x, &item));
                }
            });
        }

        // Indicador de estado.
        {
            let e = self.clone();
            rt.spawn(async move {
                loop {
                    e.emit_status();
                    tokio::select! {
                        _ = e.inner.cancel.cancelled() => return,
                        _ = tokio::time::sleep(Duration::from_millis(400)) => {}
                    }
                }
            });
        }

        // Guardado de ajustes (agrupa los cambios seguidos).
        {
            let e = self.clone();
            rt.spawn(async move {
                loop {
                    tokio::select! {
                        _ = e.inner.cancel.cancelled() => return,
                        _ = e.inner.save.notified() => {}
                    }
                    tokio::time::sleep(Duration::from_millis(700)).await;
                    e.save_now();
                }
            });
        }

        self.check_game_chat_logging();
        self.check_microsoft_setup();
        if self.inner.tr.preferred() == Provider::DeepL {
            if let Some(key) = self.inner.tr.key(Provider::DeepL) {
                let e = self.clone();
                rt.spawn(async move {
                    if let Ok(u) = e.inner.tr.check_deepl_key(&key).await {
                        *e.inner.deepl_usage.lock().unwrap() = Some(u);
                    }
                });
            }
        }
    }

    pub fn shutdown(&self) {
        self.inner.cancel.cancel();
        self.save_now();
    }

    pub fn save_now(&self) {
        let mut s = self.inner.settings.lock().unwrap();
        let _ = match &self.inner.cfg.settings_path {
            Some(p) => s.save_to(p),
            None => s.save(),
        };
    }

    pub fn schedule_save(&self) {
        self.inner.save.notify_one();
    }

    /// Para que la app lea o cambie lo que no pasa por la interfaz (posición de la ventana).
    pub fn with_settings<R>(&self, f: impl FnOnce(&mut Settings) -> R) -> R {
        f(&mut self.inner.settings.lock().unwrap())
    }

    /// NWN:EE no guarda el chat en el log salvo que se active en settings.tml.
    fn check_game_chat_logging(&self) {
        let (state, path) = gamefiles::check_chat_logging(&self.inner.cfg.game_dirs);
        if state != ChatLogging::Disabled {
            return;
        }
        let Some(path) = path else { return };
        self.notice(
            format!(
                "El juego no está guardando el chat en su log, así que no llegará nada que traducir. Cierra el juego, abre {} y en la sección {} pon «enabled = true». Deja «enabled = false» en {} para que no salga cada mensaje dos veces.",
                gamefiles::display(&path),
                gamefiles::SECTION_ALL,
                gamefiles::SECTION_TEXT
            ),
            Tone::Warn,
        );
    }

    /// Microsoft es el traductor principal: si aún no hay clave, se explica cómo conseguirla.
    fn check_microsoft_setup(&self) {
        let tr = &self.inner.tr;
        if tr.preferred() != Provider::Microsoft || tr.key(Provider::Microsoft).is_some() {
            return;
        }
        let now = tr.active_provider().map_or("ningún traductor", |p| p.name());
        self.notice(
            format!(
                "Ahora se traduce con {now}. Para usar Microsoft Translator (gratis hasta 2 millones de caracteres al mes) pega tu clave en Ajustes, en Traductor. Los pasos para conseguirla están en el README del proyecto."
            ),
            Tone::Info,
        );
    }

    // ======================================================================
    //  Estado
    // ======================================================================

    pub fn status(&self) -> StatusEvent {
        let tr = &self.inner.tr;
        let active = tr.active_provider();
        let preferred = tr.preferred();
        let mut note = String::new();
        let mut note_warn = false;
        if Some(preferred) != active && preferred != Provider::Google {
            let name = preferred.name();
            match tr.state(preferred) {
                ProviderState::NoKey => note = format!("sin clave de {name}"),
                ProviderState::QuotaExhausted => {
                    note = format!("{name} sin cuota");
                    note_warn = true;
                }
                ProviderState::InvalidKey => {
                    note = format!("clave de {name} no válida");
                    note_warn = true;
                }
                ProviderState::Ok => {
                    if tr.is_paused(preferred) {
                        note = format!("{name} en pausa");
                    }
                }
            }
        }
        let chain = tr.configured_chain();
        StatusEvent {
            active: active.map(|p| p.name()),
            note,
            note_warn,
            cooldown_s: (tr.cooldown_remaining().as_secs_f64() * 10.0).round() / 10.0,
            pending: self.inner.pipeline.pending(),
            log_found: self.inner.log_found.load(Ordering::Relaxed),
            google_in_chain: chain.contains(&Provider::Google),
            chain: chain.into_iter().map(|p| p.name()).collect(),
        }
    }

    fn emit_status(&self) {
        let st = self.status();
        let mut last = self.inner.last_status.lock().unwrap();
        if last.as_ref() != Some(&st) {
            *last = Some(st.clone());
            drop(last);
            self.inner.ui.status(st);
        }
    }

    // ======================================================================
    //  Entrada: log del juego → traducción → interfaz
    // ======================================================================

    fn on_log_lines(&self, lines: Vec<String>) {
        let inner = &self.inner;
        for raw in lines {
            // La misma línea dos veces seguidas en el mismo instante: el juego la registró por
            // partida doble (p. ej. con dos opciones de log activas). Se ignora la repetición.
            {
                let mut last = inner.last_raw.lock().unwrap();
                if let Some((text, at)) = last.as_ref() {
                    if *text == raw && at.elapsed() < Duration::from_secs(1) && !raw.trim().is_empty() {
                        continue;
                    }
                }
                *last = Some((raw.clone(), Instant::now()));
            }

            let Some(mut line) = inner.parser.lock().unwrap().parse(&raw) else { continue };
            if line.time.is_none() {
                line.time = Some(chrono::Local::now().format("%H:%M").to_string());
            }

            let (filter, alert) = {
                let s = inner.settings.lock().unwrap();
                (s.filtrar_sistema, s.avisar_privados)
            };
            let translate = line.kind == LineKind::Chat || !filter;
            let mut echo = false;
            if line.kind == LineKind::Chat {
                self.signal_echo(&line.text);
                echo = self.consume_echo(&line.text);
                if alert && !echo && line.channel.as_deref().is_some_and(|c| c.eq_ignore_ascii_case("tell")) {
                    self.alert_private();
                }
            }

            let id = self.next_id();
            inner.ui.line(LineEvent {
                id,
                kind: if line.kind == LineKind::Chat { "chat" } else { "system" },
                time: line.time.clone().unwrap_or_default(),
                speaker: line.speaker.clone(),
                channel: line.channel.clone(),
                text: line.text.clone(),
                translate,
                echo,
            });
            if translate {
                inner.pipeline.enqueue(id, line, echo);
            }
        }
    }

    /// Mensaje privado: sonido y parpadeo (como mucho uno cada 2 s).
    fn alert_private(&self) {
        let mut last = self.inner.last_alert.lock().unwrap();
        if last.is_some_and(|t| t.elapsed() < Duration::from_secs(2)) {
            return;
        }
        *last = Some(Instant::now());
        drop(last);
        self.inner.ui.alert_private();
    }

    fn remember_sent(&self, key: &str) {
        let mut v = self.inner.recent_sent.lock().unwrap();
        v.retain(|(_, at)| at.elapsed() <= RECENT_SENT_TTL);
        v.push((key.to_string(), Instant::now()));
    }

    fn forget_sent(&self, key: &str) {
        let mut v = self.inner.recent_sent.lock().unwrap();
        if let Some(i) = v.iter().rposition(|(k, _)| k == key) {
            v.remove(i);
        }
    }

    fn consume_echo(&self, text: &str) -> bool {
        let key = normalize(text);
        let mut v = self.inner.recent_sent.lock().unwrap();
        v.retain(|(_, at)| at.elapsed() <= RECENT_SENT_TTL);
        match v.iter().position(|(k, _)| echo_matches(&key, k)) {
            Some(i) => {
                v.remove(i);
                true
            }
            None => false,
        }
    }

    fn expect_echo(&self, id: u64, key: &str) -> oneshot::Receiver<()> {
        let (tx, rx) = oneshot::channel();
        self.inner.awaiting_echo.lock().unwrap().push((id, key.to_string(), Some(tx)));
        rx
    }

    fn forget_echo(&self, id: u64) {
        self.inner.awaiting_echo.lock().unwrap().retain(|(i, _, _)| *i != id);
    }

    fn signal_echo(&self, text: &str) {
        let key = normalize(text);
        let mut v = self.inner.awaiting_echo.lock().unwrap();
        if let Some(i) = v.iter().position(|(_, k, _)| echo_matches(&key, k)) {
            let (_, _, tx) = v.remove(i);
            if let Some(tx) = tx {
                let _ = tx.send(());
            }
        }
    }

    // ======================================================================
    //  Salida: lo que escribes → inglés → juego
    // ======================================================================

    fn emit_own(&self, item: &OutItem, f: impl FnOnce(&mut OwnEvent)) {
        let ev = {
            let mut last = item.last.lock().unwrap();
            f(&mut last);
            last.clone()
        };
        self.inner.ui.own(ev);
    }

    /// Enter: no se traduce aquí, así la ventana nunca espera a la red.
    pub fn send(&self, text: &str, raw: bool, return_to_app: bool) {
        let text = text.trim();
        if text.is_empty() {
            return;
        }
        let parts = outgoing::split(text);
        let first = if parts.translate && !raw { OwnStatus::Traduciendo } else { OwnStatus::Esperando };
        let item = Arc::new(OutItem {
            text: text.to_string(),
            raw,
            return_to_app,
            cancel: CancellationToken::new(),
            committed: AtomicBool::new(false),
            last: Mutex::new(OwnEvent { id: self.next_id(), es: text.to_string(), en: None, status: first, delay_ms: None, raw }),
        });
        self.inner.pending_out.lock().unwrap().push(item.clone());
        self.inner.ui.own(item.last.lock().unwrap().clone());
        let _ = self.inner.out_tx.send(item);
    }

    /// Esc: cancela el último mensaje que aún no ha empezado a escribirse en el juego y devuelve su
    /// texto para poder corregirlo.
    pub fn cancel_last(&self) -> Option<String> {
        let item = {
            let list = self.inner.pending_out.lock().unwrap();
            list.iter()
                .rev()
                .find(|i| !i.committed.load(Ordering::SeqCst) && !i.cancel.is_cancelled())
                .cloned()?
        };
        item.cancel.cancel();
        self.emit_own(&item, |e| {
            e.status = OwnStatus::Cancelado;
            e.delay_ms = None;
        });
        Some(item.text.clone())
    }

    async fn translate_outgoing(&self, item: &OutItem, body: &str) -> Result<String, TrError> {
        let tr = &self.inner.tr;
        let deadline = Instant::now() + Duration::from_secs(20);
        let mut queued = false;
        loop {
            match tr.translate(&[body.to_string()], "es", "en").await {
                Ok(mut r) => return Ok(r.remove(0)),
                Err(e @ (TrError::RateLimited { .. } | TrError::Transient(_))) => {
                    let wait = tr.time_until_ready().max(Duration::from_millis(300));
                    if Instant::now() + wait > deadline {
                        return Err(e);
                    }
                    if !queued {
                        queued = true;
                        self.emit_own(item, |ev| ev.status = OwnStatus::Cola);
                    }
                    tokio::time::sleep(wait).await;
                }
                Err(e) => return Err(e),
            }
        }
    }

    async fn wait_modifiers_released(&self, timeout: Duration) {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline && self.inner.game.modifiers_down() {
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
    }

    async fn send_one(&self, item: &Arc<OutItem>) {
        let inner = &self.inner;
        let parts = outgoing::split(&item.text);
        let (delay, upper) = {
            let s = inner.settings.lock().unwrap();
            (s.retardo_envio_ms.max(0) as u64, s.mayusculas)
        };

        // ---------- Traducir, esperar y comprobar el teclado (se puede cancelar con Esc) ----------
        let prepared = async {
            let mut body = parts.body.clone();
            let mut translated = false;
            if parts.translate && !item.raw {
                match self.translate_outgoing(item, &parts.body).await {
                    Ok(t) => {
                        body = t;
                        translated = true;
                    }
                    Err(e) => return Err(e),
                }
            }
            if upper && parts.is_chat {
                body = body.to_uppercase();
            }
            body = outgoing::for_game(&body);
            // Cuando el juego escriba el mensaje en el log se mostrará tu español original sin otra petición.
            if translated {
                inner.tr.remember(&body, &parts.body, "en", "es");
            }
            let shown = parts.compose(&body);
            self.emit_own(item, |e| {
                e.en = Some(shown);
                e.status = OwnStatus::Esperando;
                e.delay_ms = Some(delay);
            });
            tokio::time::sleep(Duration::from_millis(delay)).await;
            // Con Alt, Ctrl o Mayús pulsadas, el Enter que se manda al juego haría otra cosa
            // (Alt+Enter = pantalla completa). Pasa sobre todo con Ctrl+Enter sin espera.
            self.wait_modifiers_released(Duration::from_millis(1500)).await;
            Ok(body)
        };
        let body = tokio::select! {
            _ = item.cancel.cancelled() => return,
            _ = inner.cancel.cancelled() => return,
            r = prepared => match r {
                Ok(b) => b,
                Err(e) => {
                    self.notice(
                        format!("No se pudo traducir «{}»: {e}. Pulsa Ctrl+Enter para enviarlo sin traducir.", shorten(&item.text)),
                        Tone::Warn,
                    );
                    inner.ui.restore(item.text.clone());
                    self.emit_own(item, |ev| ev.status = OwnStatus::Fallido);
                    return;
                }
            },
        };

        // ---------- A partir de aquí ya no se puede cancelar ----------
        {
            let _list = inner.pending_out.lock().unwrap();
            if item.cancel.is_cancelled() {
                return;
            }
            item.committed.store(true, Ordering::SeqCst);
        }

        let text_for_game = parts.compose(&body);
        let echo_key = parts.is_chat.then(|| normalize(&body));
        let own_id = item.last.lock().unwrap().id;
        // Se apunta antes de enviar para no perder un eco que llegue muy rápido.
        let seen = match &echo_key {
            Some(k) if inner.log_found.load(Ordering::Relaxed) => Some(self.expect_echo(own_id, k)),
            _ => None,
        };
        if let Some(k) = &echo_key {
            self.remember_sent(k);
        }
        self.emit_own(item, |e| {
            e.status = OwnStatus::Escribiendo;
            e.delay_ms = None;
        });

        let game = inner.game.clone();
        let to_send = text_for_game.clone();
        let return_to_app = item.return_to_app;
        let result = tokio::task::spawn_blocking(move || game.send(&to_send, return_to_app))
            .await
            .unwrap_or_else(|e| Err(format!("Error al enviar al juego: {e}")));

        let status = match &result {
            Err(_) => OwnStatus::Fallido,
            Ok(()) => match seen {
                // El juego escribe tus propios mensajes en el log: si aparece, salió de verdad.
                Some(rx) => match tokio::time::timeout(Duration::from_secs(5), rx).await {
                    Ok(Ok(())) => OwnStatus::Confirmado,
                    _ => OwnStatus::SinConfirmar,
                },
                None => OwnStatus::Enviado,
            },
        };
        self.forget_echo(own_id);
        if status == OwnStatus::Fallido {
            if let Some(k) = &echo_key {
                self.forget_sent(k);
            }
        }
        self.emit_own(item, |e| {
            e.en = Some(text_for_game);
            e.status = status;
        });
        match result {
            Err(err) => {
                self.notice(err, Tone::Warn);
                inner.ui.restore(item.text.clone());
            }
            Ok(()) if status == OwnStatus::SinConfirmar => self.notice(
                "Tu mensaje no aparece en el log del juego. Si se quedó escrito en el chat del juego, pulsa Enter allí.",
                Tone::Warn,
            ),
            Ok(()) => {}
        }
    }

    // ======================================================================
    //  Ajustes
    // ======================================================================

    pub fn ui_settings(&self) -> UiSettings {
        let s = self.inner.settings.lock().unwrap();
        UiSettings {
            provider: s.proveedor.as_deref().and_then(Provider::parse).unwrap_or(Provider::Microsoft).name(),
            microsoft_key: s.microsoft_key.clone().unwrap_or_default(),
            microsoft_region: s.microsoft_region.clone().unwrap_or_default(),
            deepl_key: s.deepl_key.clone().unwrap_or_default(),
            google_fallback: s.google_de_respaldo,
            no_translate: s.no_traducir.clone(),
            filter_system: s.filtrar_sistema,
            send_delay_ms: s.retardo_envio_ms,
            return_to_app: s.volver_al_traductor,
            hotkey: s.tecla_rapida,
            alert_private: s.avisar_privados,
            theme: s.tema.clone(),
            always_on_top: s.siempre_encima,
            compact: s.compacto,
            inactive_opacity: s.opacidad_inactiva,
            upper_case: s.mayusculas,
        }
    }

    pub fn update_settings(&self, p: SettingsPatch) -> UiSettings {
        {
            let mut s = self.inner.settings.lock().unwrap();
            let tr = &self.inner.tr;
            if let Some(v) = p.provider.as_deref().and_then(Provider::parse) {
                s.proveedor = Some(v.name().to_string());
                tr.set_preferred(v);
            }
            if let Some(v) = p.google_fallback {
                s.google_de_respaldo = v;
                tr.set_google_fallback(v);
            }
            if let Some(v) = p.no_translate {
                let list = ProtectedTerms::parse_list(&v.join(","));
                if list != s.no_traducir {
                    tr.set_protected_terms(&list);
                    s.no_traducir = list;
                }
            }
            if let Some(v) = p.filter_system {
                s.filtrar_sistema = v;
            }
            if let Some(v) = p.send_delay_ms {
                s.retardo_envio_ms = v;
            }
            if let Some(v) = p.return_to_app {
                s.volver_al_traductor = v;
            }
            if let Some(v) = p.hotkey {
                s.tecla_rapida = v;
            }
            if let Some(v) = p.alert_private {
                s.avisar_privados = v;
            }
            if let Some(v) = p.theme.filter(|t| THEMES.contains(&t.as_str())) {
                s.tema = v;
            }
            if let Some(v) = p.always_on_top {
                s.siempre_encima = v;
            }
            if let Some(v) = p.compact {
                s.compacto = v;
            }
            if let Some(v) = p.inactive_opacity {
                s.opacidad_inactiva = v;
            }
            if let Some(v) = p.upper_case {
                s.mayusculas = v;
            }
            s.normalize();
        }
        self.schedule_save();
        self.emit_status();
        self.ui_settings()
    }

    pub async fn provider_info(&self, p: Provider) -> ProviderInfo {
        let tr = &self.inner.tr;
        let state = state_name(tr.state(p));
        match p {
            Provider::Microsoft => ProviderInfo {
                state,
                usage_count: Some(self.inner.settings.lock().unwrap().microsoft_usage_this_month().max(0) as u64),
                usage_limit: Some(MICROSOFT_FREE_CHARS),
            },
            Provider::DeepL => {
                if let Some(key) = tr.key(p) {
                    if let Ok(Ok(u)) = tokio::time::timeout(Duration::from_secs(6), tr.check_deepl_key(&key)).await {
                        *self.inner.deepl_usage.lock().unwrap() = Some(u);
                    }
                }
                let u = *self.inner.deepl_usage.lock().unwrap();
                ProviderInfo { state, usage_count: u.map(|x| x.0), usage_limit: u.map(|x| x.1).filter(|l| *l > 0) }
            }
            Provider::Google => ProviderInfo { state, usage_count: None, usage_limit: None },
        }
    }

    fn apply_key(&self, p: Provider, key: &str, region: Option<&str>) {
        {
            let mut s = self.inner.settings.lock().unwrap();
            let region = region.map(str::trim).filter(|r| !r.is_empty());
            if p == Provider::DeepL {
                s.deepl_key = Some(key.to_string());
            } else {
                s.microsoft_key = Some(key.to_string());
                s.microsoft_region = region.map(String::from);
            }
            s.proveedor = Some(p.name().to_string());
            self.inner.tr.set_key(p, Some(key), region);
            self.inner.tr.set_preferred(p);
        }
        self.save_now();
        self.emit_status();
    }

    pub async fn check_key(&self, p: Provider, key: &str, region: &str) -> KeyCheck {
        let key = key.trim();
        let fail = |message: String| KeyCheck { ok: false, message, usage_count: None, usage_limit: None };
        if p == Provider::Google {
            return fail("Google no necesita clave.".into());
        }
        if key.is_empty() {
            return fail("Pega la clave y pulsa Comprobar.".into());
        }
        let tr = &self.inner.tr;
        let result = if p == Provider::DeepL {
            tr.check_deepl_key(key).await.map(Some)
        } else {
            tr.check_microsoft_key(key, Some(region)).await.map(|_| None)
        };
        match result {
            Ok(usage) => {
                self.apply_key(p, key, Some(region));
                if let Some(u) = usage {
                    *self.inner.deepl_usage.lock().unwrap() = Some(u);
                }
                KeyCheck {
                    ok: true,
                    message: match p {
                        Provider::DeepL if key.to_ascii_lowercase().ends_with(":fx") => "✓ Clave válida, plan gratuito".into(),
                        Provider::DeepL => "✓ Clave válida".into(),
                        _ => "✓ Clave válida. Microsoft Translator listo.".into(),
                    },
                    usage_count: usage.map(|u| u.0),
                    usage_limit: usage.map(|u| u.1).filter(|l| *l > 0),
                }
            }
            Err(TrError::Unavailable { message, .. }) => {
                let hint = if p == Provider::Microsoft && region.trim().is_empty() {
                    " Si tu recurso no es global, escribe su región (por ejemplo westeurope)."
                } else {
                    ""
                };
                fail(format!("{} rechazó la clave: {message}.{hint}", p.name()))
            }
            Err(e) => {
                // Sin conexión para comprobarla: se guarda igualmente.
                self.apply_key(p, key, Some(region));
                KeyCheck {
                    ok: true,
                    message: format!("No se pudo comprobar ({e}). Se usará igualmente."),
                    usage_count: None,
                    usage_limit: None,
                }
            }
        }
    }
}

#[cfg(test)]
mod tests;
