//! Lo que escribe el usuario antes de ir al juego.

use regex::Regex;
use std::sync::LazyLock;

/// Un mensaje separado en:
///  - `prefix`: el comando de chat que va delante y NO se traduce ("/p ", "/t \"Seraphine\" ");
///  - `body`: el texto que se traduce;
///  - `translate`: si hay que traducir el cuerpo;
///  - `is_chat`: si es un mensaje de chat (aparecerá en el log y se puede confirmar; admite mayúsculas).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OutgoingParts {
    pub prefix: String,
    pub body: String,
    pub translate: bool,
    pub is_chat: bool,
}

impl OutgoingParts {
    pub fn compose(&self, body: &str) -> String {
        format!("{}{}", self.prefix, body)
    }
}

// Comandos de canal del chat de NWN: el resto de la línea es un mensaje normal.
const CHANNEL_COMMANDS: &[&str] = &["s", "shout", "p", "party", "w", "whisper", "dm", "tk", "talk"];

// Mensaje privado: /t "Nombre con espacios" mensaje   ó   /t Nombre mensaje
static TELL: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?i)^(?P<prefix>/(?:t|tp|tell)\s+(?:"[^"]*"|\S+)\s+)(?P<body>.+)$"#).unwrap());
static COMMAND: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?s)^/(?P<cmd>\S+)(?:\s+(?P<body>.*))?$").unwrap());

pub fn split(text: &str) -> OutgoingParts {
    let text = text.trim();
    let raw = |t: &str| OutgoingParts { prefix: String::new(), body: t.to_string(), translate: false, is_chat: false };

    // "!comando": órdenes del servidor; van tal cual.
    if text.starts_with('!') {
        return raw(text);
    }

    if text.starts_with('/') {
        if let Some(c) = TELL.captures(text) {
            return OutgoingParts {
                prefix: c["prefix"].to_string(),
                body: c["body"].trim().to_string(),
                translate: true,
                is_chat: true,
            };
        }
        if let Some(c) = COMMAND.captures(text) {
            let cmd = &c["cmd"];
            if CHANNEL_COMMANDS.iter().any(|x| x.eq_ignore_ascii_case(cmd)) {
                let body = c.name("body").map_or("", |m| m.as_str()).trim();
                if body.is_empty() {
                    return raw(text);
                }
                return OutgoingParts { prefix: format!("/{cmd} "), body: body.to_string(), translate: true, is_chat: true };
            }
        }
        // Cualquier otro comando (/roll, /dice, comandos del servidor…) va tal cual.
        return raw(text);
    }

    OutgoingParts { prefix: String::new(), body: text.to_string(), translate: true, is_chat: true }
}

/// NWN muestra el chat con la página de códigos de Windows (cp1252): las comillas tipográficas,
/// guiones largos, emojis, etc. pueden salir como huecos o símbolos raros en el juego.
/// Se cambian por su equivalente simple y se quitan los que no tienen equivalente.
pub fn for_game(text: &str) -> String {
    let mut s = String::with_capacity(text.len());
    for c in text.chars() {
        match c {
            '‘' | '’' | '‚' | '′' => s.push('\''),
            '“' | '”' | '„' | '″' => s.push('"'),
            '–' | '—' | '−' => s.push('-'),
            '…' => s.push_str("..."),
            '\u{00A0}' | '\u{2009}' | '\u{202F}' | '\r' | '\n' | '\t' => s.push(' '),
            c if c.is_control() => {}
            c if (c as u32) < 0x100 || c == '€' => s.push(c), // ASCII, Latin-1 (ñ, tildes, ¿ ¡) y €
            _ => {}
        }
    }
    s.trim().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn comandos() {
        let p = split("/t \"Seraphine Moon\" nos vemos luego");
        assert_eq!(p.prefix, "/t \"Seraphine Moon\" ");
        assert_eq!(p.body, "nos vemos luego");
        assert!(p.translate && p.is_chat);

        let p = split("/p hola grupo");
        assert_eq!((p.prefix.as_str(), p.body.as_str(), p.translate), ("/p ", "hola grupo", true));

        let p = split("/roll 1d20");
        assert!(!p.translate && !p.is_chat);
        assert_eq!(p.body, "/roll 1d20");

        assert!(!split("!dado").translate);
        assert!(!split("/p").translate);
        let p = split("  hola  ");
        assert_eq!((p.prefix.as_str(), p.body.as_str()), ("", "hola"));
    }

    #[test]
    fn caracteres_para_el_juego() {
        assert_eq!(for_game("“Hola”—dijo… ¿qué tal? ñ €"), "\"Hola\"-dijo... ¿qué tal? ñ €");
        assert_eq!(for_game("emoji 😀 fuera\n"), "emoji  fuera");
    }
}
