//! Preparación del texto antes de traducirlo:
//!  - abreviaturas del chat en inglés escritas completas (solo para el traductor; en pantalla
//!    se sigue viendo el original);
//!  - palabras que nunca se traducen (nombres, lugares, jerga del servidor).

use regex::{Regex, RegexBuilder};
use std::collections::HashMap;
use std::sync::LazyLock;

// Términos de juego que se entienden igual en español (afk, lol, gg, ooc, rp, pvp, hp…) no se tocan.
const SLANG: &[(&str, &str)] = &[
    ("u", "you"), ("ur", "your"), ("r", "are"), ("ya", "you"), ("yr", "your"),
    ("pls", "please"), ("plz", "please"), ("plox", "please"),
    ("thx", "thanks"), ("thnx", "thanks"), ("tnx", "thanks"), ("ty", "thank you"), ("tyvm", "thank you very much"),
    ("np", "no problem"), ("yw", "you're welcome"),
    ("idk", "I don't know"), ("dunno", "don't know"), ("idc", "I don't care"),
    ("imo", "in my opinion"), ("imho", "in my opinion"), ("btw", "by the way"), ("tbh", "to be honest"),
    ("omw", "on my way"), ("brb", "be right back"), ("bbl", "be back later"), ("bbiab", "be back in a bit"),
    ("gtg", "got to go"), ("g2g", "got to go"), ("cya", "see you"), ("cu", "see you"),
    ("ofc", "of course"), ("rly", "really"), ("ppl", "people"), ("b4", "before"), ("nvm", "never mind"),
    ("wb", "welcome back"), ("gl", "good luck"), ("hf", "have fun"), ("gj", "good job"), ("wp", "well played"),
    ("k", "okay"), ("kk", "okay"), ("msg", "message"), ("lvl", "level"), ("sry", "sorry"), ("srry", "sorry"),
    ("gonna", "going to"), ("wanna", "want to"), ("gotta", "got to"), ("lemme", "let me"), ("gimme", "give me"),
    ("im", "I'm"), ("ive", "I've"), ("dont", "don't"), ("cant", "can't"), ("didnt", "didn't"),
    ("doesnt", "doesn't"), ("isnt", "isn't"), ("wasnt", "wasn't"), ("thats", "that's"), ("whats", "what's"),
    ("youre", "you're"), ("theyre", "they're"), ("wouldnt", "wouldn't"), ("couldnt", "couldn't"), ("shouldnt", "shouldn't"),
];

