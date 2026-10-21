//! Configuración en %AppData%\TraductorNWN\config.json, el mismo archivo que la versión anterior:
//! al cambiar de versión se conservan las claves, la paleta, las palabras protegidas, etc.
//! Las claves de DeepL y Microsoft se guardan cifradas con DPAPI (solo tu usuario de Windows
//! puede leerlas), con la misma "entropía" que antes para poder leer las ya guardadas.

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::path::{Path, PathBuf};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct Settings {
    pub version: i32,
    pub deep_l_key_cifrada: Option<String>,
    pub mayusculas: bool,
    pub filtrar_sistema: bool,
    pub retardo_envio_ms: i32,
    pub ventana: Option<Vec<i32>>,
    pub tema: String,
    pub proveedor: Option<String>,
    pub siempre_encima: bool,
    pub volver_al_traductor: bool,
    pub microsoft_key_cifrada: Option<String>,
    pub microsoft_region: Option<String>,
    pub google_de_respaldo: bool,
    pub no_traducir: Vec<String>,
    pub avisar_privados: bool,
    pub tecla_rapida: bool,
    pub compacto: bool,
    pub opacidad_inactiva: i32,
    pub microsoft_mes: Option<String>,
    pub microsoft_caracteres: i64,

    /// Lo que no conoce esta versión se conserva tal cual al guardar.
    #[serde(flatten)]
    pub extra: Map<String, Value>,

    #[serde(skip)]
    pub microsoft_key: Option<String>,
    #[serde(skip)]
    pub deepl_key: Option<String>,
    /// En este arranque se pasó a Microsoft como traductor principal.
    #[serde(skip)]
    pub migrado_a_microsoft: bool,
}

const CURRENT_VERSION: i32 = 2;

impl Default for Settings {
    fn default() -> Self {
        Self {
            version: 0,
            deep_l_key_cifrada: None,
            mayusculas: false,
            filtrar_sistema: true,
            retardo_envio_ms: 2000,
            ventana: None,
            tema: "taberna".into(),
            proveedor: None,
            siempre_encima: true,
            volver_al_traductor: true,
            microsoft_key_cifrada: None,
            microsoft_region: None,
            google_de_respaldo: true,
            no_traducir: Vec::new(),
            avisar_privados: true,
            tecla_rapida: true,
            compacto: false,
            opacidad_inactiva: 100,
            microsoft_mes: None,
            microsoft_caracteres: 0,
            extra: Map::new(),
            microsoft_key: None,
            deepl_key: None,
            migrado_a_microsoft: false,
        }
    }
}

pub fn folder() -> PathBuf {
    dirs::config_dir().unwrap_or_else(std::env::temp_dir).join("TraductorNWN")
}

pub const THEMES: [&str; 4] = ["taberna", "pizarra", "arcano", "bosque"];

impl Settings {
    pub fn load() -> Self {
        Self::load_from(&folder().join("config.json"))
    }

    pub fn load_from(path: &Path) -> Self {
        let mut s: Settings = std::fs::read_to_string(path)
            .ok()
            .and_then(|t| serde_json::from_str(t.trim_start_matches('\u{feff}')).ok())
            .unwrap_or_default();
        s.deepl_key = s.deep_l_key_cifrada.as_deref().and_then(unprotect);
        s.microsoft_key = s.microsoft_key_cifrada.as_deref().and_then(unprotect);
        s.normalize();
        s.migrate();
        s
    }

    pub fn normalize(&mut self) {
        self.retardo_envio_ms = self.retardo_envio_ms.clamp(0, 5000);
        self.opacidad_inactiva = self.opacidad_inactiva.clamp(40, 100);
        if !THEMES.contains(&self.tema.as_str()) {
            self.tema = "taberna".into();
        }
    }

    /// Microsoft Translator pasa a ser el traductor principal (gratis hasta 2 millones de caracteres
    /// al mes) y Google el respaldo. Solo se respeta DeepL si ya tenía una clave guardada.
    fn migrate(&mut self) {
        if self.version < CURRENT_VERSION {
            let p = self.proveedor.as_deref().unwrap_or("");
            let keep_deepl = p.eq_ignore_ascii_case("deepl") && self.deepl_key.is_some();
            if !keep_deepl && !p.eq_ignore_ascii_case("microsoft") {
                self.proveedor = Some("Microsoft".into());
                self.migrado_a_microsoft = true;
            }
            self.version = CURRENT_VERSION;
        }
        if self.proveedor.is_none() {
            self.proveedor = Some("Microsoft".into());
        }
    }

    fn month(now: chrono::DateTime<chrono::Local>) -> String {
        now.format("%Y-%m").to_string()
    }

    /// Suma caracteres al contador del mes (se reinicia solo al cambiar de mes).
    pub fn add_microsoft_usage(&mut self, chars: usize) {
        let month = Self::month(chrono::Local::now());
        if self.microsoft_mes.as_deref() != Some(month.as_str()) {
            self.microsoft_mes = Some(month);
            self.microsoft_caracteres = 0;
        }
        self.microsoft_caracteres += chars as i64;
    }

    pub fn microsoft_usage_this_month(&self) -> i64 {
        if self.microsoft_mes.as_deref() == Some(Self::month(chrono::Local::now()).as_str()) {
            self.microsoft_caracteres
        } else {
            0
        }
    }

    pub fn save(&mut self) -> std::io::Result<()> {
        self.save_to(&folder().join("config.json"))
    }

