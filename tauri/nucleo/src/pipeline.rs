//! Cola de traducción de las líneas que llegan del juego.
//!  - Una sola tarea las traduce, así los mensajes salen SIEMPRE en el orden en que llegaron.
//!  - Mientras el traductor está en pausa (429) no se bloquea nada: las líneas se siguen
//!    acumulando y, al acabar la pausa, van todas juntas en una sola petición.
//!  - Si una línea lleva demasiado esperando (traductor caído o bloqueado mucho rato), se entrega
//!    sin traducir para que el chat no se quede atrás respecto al juego.

use crate::parser::ParsedLine;
use crate::translate::{TrError, Translator};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::mpsc;
use tokio_util::sync::CancellationToken;

#[derive(Clone, Debug)]
pub struct Item {
    pub id: u64,
    pub line: ParsedLine,
    pub echo: bool,
    enqueued: Instant,
}

pub trait Output: Send + Sync + 'static {
    fn translated(&self, item: &Item, text: String);
    fn untranslated(&self, item: &Item, reason: String);
    fn notice(&self, text: String, warn: bool);
}

#[derive(Clone)]
pub struct Pipeline {
    tx: mpsc::UnboundedSender<Item>,
    pending: Arc<AtomicUsize>,
}

pub struct Runner {
    rx: mpsc::UnboundedReceiver<Item>,
    pending: Arc<AtomicUsize>,
    max_wait: Duration,
    max_batch: usize,
}

pub fn new(max_wait: Duration, max_batch: usize) -> (Pipeline, Runner) {
    let (tx, rx) = mpsc::unbounded_channel();
    let pending = Arc::new(AtomicUsize::new(0));
    (Pipeline { tx, pending: pending.clone() }, Runner { rx, pending, max_wait, max_batch })
}

impl Pipeline {
    /// Mensajes recibidos que aún no se han mostrado traducidos.
    pub fn pending(&self) -> usize {
        self.pending.load(Ordering::Relaxed)
    }

    pub fn enqueue(&self, id: u64, line: ParsedLine, echo: bool) {
        self.pending.fetch_add(1, Ordering::Relaxed);
        let _ = self.tx.send(Item { id, line, echo, enqueued: Instant::now() });
    }
}

impl Runner {
    fn drain(&mut self, batch: &mut Vec<Item>) {
        while let Ok(item) = self.rx.try_recv() {
            batch.push(item);
        }
    }

    fn remove(&self, batch: &mut Vec<Item>, count: usize) {
        batch.drain(..count);
        self.pending.fetch_sub(count, Ordering::Relaxed);
    }

    /// Entrega los primeros `count` sin traducir (o traducidos si ya estaban en la caché).
    fn flush_untranslated(&self, batch: &mut Vec<Item>, count: usize, reason: &str, tr: &Translator, out: &dyn Output) {
        for item in &batch[..count] {
            match tr.try_get_cached(&item.line.text, "en", "es") {
                Some(c) => out.translated(item, c),
                None => out.untranslated(item, reason.to_string()),
            }
        }
        self.remove(batch, count);
    }

    fn flush_stale(&self, batch: &mut Vec<Item>, tr: &Translator, out: &dyn Output) {
        let n = batch.iter().take_while(|i| i.enqueued.elapsed() > self.max_wait).count();
        if n > 0 {
            let reason = format!(
                "Llevaba más de {} s esperando; se muestra el original para no quedarte atrás.",
                self.max_wait.as_secs()
            );
            self.flush_untranslated(batch, n, &reason, tr, out);
        }
    }

    pub async fn run(mut self, tr: Arc<Translator>, out: Arc<dyn Output>, cancel: CancellationToken) {
        let mut batch: Vec<Item> = Vec::new();
        let mut failures = 0;
        let mut limited = false;
        let mut no_provider = false;

        loop {
            if batch.is_empty() {
                tokio::select! {
                    _ = cancel.cancelled() => return,
                    item = self.rx.recv() => match item {
                        Some(i) => batch.push(i),
                        None => return,
                    },
                }
            }
            self.drain(&mut batch);

            // Esperar a que el traductor acepte peticiones, sin dejar de recoger líneas
            // ni de soltar las que llevan demasiado esperando.
            while !batch.is_empty() {
                self.flush_stale(&mut batch, &tr, out.as_ref());
                let wait = tr.time_until_ready();
                if wait.is_zero() {
                    break;
                }
                tokio::select! {
                    _ = cancel.cancelled() => return,
                    _ = tokio::time::sleep(wait.min(Duration::from_millis(250))) => {}
                }
                self.drain(&mut batch);
            }
            if batch.is_empty() {
                continue;
            }

            // Pequeña ventana para que una ráfaga de líneas vaya en la misma petición.
            if batch.len() < 4 {
                tokio::time::sleep(Duration::from_millis(40)).await;
                self.drain(&mut batch);
            }

            let take = batch.len().min(self.max_batch);
            let texts: Vec<String> = batch[..take].iter().map(|i| i.line.text.clone()).collect();
            let result = tokio::select! {
                _ = cancel.cancelled() => return,
                r = tr.translate(&texts, "en", "es") => r,
            };
            match result {
                Ok(results) => {
                    for (item, text) in batch[..take].iter().zip(results) {
                        out.translated(item, text);
                    }
                    self.remove(&mut batch, take);
                    failures = 0;
                    if limited || no_provider {
                        limited = false;
                        no_provider = false;
                        out.notice("Traducción reanudada.".into(), false);
                    }
                }
                Err(TrError::RateLimited { provider, retry_after }) => {
                    // El lote se queda en la cola y se reintenta al acabar la pausa.
                    if !limited {
                        limited = true;
                        let secs = retry_after.unwrap_or_else(|| tr.time_until_ready()).as_secs_f64().ceil();
                        out.notice(
                            format!(
                                "{} está limitando las peticiones. Lo vuelve a intentar en {secs} s y traduce de una vez todo lo que llegue mientras tanto.",
                                provider.name()
                            ),
                            true,
                        );
                    }
                }
                Err(TrError::Transient(msg)) => {
                    failures += 1;
                    if failures >= 3 {
                        self.flush_untranslated(&mut batch, take, "No se pudo conectar con el traductor.", &tr, out.as_ref());
                        out.notice(format!("No se pudo traducir: {msg}"), true);
                        failures = 0;
                    } else {
                        tokio::time::sleep(Duration::from_millis(300)).await;
                    }
                }
                Err(TrError::NoProvider(msg)) => {
                    // Ningún traductor disponible y Google desactivado: se muestran sin traducir (avisando una vez).
                    self.flush_untranslated(&mut batch, take, "No hay ningún traductor disponible.", &tr, out.as_ref());
                    if !no_provider {
                        no_provider = true;
                        out.notice(format!("{msg} Los mensajes se muestran sin traducir."), true);
                    }
                    failures = 0;
                }
                Err(e) => {
                    self.flush_untranslated(&mut batch, take, "El traductor devolvió un error.", &tr, out.as_ref());
                    out.notice(format!("Error de traducción: {e}"), true);
                    failures = 0;
                }
            }
        }
    }
}
