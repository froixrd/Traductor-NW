//! Windows: localizar la ventana del juego, ponerla delante y escribirle el mensaje.
//!
//! Por qué no se simula el teclado "normal": el juego (NWN:EE usa SDL) identifica Enter por su
//! código físico (scancode), así que se envía con ese código y se mantiene pulsado unos
//! milisegundos. El texto viaja como caracteres Unicode: llega igual con cualquier distribución de
//! teclado (ñ, tildes, ¿ ¡) y nunca puede disparar atajos del juego. Y nunca se lee el teclado de
//! todo el sistema: los antivirus asocian eso a los keyloggers.

use std::sync::Mutex;
use std::thread::sleep;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{CloseHandle, HWND, LPARAM};
use windows_sys::Win32::System::Threading::{
    AttachThreadInput, GetCurrentProcessId, GetCurrentThreadId, OpenProcess, QueryFullProcessImageNameW,
    PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE, KEYEVENTF_UNICODE,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, EnumWindows, GetForegroundWindow, GetWindow, GetWindowTextW, GetWindowThreadProcessId, IsIconic,
    IsWindow, IsWindowVisible, SetForegroundWindow, ShowWindow, GW_OWNER, MB_ICONASTERISK, SW_RESTORE,
};
use windows_sys::Win32::System::Diagnostics::Debug::MessageBeep;

/// Las ventanas se guardan como número para poder pasarlas entre hilos.
pub type Hwnd = isize;

fn h(w: Hwnd) -> HWND {
    w as HWND
}

const GAME_PROCESSES: &[&str] = &["nwmain"];

// Procesos que NUNCA deben recibir pulsaciones aunque su título contenga "Neverwinter"
// (una pestaña del navegador con la wiki, Discord, Steam…).
const EXCLUDED_PROCESSES: &[&str] = &[
    "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "explorer", "discord", "steam",
    "steamwebhelper", "notepad", "notepad++", "code", "devenv", "msedgewebview2", "traductornwn",
];

const SCAN_ENTER: u16 = 0x1C;
// El juego mira el teclado una vez por fotograma: una pulsación de 0 ms puede perderse.
const ENTER_HOLD: Duration = Duration::from_millis(50);

const BLOCKED: &str =
    "Windows no dejó enviar las teclas al juego. Si el juego se ejecuta como administrador, abre también el traductor como administrador.";

static CACHED_GAME: Mutex<Hwnd> = Mutex::new(0);

pub fn foreground() -> Hwnd {
    unsafe { GetForegroundWindow() as Hwnd }
}

pub fn is_window(w: Hwnd) -> bool {
    w != 0 && unsafe { IsWindow(h(w)) } != 0
}

/// Sonido de aviso de Windows (el de "información").
pub fn beep() {
    unsafe { MessageBeep(MB_ICONASTERISK) };
}

/// Escribe el mensaje en el chat del juego: Enter, texto, Enter.
/// `return_to`: ventana a la que devolver el foco al terminar (el traductor), solo si era la activa
/// justo antes de enviar; así se puede seguir escribiendo sin hacer clic.
pub fn send_to_game(text: &str, return_to: Option<Hwnd>) -> Result<(), String> {
    let clean = nucleo::outgoing::for_game(text);
    if clean.is_empty() {
        return Ok(());
    }
    let previous = foreground();
    let game = find_game_window();
    if game == 0 {
        return Err("No se encontró la ventana del juego. Asegúrate de que Neverwinter Nights esté abierto.".into());
    }
    unsafe {
        if IsIconic(h(game)) != 0 {
            ShowWindow(h(game), SW_RESTORE);
        }
    }
    if !bring_to_front(game) {
        return Err("Windows no dejó poner el juego en primer plano. Haz clic en el juego y vuelve a enviar el mensaje.".into());
    }
    sleep(Duration::from_millis(150));

    // 1. Abrir la caja de chat.
    if !press_enter() {
        return Err(BLOCKED.into());
    }
    sleep(Duration::from_millis(250));

    // Si cambiaste de ventana justo ahora, no se escribe el texto en otra aplicación.
    if foreground() != game {
        return Err("El juego perdió el foco antes de escribir; mensaje no enviado.".into());
    }

    // 2. Escribir el mensaje.
    if !type_text(&clean) {
        return Err(BLOCKED.into());
    }

    // 3. Margen para que el juego termine de meter el texto en la caja antes del Enter final.
    let units = clean.encode_utf16().count() as u64;
    sleep(Duration::from_millis((120 + units * 2).clamp(150, 600)));
    if foreground() != game {
        return Err("El juego perdió el foco antes de enviar. Si el texto se quedó en el chat del juego, pulsa Enter allí.".into());
    }

    // 4. Enviar.
    if !press_enter() {
        return Err(BLOCKED.into());
    }

    // 5. Volver al traductor, si desde ahí se escribió el mensaje.
    if let Some(app) = return_to {
        if previous == app && is_window(app) {
            sleep(Duration::from_millis(150)); // que el juego reciba el Enter antes de perder el foco
            bring_to_front(app);
        }
    }
    Ok(())
}

