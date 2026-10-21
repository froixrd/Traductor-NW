//! Núcleo del Traductor NWN, sin nada de interfaz:
//!  - `logwatch`: sigue el log del juego (rotación, líneas a medio escribir);
//!  - `parser`: interpreta cada línea del chat;
//!  - `translate`: Microsoft / DeepL / Google con caché, límites y respaldo;
//!  - `pipeline`: la cola de lo que llega del juego;
//!  - `outgoing`: lo que escribes tú (comandos de chat, caracteres que NWN no sabe mostrar);
//!  - `engine`: lo une todo y habla con la interfaz y con el juego a través de dos traits.

pub mod engine;
pub mod events;
pub mod gamefiles;
pub mod gate;
pub mod logwatch;
pub mod outgoing;
pub mod parser;
pub mod pipeline;
pub mod settings;
pub mod terms;
pub mod translate;

#[cfg(test)]
mod testutil;
