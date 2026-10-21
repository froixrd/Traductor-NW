// Sin consola detrás de la ventana en la versión final.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

#[cfg(windows)]
mod win;

use nucleo::engine::{Engine, EngineConfig, KeyCheck, ProviderInfo, SettingsPatch, UiSettings};
use nucleo::events::{Game, LineEvent, NoticeEvent, OwnEvent, StatusEvent, Tone, TranslationEvent, Ui};
use nucleo::settings::{self, Settings};
use nucleo::translate::{Provider, Translator};
use std::io::Write;
use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use std::sync::{Arc, Mutex};
use tauri::{AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, State, UserAttentionType, WebviewWindow, WindowEvent};
use tauri_plugin_global_shortcut::{Code, GlobalShortcutExt, Modifiers, Shortcut, ShortcutState};

const HOTKEY_LABEL: &str = "Ctrl+Mayús+Espacio";

fn hotkey() -> Shortcut {
    Shortcut::new(Some(Modifiers::CONTROL | Modifiers::SHIFT), Code::Space)
}

// ======================================================================
//  Puentes entre el núcleo y la ventana
// ======================================================================

struct TauriUi {
    app: AppHandle,
}

impl Ui for TauriUi {
    fn line(&self, e: LineEvent) {
        let _ = self.app.emit("line", e);
    }
    fn translation(&self, e: TranslationEvent) {
        let _ = self.app.emit("translation", e);
    }
    fn own(&self, e: OwnEvent) {
        let _ = self.app.emit("own", e);
    }
    fn notice(&self, e: NoticeEvent) {
        let _ = self.app.emit("notice", e);
    }
    fn status(&self, e: StatusEvent) {
        let _ = self.app.emit("status", e);
    }
    fn restore(&self, text: String) {
        let _ = self.app.emit("restore", text);
    }
    /// Mensaje privado: sonido y la ventana parpadea en la barra de tareas si no la estás usando.
    fn alert_private(&self) {
        let app = self.app.clone();
        let _ = self.app.run_on_main_thread(move || {
            #[cfg(windows)]
            win::beep();
            if let Some(w) = app.get_webview_window("main") {
                if !w.is_focused().unwrap_or(false) {
                    let _ = w.request_user_attention(Some(UserAttentionType::Critical));
                }
            }
        });
    }
}

struct WinGame {
    own_window: Arc<AtomicIsize>,
    modifiers: Arc<AtomicBool>,
}

impl Game for WinGame {
    fn send(&self, text: &str, return_to_app: bool) -> Result<(), String> {
        #[cfg(windows)]
        {
            let own = self.own_window.load(Ordering::SeqCst);
            win::send_to_game(text, (return_to_app && own != 0).then_some(own))
        }
        #[cfg(not(windows))]
        {
            let _ = (text, return_to_app, &self.own_window);
            Err("Solo funciona en Windows.".into())
        }
    }

    /// Lo dice la propia interfaz (teclas que recibe la ventana del traductor), sin leer el teclado de
    /// todo el sistema: los antivirus asocian esa lectura a los keyloggers.
    fn modifiers_down(&self) -> bool {
        self.modifiers.load(Ordering::SeqCst)
    }
}

#[cfg_attr(not(windows), allow(dead_code))]
struct AppState {
    engine: Engine,
    /// Ventana desde la que se saltó al traductor con la tecla rápida (el juego).
    hotkey_return_to: Mutex<Option<isize>>,
    modifiers: Arc<AtomicBool>,
    own_window: Arc<AtomicIsize>,
}

// ======================================================================
//  Órdenes que llama la interfaz
// ======================================================================

#[tauri::command]
async fn ui_ready(state: State<'_, AppState>) -> Result<(), String> {
    state.engine.start(&tokio::runtime::Handle::current());
    Ok(())
}

#[tauri::command]
fn get_settings(state: State<'_, AppState>) -> UiSettings {
    state.engine.ui_settings()
}

