//! Sigue el log del cliente de NWN (nwclientLog1.txt) y entrega las líneas nuevas completas.
//!  - Lee el tamaño desde su propio manejador de archivo (no el de la carpeta, que puede ir desfasado).
//!  - Detecta la rotación del log al empezar una partida nueva comparando el identificador del
//!    archivo, en vez de volver a leer (y traducir) toda la sesión anterior.
//!  - Solo entrega líneas completas: una línea a medio escribir se guarda hasta que llegue el resto.

use crate::gamefiles;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

const POLL: Duration = Duration::from_millis(150);
const IDENTITY_CHECK: Duration = Duration::from_millis(1000);
const PARTIAL_FLUSH: Duration = Duration::from_millis(600);
// Tras una rotación solo se relee el log nuevo desde el principio si es pequeño (una sesión que
// acaba de empezar). Si es grande, se salta al final.
const MAX_REPLAY_BYTES: u64 = 64 * 1024;
const MAX_LINE_BYTES: usize = 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
struct FileId(u64, u64);

impl FileId {
    fn is_empty(&self) -> bool {
        self.0 == 0 && self.1 == 0
    }
}

#[cfg(windows)]
fn file_id(f: &File) -> FileId {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{GetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION};
    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: manejador válido mientras vive `f`.
    if unsafe { GetFileInformationByHandle(f.as_raw_handle() as _, &mut info) } != 0 {
        FileId(info.dwVolumeSerialNumber as u64, ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64)
    } else {
        FileId::default()
    }
}

#[cfg(unix)]
fn file_id(f: &File) -> FileId {
    use std::os::unix::fs::MetadataExt;
    f.metadata().map(|m| FileId(m.dev(), m.ino())).unwrap_or_default()
}

/// Windows-1252 = Latin1 + comillas tipográficas, €, … (lo que escribe el cliente en Windows).
fn decode(bytes: &[u8]) -> String {
    let bytes = bytes.strip_suffix(b"\r").unwrap_or(bytes);
    encoding_rs::WINDOWS_1252.decode_without_bom_handling(bytes).0.into_owned()
}

#[derive(Default)]
struct LineAssembler {
    buf: Vec<u8>,
}

impl LineAssembler {
    fn feed(&mut self, mut data: &[u8], lines: &mut Vec<String>) {
        while !data.is_empty() {
            match data.iter().position(|&b| b == b'\n') {
                None => {
                    self.append(data);
                    return;
                }
                Some(nl) => {
                    self.append(&data[..nl]);
                    lines.push(decode(&self.buf));
                    self.buf.clear();
                    data = &data[nl + 1..];
                }
            }
        }
    }

    fn append(&mut self, s: &[u8]) {
        if self.buf.len() + s.len() > MAX_LINE_BYTES {
            self.buf.clear(); // basura sin saltos de línea: se descarta
            return;
        }
        self.buf.extend_from_slice(s);
    }

    fn flush_partial(&mut self) -> Option<String> {
        if self.buf.is_empty() {
            return None;
        }
        let s = decode(&self.buf);
        self.buf.clear();
        Some(s)
    }
}

pub struct LogWatcher {
    candidates: Vec<PathBuf>,
    on_lines: Box<dyn FnMut(Vec<String>) + Send>,
    on_status: Box<dyn FnMut(&str, bool) + Send>,
}

impl LogWatcher {
    pub fn new(
        game_dirs: &[PathBuf],
        on_lines: impl FnMut(Vec<String>) + Send + 'static,
        on_status: impl FnMut(&str, bool) + Send + 'static,
    ) -> Self {
        Self { candidates: gamefiles::log_candidates(game_dirs), on_lines: Box::new(on_lines), on_status: Box::new(on_status) }
    }

    fn find(&self) -> Option<PathBuf> {
        gamefiles::most_recent(self.candidates.iter().cloned())
    }

    fn has_been_replaced(path: &Path, current: FileId) -> bool {
        if current.is_empty() {
            return false; // no se pudo identificar: mejor no arriesgarse a releer
        }
        // En plena rotación puede no existir un instante; se verá en la siguiente comprobación.
        File::open(path).map(|f| file_id(&f)).is_ok_and(|id| !id.is_empty() && id != current)
    }

