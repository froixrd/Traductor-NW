// Puente con el núcleo en Rust. Fuera de la app (vista previa en el navegador) usa una demo
// que simula una partida, para poder ver y probar la interfaz sin el juego.
import type {
  KeyCheck,
  LineEvent,
  NoticeEvent,
  OwnEvent,
  Provider,
  ProviderInfo,
  Settings,
  StatusEvent,
  TranslationEvent,
} from "./types";

export interface Handlers {
  line(e: LineEvent): void;
  translation(e: TranslationEvent): void;
  own(e: OwnEvent): void;
  notice(e: NoticeEvent): void;
  status(e: StatusEvent): void;
  // Un mensaje que no pudo salir vuelve al campo de texto (si está vacío).
  restore(text: string): void;
  // Tecla rápida: el núcleo trae la ventana al frente y pide el foco en el campo de texto.
  focusInput(): void;
}

export interface Backend {
  readonly demo: boolean;
  subscribe(h: Handlers): Promise<() => void>;
  ready(): Promise<void>;
  getSettings(): Promise<Settings>;
  updateSettings(patch: Partial<Settings>): Promise<Settings>;
  providerInfo(p: Provider): Promise<ProviderInfo>;
  checkKey(p: Provider, key: string, region: string): Promise<KeyCheck>;
  send(text: string, raw: boolean): Promise<void>;
  cancelLast(): Promise<string | null>;
  // Alt, Ctrl o Mayús pulsadas en la ventana (el núcleo espera a que se suelten antes de enviar).
  setModifiers(down: boolean): Promise<void>;
  minimize(): Promise<void>;
  close(): Promise<void>;
  onFocusChange(cb: (focused: boolean) => void): Promise<() => void>;
}

const isTauri = typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

async function createTauriBackend(): Promise<Backend> {
  const { invoke } = await import("@tauri-apps/api/core");
  const { listen } = await import("@tauri-apps/api/event");
  const { getCurrentWindow } = await import("@tauri-apps/api/window");
  const win = getCurrentWindow();

  return {
    demo: false,
    async subscribe(h) {
      const offs = await Promise.all([
        listen<LineEvent>("line", (e) => h.line(e.payload)),
        listen<TranslationEvent>("translation", (e) => h.translation(e.payload)),
        listen<OwnEvent>("own", (e) => h.own(e.payload)),
        listen<NoticeEvent>("notice", (e) => h.notice(e.payload)),
        listen<StatusEvent>("status", (e) => h.status(e.payload)),
        listen<string>("restore", (e) => h.restore(e.payload)),
        listen("focus-input", () => h.focusInput()),
      ]);
      return () => offs.forEach((off) => off());
    },
    ready: () => invoke("ui_ready"),
    getSettings: () => invoke<Settings>("get_settings"),
    updateSettings: (patch) => invoke<Settings>("update_settings", { patch }),
    providerInfo: (provider) => invoke<ProviderInfo>("provider_info", { provider }),
    checkKey: (provider, key, region) => invoke<KeyCheck>("check_key", { provider, key, region }),
    send: (text, raw) => invoke("send_message", { text, raw }),
    cancelLast: () => invoke<string | null>("cancel_last"),
    setModifiers: (down) => invoke("set_modifiers", { down }),
    minimize: () => win.minimize(),
    close: () => win.close(),
    async onFocusChange(cb) {
      return win.onFocusChanged((e) => cb(e.payload));
    },
  };
}

export async function createBackend(): Promise<Backend> {
  if (isTauri) return createTauriBackend();
  const { createDemoBackend } = await import("./demo");
  return createDemoBackend();
}