#[tauri::command]
fn update_settings(app: AppHandle, state: State<'_, AppState>, patch: SettingsPatch) -> UiSettings {
    let on_top = patch.always_on_top;
    let hotkey_on = patch.hotkey;
    let result = state.engine.update_settings(patch);
    if let Some(w) = app.get_webview_window("main") {
        if let Some(v) = on_top {
            let _ = w.set_always_on_top(v);
        }
    }
    if let Some(v) = hotkey_on {
        set_hotkey(&app, &state.engine, v);
    }
    result
}

fn parse_provider(name: &str) -> Result<Provider, String> {
    Provider::parse(name).ok_or_else(|| format!("traductor desconocido: {name}"))
}

#[tauri::command]
async fn provider_info(state: State<'_, AppState>, provider: String) -> Result<ProviderInfo, String> {
    Ok(state.engine.provider_info(parse_provider(&provider)?).await)
}

#[tauri::command]
async fn check_key(state: State<'_, AppState>, provider: String, key: String, region: String) -> Result<KeyCheck, String> {
    Ok(state.engine.check_key(parse_provider(&provider)?, &key, &region).await)
}

#[tauri::command]
fn send_message(state: State<'_, AppState>, text: String, raw: bool) {
    // Si se llegó con la tecla rápida desde el juego, después de enviar se queda en el juego.
    let via_hotkey = state.hotkey_return_to.lock().unwrap().take().is_some();
    let return_to_app = state.engine.with_settings(|s| s.volver_al_traductor) && !via_hotkey;
    state.engine.send(&text, raw, return_to_app);
}

#[tauri::command]
fn cancel_last(state: State<'_, AppState>) -> Option<String> {
    state.engine.cancel_last()
}

#[tauri::command]
fn set_modifiers(state: State<'_, AppState>, down: bool) {
    state.modifiers.store(down, Ordering::SeqCst);
}

// ======================================================================
//  Tecla rápida: Ctrl+Mayús+Espacio
// ======================================================================

fn set_hotkey(app: &AppHandle, engine: &Engine, on: bool) {
    let gs = app.global_shortcut();
    let registered = gs.is_registered(hotkey());
    if on && !registered {
        if gs.register(hotkey()).is_err() {
            engine.notice(format!("No se pudo activar {HOTKEY_LABEL}: otro programa ya usa esa combinación."), Tone::Warn);
        }
    } else if !on && registered {
        let _ = gs.unregister(hotkey());
    }
}

/// Desde el juego salta al campo de escribir; desde el traductor vuelve al juego.
/// Un mensaje escrito tras saltar con la tecla se envía y te deja en el juego.
fn on_hotkey(app: &AppHandle) {
    let Some(w) = app.get_webview_window("main") else { return };
    let state = app.state::<AppState>();
    #[cfg(windows)]
    {
        let own = state.own_window.load(Ordering::SeqCst);
        let fg = win::foreground();
        if fg == own {
            let back = state.hotkey_return_to.lock().unwrap().take().filter(|h| win::is_window(*h));
            let game = back.unwrap_or_else(win::find_game_window);
            if game != 0 {
                std::thread::spawn(move || {
                    win::bring_to_front(game);
                });
            }
            return;
        }
        let _ = w.unminimize();
        let _ = w.show();
        std::thread::spawn(move || {
            win::bring_to_front(own);
        });
        let _ = w.set_focus();
        *state.hotkey_return_to.lock().unwrap() = Some(fg);
    }
    #[cfg(not(windows))]
    {
        let _ = w.set_focus();
        let _ = &state;
    }
    let _ = app.emit("focus-input", ());
}

// ======================================================================
//  Ventana: posición y tamaño guardados
// ======================================================================

fn restore_bounds(w: &WebviewWindow, engine: &Engine) {
    let Some(v) = engine.with_settings(|s| s.ventana.clone()) else { return };
    let [x, y, width, height] = v[..] else { return };
    let (width, height) = (width.max(400) as u32, height.max(360) as u32);
    // Solo si sigue cayendo dentro de alguna pantalla (por si se desconectó un monitor).
    let visible = w.available_monitors().unwrap_or_default().iter().any(|m| {
        let (p, s) = (m.position(), m.size());
        x < p.x + s.width as i32 && x + width as i32 > p.x && y < p.y + s.height as i32 && y + height as i32 > p.y
    });
    if visible {
        let _ = w.set_size(PhysicalSize::new(width, height));
        let _ = w.set_position(PhysicalPosition::new(x, y));
    }
}

