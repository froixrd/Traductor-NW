//! Carpeta de usuario del juego (Documentos\Neverwinter Nights), donde están los logs y settings.tml.

use std::path::{Path, PathBuf};
use std::time::SystemTime;

/// Ubicaciones habituales, incluida la carpeta Documentos redirigida a OneDrive (también en español).
pub fn user_directories() -> Vec<PathBuf> {
    let mut list: Vec<PathBuf> = Vec::new();
    if let Some(docs) = dirs::document_dir() {
        list.push(docs.join("Neverwinter Nights"));
    }
    if let Some(home) = dirs::home_dir() {
        list.push(home.join("OneDrive").join("Documents").join("Neverwinter Nights"));
        list.push(home.join("OneDrive").join("Documentos").join("Neverwinter Nights"));
        list.push(home.join("Documents").join("Neverwinter Nights"));
    }
    let mut out: Vec<PathBuf> = Vec::new();
    for p in list {
        if !out.iter().any(|x| x.to_string_lossy().eq_ignore_ascii_case(&p.to_string_lossy())) {
            out.push(p);
        }
    }
    out
}

/// De varios archivos candidatos, el que se escribió más recientemente.
pub fn most_recent<I: IntoIterator<Item = PathBuf>>(paths: I) -> Option<PathBuf> {
    let mut best: Option<(PathBuf, SystemTime)> = None;
    for p in paths {
        let Ok(meta) = std::fs::metadata(&p) else { continue };
        if !meta.is_file() {
            continue;
        }
        let t = meta.modified().unwrap_or(SystemTime::UNIX_EPOCH);
        if best.as_ref().is_none_or(|(_, bt)| t > *bt) {
            best = Some((p, t));
        }
    }
    best.map(|(p, _)| p)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ChatLogging {
    Enabled,
    Disabled,
    Unknown,
}

pub const SECTION_ALL: &str = "[game.log.chat.all]";
pub const SECTION_TEXT: &str = "[game.log.chat.text]";

/// NWN:EE solo escribe el chat en el log si está activado en settings.tml
/// (`[game.log.chat.all]` con `enabled = true`). Sin eso, el traductor no recibe nada.
pub fn check_chat_logging(dirs: &[PathBuf]) -> (ChatLogging, Option<PathBuf>) {
    let Some(path) = most_recent(dirs.iter().map(|d| d.join("settings.tml"))) else {
        return (ChatLogging::Unknown, None);
    };
    match std::fs::read(&path) {
        Ok(bytes) => {
            let text = String::from_utf8_lossy(&bytes);
            let state = if parse_enabled(&text, SECTION_ALL) == Some(true) { ChatLogging::Enabled } else { ChatLogging::Disabled };
            (state, Some(path))
        }
        Err(_) => (ChatLogging::Unknown, Some(path)),
    }
}

/// Valor de "enabled" en la sección indicada; `None` si no aparece.
pub fn parse_enabled(toml: &str, section: &str) -> Option<bool> {
    let mut in_section = false;
    for raw in toml.split('\n') {
        let mut line = raw.trim();
        if let Some(hash) = line.find('#') {
            line = line[..hash].trim();
        }
        if line.is_empty() {
            continue;
        }
        if line.starts_with('[') {
            in_section = line.eq_ignore_ascii_case(section);
            continue;
        }
        if !in_section {
            continue;
        }
        let Some((k, v)) = line.split_once('=') else { continue };
        if k.trim().eq_ignore_ascii_case("enabled") {
            return Some(v.trim().eq_ignore_ascii_case("true"));
        }
    }
    None
}

pub fn log_candidates(dirs: &[PathBuf]) -> Vec<PathBuf> {
    dirs.iter().map(|d| d.join("logs").join("nwclientLog1.txt")).collect()
}

pub fn display(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lee_settings_tml() {
        let toml = "[game.log.chat.text]\nenabled = false\n\n[game.log.chat.all]\n# comentario\nenabled = true # sí\n";
        assert_eq!(parse_enabled(toml, SECTION_ALL), Some(true));
        assert_eq!(parse_enabled(toml, SECTION_TEXT), Some(false));
        assert_eq!(parse_enabled("[otra]\nenabled=true", SECTION_ALL), None);

        let dir = tempfile::tempdir().unwrap();
        let d = dir.path().to_path_buf();
        assert_eq!(check_chat_logging(&[d.clone()]).0, ChatLogging::Unknown);
        std::fs::write(d.join("settings.tml"), "[game.log.chat.all]\nenabled = false\n").unwrap();
        assert_eq!(check_chat_logging(&[d]).0, ChatLogging::Disabled);
    }
}