static SLANG_MAP: LazyLock<HashMap<&'static str, &'static str>> = LazyLock::new(|| SLANG.iter().copied().collect());
static WORD: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[\p{L}\p{N}'’]+").unwrap());

/// Escribe completas las abreviaturas de chat ("u" → "you", "ty" → "thank you").
/// Palabra completa: sin letras, dígitos ni apóstrofos pegados, y sin un punto y una letra
/// detrás (para no tocar "U.S.").
pub fn expand_slang(text: &str) -> String {
    let mut out = String::with_capacity(text.len() + 16);
    let mut last = 0;
    for m in WORD.find_iter(text) {
        let lower = m.as_str().to_lowercase();
        let Some(full) = SLANG_MAP.get(lower.as_str()) else { continue };
        let mut after = text[m.end()..].chars();
        if after.next() == Some('.') && after.next().is_some_and(|c| c.is_alphabetic()) {
            continue;
        }
        out.push_str(&text[last..m.start()]);
        out.push_str(full);
        last = m.end();
    }
    out.push_str(&text[last..]);
    out
}

/// Palabras que el usuario quiere que no se traduzcan nunca. Antes de traducir se cambian por
/// marcadores {0}, {1}… que los traductores respetan, y después se vuelven a poner tal cual.
#[derive(Default)]
pub struct ProtectedTerms {
    regex: Option<Regex>,
}

static PLACEHOLDER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\{\s*(\d{1,2})\s*\}").unwrap());

fn is_word_char(c: char) -> bool {
    c.is_alphanumeric() || c == '_'
}

impl ProtectedTerms {
    pub fn new<I: IntoIterator<Item = S>, S: AsRef<str>>(terms: I) -> Self {
        let mut list: Vec<String> = Vec::new();
        for t in terms {
            let t = t.as_ref().trim();
            if !t.is_empty() && !list.iter().any(|x| x.to_lowercase() == t.to_lowercase()) {
                list.push(t.to_string());
            }
        }
        if list.is_empty() {
            return Self { regex: None };
        }
        list.sort_by_key(|t| std::cmp::Reverse(t.chars().count()));
        let pattern = list.iter().map(|t| regex::escape(t)).collect::<Vec<_>>().join("|");
        let regex = RegexBuilder::new(&format!("(?:{pattern})")).case_insensitive(true).build().ok();
        Self { regex }
    }

    /// Separa una lista escrita por el usuario: "Seraphine, Kael; Aguas Profundas".
    pub fn parse_list(text: &str) -> Vec<String> {
        let mut out: Vec<String> = Vec::new();
        for t in text.split([',', ';', '\n']) {
            let t = t.trim();
            if !t.is_empty() && !out.iter().any(|x| x.to_lowercase() == t.to_lowercase()) {
                out.push(t.to_string());
            }
        }
        out
    }

    pub fn protect(&self, text: &str) -> (String, Option<Vec<String>>) {
        let Some(re) = &self.regex else { return (text.to_string(), None) };
        let mut tokens: Vec<String> = Vec::new();
        let mut out = String::with_capacity(text.len());
        let mut last = 0;
        let mut pos = 0;
        while pos <= text.len() {
            let Some(m) = re.find_at(text, pos) else { break };
            let before_ok = !text[..m.start()].chars().next_back().is_some_and(is_word_char);
            let after_ok = !text[m.end()..].chars().next().is_some_and(is_word_char);
            if before_ok && after_ok && !m.as_str().is_empty() && tokens.len() < 99 {
                out.push_str(&text[last..m.start()]);
                out.push_str(&format!("{{{}}}", tokens.len()));
                tokens.push(m.as_str().to_string());
                last = m.end();
                pos = m.end();
            } else {
                // Buscar otra coincidencia a partir del siguiente carácter.
                pos = m.start() + text[m.start()..].chars().next().map_or(1, |c| c.len_utf8());
            }
        }
        if tokens.is_empty() {
            return (text.to_string(), None);
        }
        out.push_str(&text[last..]);
        (out, Some(tokens))
    }

    pub fn restore(text: &str, tokens: &Option<Vec<String>>) -> String {
        let Some(tokens) = tokens else { return text.to_string() };
        PLACEHOLDER
            .replace_all(text, |c: &regex::Captures| {
                c[1].parse::<usize>()
                    .ok()
                    .and_then(|i| tokens.get(i).cloned())
                    .unwrap_or_else(|| c[0].to_string())
            })
            .into_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abreviaturas() {
        assert_eq!(expand_slang("ty u r great, brb"), "thank you you are great, be right back");
        assert_eq!(expand_slang("I'm in the U.S. now"), "I'm in the U.S. now");
        assert_eq!(expand_slang("im here, dont go"), "I'm here, don't go");
        assert_eq!(expand_slang("lol gg"), "lol gg");
        assert_eq!(expand_slang("Ur turn"), "your turn");
    }

    #[test]
    fn palabras_protegidas() {
        let t = ProtectedTerms::new(["Seraphine", "Moonstone Mask", "Kael"]);
        let (p, tokens) = t.protect("Ask Seraphine at the moonstone mask, not Kaelith");
        assert_eq!(p, "Ask {0} at the {1}, not Kaelith");
        assert_eq!(ProtectedTerms::restore("Pregunta a {0} en la { 1 }", &tokens), "Pregunta a Seraphine en la moonstone mask");
        let (same, none) = t.protect("nothing here");
        assert_eq!(same, "nothing here");
        assert!(none.is_none());
        assert_eq!(ProtectedTerms::parse_list("a, B; b\nc,,"), vec!["a", "B", "c"]);
    }
}