fn save_bounds(w: &tauri::Window, engine: &Engine) {
    if w.is_minimized().unwrap_or(false) || w.is_maximized().unwrap_or(false) {
        return;
    }
    if let (Ok(p), Ok(s)) = (w.outer_position(), w.outer_size()) {
        engine.with_settings(|st| st.ventana = Some(vec![p.x, p.y, s.width as i32, s.height as i32]));
    }
}

// ======================================================================
//  Errores inesperados: %AppData%\TraductorNWN\errores.log
// ======================================================================

fn log_error(text: &str) {
    let dir = settings::folder();
    let path = dir.join("errores.log");
    let _ = std::fs::create_dir_all(&dir);
    if std::fs::metadata(&path).map(|m| m.len() > 512 * 1024).unwrap_or(false) {
        let _ = std::fs::remove_file(&path);
    }
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(&path) {
        let _ = writeln!(f, "[{}] {text}\n", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"));
    }
}

fn main() {
    std::panic::set_hook(Box::new(|info| log_error(&info.to_string())));

    let modifiers = Arc::new(AtomicBool::new(false));
    let own_window = Arc::new(AtomicIsize::new(0));

    tauri::Builder::default()
        // Una sola ventana: dos traductores leyendo el mismo log duplicarían las peticiones
        // (y los mensajes enviados al juego). Abrirlo otra vez trae delante el que ya está abierto.
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(w) = app.get_webview_window("main") {
                let _ = w.unminimize();
                let _ = w.show();
                let _ = w.set_focus();
            }
        }))
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_handler(|app, shortcut, event| {
                    if event.state() == ShortcutState::Pressed && *shortcut == hotkey() {
                        on_hotkey(app);
                    }
                })
                .build(),
        )
        .setup({
            let modifiers = modifiers.clone();
            let own_window = own_window.clone();
            move |app| {
                let handle = app.handle().clone();
                let settings = Settings::load();
                let on_top = settings.siempre_encima;
                let hotkey_on = settings.tecla_rapida;
                let engine = Engine::new(
                    settings,
                    EngineConfig::default(),
                    Translator::default(),
                    Arc::new(TauriUi { app: handle.clone() }),
                    Arc::new(WinGame { own_window: own_window.clone(), modifiers: modifiers.clone() }),
                );

                let w = app.get_webview_window("main").expect("ventana principal");
                #[cfg(windows)]
                if let Ok(hwnd) = w.hwnd() {
                    own_window.store(hwnd.0 as isize, Ordering::SeqCst);
                }
                let _ = w.set_always_on_top(on_top);
                restore_bounds(&w, &engine);
                let _ = w.show();
                let _ = w.set_focus();

                set_hotkey(&handle, &engine, hotkey_on);
                app.manage(AppState { engine, hotkey_return_to: Mutex::new(None), modifiers, own_window });
                Ok(())
            }
        })
        .on_window_event(|window, event| {
            let Some(state) = window.try_state::<AppState>() else { return };
            match event {
                WindowEvent::Focused(false) => {
                    // Fuera de la ventana no se sabe qué teclas hay pulsadas: se da por hecho que ninguna.
                    state.modifiers.store(false, Ordering::SeqCst);
                    *state.hotkey_return_to.lock().unwrap() = None;
                }
                WindowEvent::Moved(_) | WindowEvent::Resized(_) => save_bounds(window, &state.engine),
                WindowEvent::CloseRequested { .. } => {
                    save_bounds(window, &state.engine);
                    state.engine.shutdown();
                }
                _ => {}
            }
        })
        .invoke_handler(tauri::generate_handler![
            ui_ready,
            get_settings,
            update_settings,
            provider_info,
            check_key,
            send_message,
            cancel_last,
            set_modifiers
        ])
        .run(tauri::generate_context!())
        .unwrap_or_else(|e| log_error(&format!("No se pudo abrir la ventana: {e}")));
}