    /// Bucle de lectura (bloqueante): termina cuando `stop` se pone a true.
    pub fn run(mut self, stop: Arc<AtomicBool>) {
        (self.on_status)("Buscando el log de Neverwinter Nights…", false);
        let mut file: Option<File> = None;
        let mut path: Option<PathBuf> = None;
        let mut id = FileId::default();
        let mut pos: u64 = 0;
        let mut first_open = true;
        let mut buffer = vec![0u8; 64 * 1024];
        let mut assembler = LineAssembler::default();
        let mut last_identity_check = Instant::now();
        let mut last_data = Instant::now();

        while !stop.load(Ordering::Relaxed) {
            // ---------- (Re)abrir el log ----------
            if file.is_none() {
                let Some(p) = self.find() else {
                    std::thread::sleep(Duration::from_secs(1));
                    continue;
                };
                match File::open(&p).and_then(|f| f.metadata().map(|m| (f, m.len()))) {
                    Ok((f, len)) => {
                        let new_id = file_id(&f);
                        if first_open {
                            pos = len; // al arrancar no se traduce el historial
                            (self.on_status)("Log encontrado. Traduciendo en tiempo real.", true);
                        } else if !new_id.is_empty() && new_id == id {
                            pos = pos.min(len); // el mismo archivo tras un error de lectura: se sigue donde se iba
                        } else {
                            pos = if len <= MAX_REPLAY_BYTES { 0 } else { len };
                            assembler = LineAssembler::default();
                            (self.on_status)("Partida nueva detectada; siguiendo el log nuevo.", false);
                        }
                        id = new_id;
                        first_open = false;
                        last_identity_check = Instant::now();
                        file = Some(f);
                        path = Some(p);
                    }
                    Err(_) => {
                        std::thread::sleep(Duration::from_secs(1));
                        continue;
                    }
                }
            }

            // ---------- Leer lo nuevo ----------
            let f = file.as_mut().unwrap();
            let mut read_something = false;
            let result: std::io::Result<()> = (|| {
                let len = f.metadata()?.len();
                if len < pos {
                    // Truncado en el sitio: se empieza desde el principio del contenido nuevo.
                    pos = if len <= MAX_REPLAY_BYTES { 0 } else { len };
                    assembler = LineAssembler::default();
                }
                if len > pos {
                    f.seek(SeekFrom::Start(pos))?;
                    let mut lines = Vec::new();
                    while pos < len {
                        let want = buffer.len().min((len - pos) as usize);
                        let n = f.read(&mut buffer[..want])?;
                        if n == 0 {
                            break;
                        }
                        pos += n as u64;
                        assembler.feed(&buffer[..n], &mut lines);
                    }
                    if !lines.is_empty() {
                        (self.on_lines)(lines);
                    }
                    read_something = true;
                    last_data = Instant::now();
                }
                Ok(())
            })();
            if result.is_err() {
                file = None;
                continue;
            }
            if read_something {
                continue; // puede haber más; se vuelve a mirar sin esperar
            }

            // Una línea sin salto final que lleva un rato quieta se entrega igualmente.
            if last_data.elapsed() >= PARTIAL_FLUSH {
                if let Some(partial) = assembler.flush_partial() {
                    (self.on_lines)(vec![partial]);
                }
            }

            // ¿Sigue siendo el mismo archivo el que hay en la ruta? (rotación del log)
            if last_identity_check.elapsed() >= IDENTITY_CHECK {
                last_identity_check = Instant::now();
                if path.as_deref().is_some_and(|p| Self::has_been_replaced(p, id)) {
                    file = None;
                    continue;
                }
            }

            std::thread::sleep(POLL);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;
    use std::sync::Mutex;

    fn wait_for(lines: &Arc<Mutex<Vec<String>>>, n: usize) {
        let start = Instant::now();
        while lines.lock().unwrap().len() < n && start.elapsed() < Duration::from_secs(5) {
            std::thread::sleep(Duration::from_millis(20));
        }
    }

    #[test]
    fn sigue_el_log_y_la_rotacion() {
        let dir = tempfile::tempdir().unwrap();
        let game = dir.path().to_path_buf();
        std::fs::create_dir_all(game.join("logs")).unwrap();
        let log = game.join("logs").join("nwclientLog1.txt");
        std::fs::write(&log, "historial viejo\n").unwrap();

        let lines = Arc::new(Mutex::new(Vec::<String>::new()));
        let l2 = lines.clone();
        let found = Arc::new(AtomicBool::new(false));
        let f2 = found.clone();
        let w = LogWatcher::new(&[game.clone()], move |v| l2.lock().unwrap().extend(v), move |_, ok| {
            if ok {
                f2.store(true, Ordering::SeqCst)
            }
        });
        let stop = Arc::new(AtomicBool::new(false));
        let s2 = stop.clone();
        let t = std::thread::spawn(move || w.run(s2));

        while !found.load(Ordering::SeqCst) {
            std::thread::sleep(Duration::from_millis(20));
        }
        let mut f = std::fs::OpenOptions::new().append(true).open(&log).unwrap();
        // cp1252: 0x93/0x94 son comillas tipográficas, 0xF1 es ñ. Una línea llega en dos trozos.
        f.write_all(b"[CHAT WINDOW TEXT] Kael: [Talk] \x93a\xf1o\x94\r\nmedia ").unwrap();
        f.flush().unwrap();
        std::thread::sleep(Duration::from_millis(200));
        f.write_all(b"linea\n").unwrap();
        f.flush().unwrap();
        wait_for(&lines, 2);
        assert_eq!(*lines.lock().unwrap(), vec!["[CHAT WINDOW TEXT] Kael: [Talk] “año”", "media linea"]);

        // Partida nueva: el juego sustituye el archivo. Se lee desde el principio (es pequeño).
        drop(f);
        let tmp = game.join("logs").join("nuevo.tmp");
        std::fs::write(&tmp, "nueva sesion\n").unwrap();
        std::fs::rename(&tmp, &log).unwrap();
        wait_for(&lines, 3);
        assert_eq!(lines.lock().unwrap().last().unwrap(), "nueva sesion");
        assert!(!lines.lock().unwrap().iter().any(|l| l.contains("historial")));

        stop.store(true, Ordering::SeqCst);
        t.join().unwrap();
    }
}
