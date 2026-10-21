//! Lo que el núcleo le cuenta a la interfaz (mismos nombres que en src/types.ts).

use serde::Serialize;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LineEvent {
    pub id: u64,
    pub kind: &'static str, // "chat" | "system"
    pub time: String,
    pub speaker: Option<String>,
    pub channel: Option<String>,
    pub text: String,
    /// false = combate o sistema con "Solo chat de jugadores": no se traduce.
    pub translate: bool,
    /// Es el eco en el log de un mensaje que acabas de enviar.
    pub echo: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TranslationEvent {
    pub id: u64,
    pub text: String,
    pub untranslated: Option<String>,
    pub echo: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum OwnStatus {
    Traduciendo,
    Esperando,
    Cola,
    Escribiendo,
    Confirmado,
    Enviado,
    SinConfirmar,
    Fallido,
    Cancelado,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OwnEvent {
    pub id: u64,
    pub es: String,
    pub en: Option<String>,
    pub status: OwnStatus,
    pub delay_ms: Option<u64>,
    pub raw: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Tone {
    Info,
    Ok,
    Warn,
    Accent,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NoticeEvent {
    pub id: u64,
    pub text: String,
    pub tone: Tone,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StatusEvent {
    pub active: Option<&'static str>,
    pub note: String,
    pub note_warn: bool,
    pub cooldown_s: f64,
    pub pending: usize,
    pub log_found: bool,
    pub chain: Vec<&'static str>,
    pub google_in_chain: bool,
}

/// La interfaz. Todas las llamadas llegan desde tareas de fondo.
pub trait Ui: Send + Sync + 'static {
    fn line(&self, e: LineEvent);
    fn translation(&self, e: TranslationEvent);
    fn own(&self, e: OwnEvent);
    fn notice(&self, e: NoticeEvent);
    fn status(&self, e: StatusEvent);
    /// Un mensaje que no pudo salir vuelve al campo de texto (si está vacío).
    fn restore(&self, text: String);
    /// Llegó un mensaje privado: sonido y parpadeo en la barra de tareas.
    fn alert_private(&self);
}

/// El juego: escribir un mensaje en su chat.
pub trait Game: Send + Sync + 'static {
    /// Bloqueante. `Err` con el motivo si no se pudo enviar.
    fn send(&self, text: &str, return_to_app: bool) -> Result<(), String>;
    /// ¿Tienes pulsada Alt, Ctrl o Mayús? (con alguna pulsada, el Enter haría otra cosa en el juego).
    fn modifiers_down(&self) -> bool;
}
