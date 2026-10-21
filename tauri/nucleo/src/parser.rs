//! Interpreta las líneas del log de NWN:EE.
//!
//! Formato: `[CHAT WINDOW TEXT] [Sun Oct 11 14:35:00] Nombre: [Talk] mensaje`.
//! El segundo corchete (fecha y hora) es opcional; solo se toma como tal si contiene una hora.

use regex::Regex;
use std::sync::LazyLock;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LineKind {
    Chat,
    System,
}

/// Una línea del log ya interpretada.
///  - Chat: hora, quien habla y canal; `text` es el mensaje.
///  - System: combate, avisos del servidor… Solo `text` (y la hora si la hay).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ParsedLine {
    pub kind: LineKind,
    pub time: Option<String>,
    pub speaker: Option<String>,
    pub channel: Option<String>,
    pub text: String,
}

static PREFIX: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\[[^\]]*\]\s*(?:\[(?P<ts>[^\]]*\d:\d\d[^\]]*)\]\s*)?(?P<rest>.*)$").unwrap());
static SPEAKER: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^(?P<speaker>.+?):\s*\[(?P<channel>[^\]]+)\]\s?(?P<msg>.*)$").unwrap());
// Etiquetas de color del cliente, tipo <cÌ®þ>texto</c>.
static COLOR_TAG: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"</?c[^>]*>").unwrap());
static TIME: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(\d{1,2}:\d{2})(?::\d{2})?").unwrap());

pub fn strip_color_tags(text: &str) -> String {
    COLOR_TAG.replace_all(text, "").trim().to_string()
}

#[derive(Default)]
pub struct ChatParser {
    last: Option<ParsedLine>,
}

impl ChatParser {
    pub fn new() -> Self {
        Self::default()
    }

    /// `None` para líneas vacías o que no interesan (cabeceras del log).
    pub fn parse(&mut self, raw: &str) -> Option<ParsedLine> {
        let line = raw.trim();
        if line.is_empty() || line.contains("Messages for:") {
            return None;
        }

        let result = if !line.starts_with('[') {
            // Sin prefijo: continuación de un mensaje de varias líneas.
            let text = strip_color_tags(line);
            if text.is_empty() {
                return None;
            }
            match &self.last {
                Some(prev) if prev.kind == LineKind::Chat => ParsedLine { text, ..prev.clone() },
                _ => {
                    if text.contains("----") {
                        return None;
                    }
                    ParsedLine {
                        kind: LineKind::System,
                        time: self.last.as_ref().and_then(|l| l.time.clone()),
                        speaker: None,
                        channel: None,
                        text,
                    }
                }
            }
        } else {
            let mut time = None;
            let mut rest = line.to_string();
            if let Some(c) = PREFIX.captures(line) {
                rest = c["rest"].to_string();
                if let Some(ts) = c.name("ts") {
                    if let Some(t) = TIME.captures(ts.as_str()) {
                        time = Some(t[1].to_string());
                    }
                }
            }

            if let Some(c) = SPEAKER.captures(&rest) {
                let speaker = strip_color_tags(&c["speaker"]);
                let msg = strip_color_tags(&c["msg"]);
                if msg.is_empty() {
                    return None;
                }
                ParsedLine {
                    kind: LineKind::Chat,
                    time,
                    speaker: Some(speaker),
                    channel: Some(c["channel"].trim().to_string()),
                    text: msg,
                }
            } else {
                let text = strip_color_tags(&rest);
                if text.is_empty() || text.contains("----") {
                    return None;
                }
                ParsedLine { kind: LineKind::System, time, speaker: None, channel: None, text }
            }
        };

        self.last = Some(result.clone());
        Some(result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chat_con_hora_y_canal() {
        let mut p = ChatParser::new();
        let l = p.parse("[CHAT WINDOW TEXT] [Sun Oct 11 14:35:07] Kael Ironfist: [Party] hola grupo").unwrap();
        assert_eq!(l.kind, LineKind::Chat);
        assert_eq!(l.time.as_deref(), Some("14:35"));
        assert_eq!(l.speaker.as_deref(), Some("Kael Ironfist"));
        assert_eq!(l.channel.as_deref(), Some("Party"));
        assert_eq!(l.text, "hola grupo");
    }

    #[test]
    fn sin_hora_y_con_colores() {
        let mut p = ChatParser::new();
        let l = p.parse("[CHAT WINDOW TEXT] <cÌ®þ>Seraphine</c>: [Talk] <c>*sonríe*</c>").unwrap();
        assert_eq!(l.time, None);
        assert_eq!(l.speaker.as_deref(), Some("Seraphine"));
        assert_eq!(l.text, "*sonríe*");
    }

    #[test]
    fn sistema_y_continuacion() {
        let mut p = ChatParser::new();
        let s = p.parse("[CHAT WINDOW TEXT] [Sun Oct 11 14:35:00] Kael : Persuade : *success*").unwrap();
        assert_eq!(s.kind, LineKind::System);
        let c = p.parse("[CHAT WINDOW TEXT] [Sun Oct 11 14:36:00] Lyra: [Tell] primera línea").unwrap();
        assert_eq!(c.kind, LineKind::Chat);
        let cont = p.parse("segunda línea").unwrap();
        assert_eq!(cont.kind, LineKind::Chat);
        assert_eq!(cont.speaker.as_deref(), Some("Lyra"));
        assert_eq!(cont.text, "segunda línea");
        assert!(p.parse("   ").is_none());
        assert!(p.parse("[CHAT WINDOW TEXT] Messages for: x").is_none());
    }
}