fn key_input(scan: u16, flags: u32) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: 0, wScan: scan, dwFlags: flags, time: 0, dwExtraInfo: 0 } },
    }
}

fn send_inputs(inputs: &[INPUT]) -> bool {
    let sent = unsafe { SendInput(inputs.len() as u32, inputs.as_ptr(), std::mem::size_of::<INPUT>() as i32) };
    sent as usize == inputs.len()
}

fn press_enter() -> bool {
    let down = send_inputs(&[key_input(SCAN_ENTER, KEYEVENTF_SCANCODE)]);
    sleep(ENTER_HOLD);
    let up = send_inputs(&[key_input(SCAN_ENTER, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP)]); // soltar siempre
    down && up
}

fn type_text(text: &str) -> bool {
    const PER_BATCH: usize = 16;
    let units: Vec<u16> = text.encode_utf16().collect();
    for (i, chunk) in units.chunks(PER_BATCH).enumerate() {
        let mut batch = Vec::with_capacity(chunk.len() * 2);
        for &u in chunk {
            batch.push(key_input(u, KEYEVENTF_UNICODE));
            batch.push(key_input(u, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if !send_inputs(&batch) {
            return false;
        }
        if (i + 1) * PER_BATCH < units.len() {
            sleep(Duration::from_millis(8));
        }
    }
    true
}

// ---------- Localizar la ventana del juego ----------

fn process_name(pid: u32) -> Option<String> {
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if handle.is_null() {
            return None;
        }
        let mut buf = [0u16; 1024];
        let mut len = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(handle, PROCESS_NAME_WIN32, buf.as_mut_ptr(), &mut len);
        CloseHandle(handle);
        if ok == 0 {
            return None;
        }
        let path = String::from_utf16_lossy(&buf[..len as usize]);
        let file = path.rsplit(['\\', '/']).next().unwrap_or(&path).to_lowercase();
        Some(file.strip_suffix(".exe").unwrap_or(&file).to_string())
    }
}

fn window_title(w: HWND) -> String {
    let mut buf = [0u16; 256];
    let n = unsafe { GetWindowTextW(w, buf.as_mut_ptr(), buf.len() as i32) };
    String::from_utf16_lossy(&buf[..n.max(0) as usize])
}

struct Search {
    own_pid: u32,
    by_process: Hwnd,
    by_title: Hwnd,
}

unsafe extern "system" fn enum_proc(w: HWND, lparam: LPARAM) -> i32 {
    let s = &mut *(lparam as *mut Search);
    if IsWindowVisible(w) == 0 || !GetWindow(w, GW_OWNER).is_null() {
        return 1;
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(w, &mut pid);
    if pid == s.own_pid {
        return 1;
    }
    let name = process_name(pid).unwrap_or_default();
    // Lo más fiable: el ejecutable del juego (nwmain.exe).
    if GAME_PROCESSES.contains(&name.as_str()) {
        s.by_process = w as Hwnd;
        return 0;
    }
    // Plan B: por título, excluyendo aplicaciones que no son el juego.
    if s.by_title == 0 {
        let title = window_title(w);
        let lower = title.to_lowercase();
        let looks_like_game = lower.contains("neverwinter") || title.starts_with("NWN");
        if looks_like_game && !EXCLUDED_PROCESSES.contains(&name.as_str()) {
            s.by_title = w as Hwnd;
        }
    }
    1
}

pub fn find_game_window() -> Hwnd {
    let mut cached = CACHED_GAME.lock().unwrap();
    if is_window(*cached) && unsafe { IsWindowVisible(h(*cached)) } != 0 {
        return *cached;
    }
    let mut s = Search { own_pid: unsafe { GetCurrentProcessId() }, by_process: 0, by_title: 0 };
    unsafe { EnumWindows(Some(enum_proc), &mut s as *mut Search as LPARAM) };
    *cached = if s.by_process != 0 { s.by_process } else { s.by_title };
    *cached
}

// ---------- Poner una ventana en primer plano ----------

fn wait_foreground(w: Hwnd, timeout: Duration) -> bool {
    let start = Instant::now();
    while start.elapsed() < timeout {
        if foreground() == w {
            return true;
        }
        sleep(Duration::from_millis(15));
    }
    foreground() == w
}

/// Windows a veces impide poner una ventana delante; el truco habitual es enlazar un momento la
/// entrada de este hilo con la de la ventana que tiene el foco.
pub fn bring_to_front(w: Hwnd) -> bool {
    if foreground() == w {
        return true;
    }
    unsafe { SetForegroundWindow(h(w)) };
    if wait_foreground(w, Duration::from_millis(150)) {
        return true;
    }
    unsafe {
        let fg = GetForegroundWindow();
        let fg_thread = GetWindowThreadProcessId(fg, std::ptr::null_mut());
        let this_thread = GetCurrentThreadId();
        let attached = fg_thread != 0 && fg_thread != this_thread && AttachThreadInput(this_thread, fg_thread, 1) != 0;
        BringWindowToTop(h(w));
        SetForegroundWindow(h(w));
        if attached {
            AttachThreadInput(this_thread, fg_thread, 0);
        }
    }
    wait_foreground(w, Duration::from_millis(300))
}
