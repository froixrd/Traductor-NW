//! Pruebas de principio a fin: un log de verdad en disco, un traductor falso por HTTP y un
//! "juego" falso que, como el de verdad, escribe en el log lo que se le envía.

use super::*;
use crate::testutil::*;
use std::io::Write;
use std::path::Path;

#[derive(Default)]
struct FakeUi {
    events: Mutex<Vec<String>>,
    lines: Mutex<Vec<LineEvent>>,
    translations: Mutex<Vec<TranslationEvent>>,
    own: Mutex<Vec<OwnEvent>>,
    notices: Mutex<Vec<NoticeEvent>>,
    restored: Mutex<Vec<String>>,
    alerts: AtomicU64,
}

impl Ui for FakeUi {
    fn line(&self, e: LineEvent) {
        self.events.lock().unwrap().push(format!("line {}", e.text));
        self.lines.lock().unwrap().push(e);
    }
    fn translation(&self, e: TranslationEvent) {
        self.translations.lock().unwrap().push(e);
    }
    fn own(&self, e: OwnEvent) {
        self.own.lock().unwrap().push(e);
    }
    fn notice(&self, e: NoticeEvent) {
        self.notices.lock().unwrap().push(e);
    }
    fn status(&self, _e: StatusEvent) {}
    fn restore(&self, text: String) {
        self.restored.lock().unwrap().push(text);
    }
    fn alert_private(&self) {
        self.alerts.fetch_add(1, Ordering::SeqCst);
    }
}

struct FakeGame {
    log: PathBuf,
    sent: Mutex<Vec<String>>,
    fail: AtomicBool,
    echo: AtomicBool,
}

impl Game for FakeGame {
    fn send(&self, text: &str, _return_to_app: bool) -> Result<(), String> {
        self.sent.lock().unwrap().push(text.to_string());
        if self.fail.load(Ordering::SeqCst) {
            return Err("No se encontró la ventana del juego.".into());
        }
        if self.echo.load(Ordering::SeqCst) {
            // Como el juego: "/p hola" sale en el canal de grupo sin el comando.
            let (channel, msg) = match text.strip_prefix("/p ") {
                Some(rest) => ("Party", rest),
                None => ("Talk", text),
            };
            append(&self.log, &format!("[CHAT WINDOW TEXT] [Sun Oct 11 14:40:00] Aria: [{channel}] {msg}\n"));
        }
        Ok(())
    }
    fn modifiers_down(&self) -> bool {
        false
    }
}

fn append(path: &Path, text: &str) {
    let mut f = std::fs::OpenOptions::new().append(true).open(path).unwrap();
    f.write_all(text.as_bytes()).unwrap();
}

