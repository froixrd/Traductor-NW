// Datos que intercambian la interfaz y el núcleo en Rust (mismos nombres que en src-tauri).

export type Tone = "info" | "ok" | "warn" | "accent";

// Una línea del log del juego, nada más llegar (la pestaña Inglés la muestra al instante).
export interface LineEvent {
  id: number;
  kind: "chat" | "system";
  time: string;
  speaker?: string | null;
  channel?: string | null;
  text: string;
  // false = combate o sistema con "Solo chat de jugadores": no se traduce.
  translate: boolean;
  // Es el eco en el log de un mensaje que acabas de enviar.
  echo?: boolean;
}

// La traducción de una línea (o el motivo por el que se muestra sin traducir).
export interface TranslationEvent {
  id: number;
  text: string;
  untranslated?: string | null;
  // Es el eco en el log de un mensaje tuyo: en Ambos ya aparece como mensaje propio.
  echo: boolean;
}

export type OwnStatus =
  | "traduciendo"
  | "esperando"
  | "cola"
  | "escribiendo"
  | "confirmado"
  | "enviado"
  | "sin_confirmar"
  | "fallido"
  | "cancelado";

// Un mensaje tuyo, desde que pulsas Enter hasta que el juego lo confirma.
export interface OwnEvent {
  id: number;
  es: string;
  en?: string | null;
  status: OwnStatus;
  // Espera antes de escribirlo en el juego (para la mecha que se consume).
  delayMs?: number | null;
  raw?: boolean;
}

export interface NoticeEvent {
  id: number;
  text: string;
  tone: Tone;
}

export interface StatusEvent {
  active: string | null; // "Microsoft" | "DeepL" | "Google" | null
  note: string;
  noteWarn: boolean;
  cooldownS: number;
  pending: number;
  logFound: boolean;
  chain: string[];
  googleInChain: boolean;
}

export type Provider = "Microsoft" | "Google" | "DeepL";
export type ThemeId = "taberna" | "pizarra" | "arcano" | "bosque";

export interface Settings {
  provider: Provider;
  microsoftKey: string;
  microsoftRegion: string;
  deeplKey: string;
  googleFallback: boolean;
  noTranslate: string[];
  filterSystem: boolean;
  sendDelayMs: number;
  returnToApp: boolean;
  hotkey: boolean;
  alertPrivate: boolean;
  theme: ThemeId;
  alwaysOnTop: boolean;
  compact: boolean;
  inactiveOpacity: number;
  upperCase: boolean;
}

export type KeyState = "ok" | "noKey" | "invalidKey" | "quotaExhausted";

export interface ProviderInfo {
  state: KeyState;
  // Microsoft: lo que ha enviado esta app este mes. DeepL: lo que dice su API.
  usageCount: number | null;
  usageLimit: number | null;
}

export interface KeyCheck {
  ok: boolean;
  message: string;
  usageCount?: number | null;
  usageLimit?: number | null;
}