    pub fn save_to(&mut self, path: &Path) -> std::io::Result<()> {
        self.deep_l_key_cifrada = self.deepl_key.as_deref().filter(|k| !k.is_empty()).and_then(protect);
        self.microsoft_key_cifrada = self.microsoft_key.as_deref().filter(|k| !k.is_empty()).and_then(protect);
        let json = serde_json::to_string_pretty(self).map_err(std::io::Error::other)?;
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = path.with_extension("json.tmp");
        std::fs::write(&tmp, json)?;
        std::fs::rename(&tmp, path)
    }
}

// ---------- Cifrado de las claves ----------

const ENTROPY: &[u8] = b"TraductorNWN.v1";

#[cfg(windows)]
fn protect(plain: &str) -> Option<String> {
    use base64::Engine;
    dpapi::run(plain.as_bytes(), true).map(|b| base64::engine::general_purpose::STANDARD.encode(b))
}

#[cfg(windows)]
fn unprotect(cipher: &str) -> Option<String> {
    use base64::Engine;
    let bytes = base64::engine::general_purpose::STANDARD.decode(cipher).ok()?;
    dpapi::run(&bytes, false).and_then(|b| String::from_utf8(b).ok())
}

#[cfg(windows)]
mod dpapi {
    use super::ENTROPY;
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB};

    const UI_FORBIDDEN: u32 = 0x1; // nunca mostrar ventanas de Windows al cifrar o descifrar

    pub fn run(input: &[u8], encrypt: bool) -> Option<Vec<u8>> {
        let data_in = CRYPT_INTEGER_BLOB { cbData: input.len() as u32, pbData: input.as_ptr() as *mut u8 };
        let entropy = CRYPT_INTEGER_BLOB { cbData: ENTROPY.len() as u32, pbData: ENTROPY.as_ptr() as *mut u8 };
        let mut out = CRYPT_INTEGER_BLOB { cbData: 0, pbData: std::ptr::null_mut() };
        // SAFETY: estructuras válidas durante la llamada; la memoria de salida se libera con LocalFree.
        let ok = unsafe {
            if encrypt {
                CryptProtectData(&data_in, std::ptr::null(), &entropy, std::ptr::null(), std::ptr::null(), UI_FORBIDDEN, &mut out)
            } else {
                CryptUnprotectData(&data_in, std::ptr::null_mut(), &entropy, std::ptr::null(), std::ptr::null(), UI_FORBIDDEN, &mut out)
            }
        };
        if ok == 0 || out.pbData.is_null() {
            return None;
        }
        // SAFETY: Windows devuelve cbData bytes en pbData.
        let v = unsafe { std::slice::from_raw_parts(out.pbData, out.cbData as usize).to_vec() };
        unsafe { LocalFree(out.pbData as _) };
        Some(v)
    }
}

// Fuera de Windows (solo pruebas): base64 sin cifrar.
#[cfg(not(windows))]
fn protect(plain: &str) -> Option<String> {
    use base64::Engine;
    let _ = ENTROPY;
    Some(base64::engine::general_purpose::STANDARD.encode(plain))
}

#[cfg(not(windows))]
fn unprotect(cipher: &str) -> Option<String> {
    use base64::Engine;
    String::from_utf8(base64::engine::general_purpose::STANDARD.decode(cipher).ok()?).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lee_la_configuracion_de_la_version_anterior_y_conserva_lo_desconocido() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("config.json");
        std::fs::write(
            &path,
            r#"{"Version":2,"Mayusculas":true,"FiltrarSistema":false,"RetardoEnvioMs":9000,"Tema":"arcano",
               "Proveedor":"DeepL","NoTraducir":["Kael"],"OpacidadInactiva":10,"Ventana":[10,20,700,800],
               "MicrosoftKeyCifrada":"Y2xhdmU=","CampoFuturo":{"a":1}}"#,
        )
        .unwrap();
        let mut s = Settings::load_from(&path);
        assert!(s.mayusculas && !s.filtrar_sistema);
        assert_eq!(s.retardo_envio_ms, 5000);
        assert_eq!(s.opacidad_inactiva, 40);
        assert_eq!(s.tema, "arcano");
        assert_eq!(s.proveedor.as_deref(), Some("DeepL"));
        #[cfg(not(windows))]
        assert_eq!(s.microsoft_key.as_deref(), Some("clave"));
        assert_eq!(s.ventana, Some(vec![10, 20, 700, 800]));
        s.add_microsoft_usage(10);
        s.save_to(&path).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("\"CampoFuturo\"") && text.contains("\"MicrosoftCaracteres\": 10"));
        assert!(!text.contains("\"clave\""));
    }

    #[test]
    fn las_claves_se_guardan_cifradas_y_se_vuelven_a_leer() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("config.json");
        let mut s = Settings::load_from(&path);
        s.microsoft_key = Some("mi-clave-secreta".into());
        s.deepl_key = Some("otra:fx".into());
        s.save_to(&path).unwrap();
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(!text.contains("mi-clave-secreta") && !text.contains("otra:fx"));
        let s = Settings::load_from(&path);
        assert_eq!(s.microsoft_key.as_deref(), Some("mi-clave-secreta"));
        assert_eq!(s.deepl_key.as_deref(), Some("otra:fx"));
    }

    #[test]
    fn migra_a_microsoft() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("config.json");
        std::fs::write(&path, r#"{"Proveedor":"Google"}"#).unwrap();
        let s = Settings::load_from(&path);
        assert_eq!(s.proveedor.as_deref(), Some("Microsoft"));
        assert!(s.migrado_a_microsoft);
        let s = Settings::load_from(&dir.path().join("no-existe.json"));
        assert_eq!(s.proveedor.as_deref(), Some("Microsoft"));
        assert!(s.filtrar_sistema && s.siempre_encima && s.retardo_envio_ms == 2000);
    }
}