async fn until<F: Fn() -> bool>(what: &str, f: F) {
    let start = Instant::now();
    while !f() {
        assert!(start.elapsed() < Duration::from_secs(8), "no llegó a tiempo: {what}");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
}

fn last_own(ui: &FakeUi) -> OwnEvent {
    ui.own.lock().unwrap().last().unwrap().clone()
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn de_principio_a_fin() {
    let (base, http_log) = server(Arc::new(|r: &Req| google_ok(r))).await;
    let dir = tempfile::tempdir().unwrap();
    let game_dir = dir.path().join("Neverwinter Nights");
    std::fs::create_dir_all(game_dir.join("logs")).unwrap();
    let log = game_dir.join("logs").join("nwclientLog1.txt");
    std::fs::write(&log, "").unwrap();

    let settings_path = dir.path().join("config.json");
    let mut settings = Settings::load_from(&settings_path);
    settings.proveedor = Some("Google".into());
    settings.retardo_envio_ms = 0;

    let ui = Arc::new(FakeUi::default());
    let game = Arc::new(FakeGame { log: log.clone(), sent: Mutex::default(), fail: AtomicBool::new(false), echo: AtomicBool::new(true) });
    let engine = Engine::new(
        settings,
        EngineConfig { game_dirs: vec![game_dir], settings_path: Some(settings_path.clone()), incoming_max_wait: Duration::from_secs(15) },
        Translator::new(endpoints(&base)),
        ui.clone(),
        game.clone(),
    );
    engine.start(&tokio::runtime::Handle::current());
    until("log encontrado", || engine.status().log_found).await;

    // ---------- Llega un privado: se traduce y avisa ----------
    append(&log, "[CHAT WINDOW TEXT] [Sun Oct 11 14:35:00] Kael: [Tell] hello friend\n");
    until("traducción", || !ui.translations.lock().unwrap().is_empty()).await;
    let line = ui.lines.lock().unwrap()[0].clone();
    assert_eq!((line.kind, line.channel.as_deref(), line.echo, line.translate), ("chat", Some("Tell"), false, true));
    let tr = ui.translations.lock().unwrap()[0].clone();
    assert_eq!((tr.id, tr.text.as_str()), (line.id, "HELLO FRIEND"));
    assert_eq!(ui.alerts.load(Ordering::SeqCst), 1);

    // Combate con "Solo chat de jugadores": se muestra en Inglés pero no se traduce.
    append(&log, "[CHAT WINDOW TEXT] [Sun Oct 11 14:35:01] Kael : Attack of Opportunity : *hit*\n");
    until("línea de sistema", || ui.lines.lock().unwrap().len() == 2).await;
    assert!(!ui.lines.lock().unwrap()[1].translate);

    // ---------- Escribes: se traduce, se envía y el eco lo confirma ----------
    let requests_before = http_log.lock().unwrap().len();
    engine.send("/p hola amigos", false, true);
    until("confirmado", || last_own(&ui).status == OwnStatus::Confirmado).await;
    assert_eq!(game.sent.lock().unwrap().as_slice(), ["/p HOLA AMIGOS"]);
    let statuses: Vec<OwnStatus> = ui.own.lock().unwrap().iter().map(|e| e.status).collect();
    assert_eq!(
        statuses,
        [OwnStatus::Traduciendo, OwnStatus::Esperando, OwnStatus::Escribiendo, OwnStatus::Confirmado]
    );
    assert_eq!(last_own(&ui).en.as_deref(), Some("/p HOLA AMIGOS"));
    // El eco se marca como tuyo y su "traducción" es tu español, sin otra petición.
    until("eco traducido", || ui.translations.lock().unwrap().len() == 2).await;
    let echo_line = ui.lines.lock().unwrap().last().unwrap().clone();
    assert!(echo_line.echo);
    let echo_tr = ui.translations.lock().unwrap().last().unwrap().clone();
    assert_eq!((echo_tr.text.as_str(), echo_tr.echo), ("hola amigos", true));
    assert_eq!(http_log.lock().unwrap().len(), requests_before + 1);

    // ---------- Esc cancela un mensaje que espera ----------
    engine.update_settings(SettingsPatch { send_delay_ms: Some(3000), ..Default::default() });
    engine.send("esto no sale", true, true);
    until("esperando", || last_own(&ui).status == OwnStatus::Esperando).await;
    assert_eq!(engine.cancel_last().as_deref(), Some("esto no sale"));
    assert_eq!(last_own(&ui).status, OwnStatus::Cancelado);
    assert_eq!(engine.cancel_last(), None);
    tokio::time::sleep(Duration::from_millis(300)).await;
    assert_eq!(game.sent.lock().unwrap().len(), 1);

    // ---------- Fallo al enviar: avisa y devuelve el texto ----------
    engine.update_settings(SettingsPatch { send_delay_ms: Some(0), upper_case: Some(true), ..Default::default() });
    game.fail.store(true, Ordering::SeqCst);
    engine.send("/roll 1d20", false, true);
    until("fallido", || last_own(&ui).status == OwnStatus::Fallido).await;
    assert_eq!(game.sent.lock().unwrap().last().unwrap(), "/roll 1d20"); // comando: ni traducido ni en mayúsculas
    assert_eq!(ui.restored.lock().unwrap().as_slice(), ["/roll 1d20"]);

    // ---------- Sin eco en el log: "sin confirmar" ----------
    game.fail.store(false, Ordering::SeqCst);
    game.echo.store(false, Ordering::SeqCst);
    engine.send("hola", true, true);
    until("sin confirmar", || last_own(&ui).status == OwnStatus::SinConfirmar).await;
    assert_eq!(game.sent.lock().unwrap().last().unwrap(), "HOLA");

    // Los ajustes se guardan en el mismo formato que la versión anterior.
    engine.shutdown();
    let saved = std::fs::read_to_string(&settings_path).unwrap();
    assert!(saved.contains("\"Proveedor\": \"Google\"") && saved.contains("\"Mayusculas\": true"));
}
