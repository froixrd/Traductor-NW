// Demo para la vista previa en el navegador: simula una escena en una taberna del puerto,
// con mensajes que llegan, se traducen y una respuesta tuya que sale al juego.
import type { Backend, Handlers } from "./api";
import type { OwnEvent, Provider, Settings, StatusEvent } from "./types";

const SCRIPT: Array<{
  speaker?: string;
  channel?: string;
  text: string;
  es: string;
  system?: boolean;
  untranslated?: boolean;
}> = [
  { speaker: "Seraphine", channel: "Talk", text: "*pushes the tavern door open and shakes the rain off her cloak*", es: "*empuja la puerta de la taberna y se sacude la lluvia de la capa*" },
  { speaker: "Kael Ironfist", channel: "Talk", text: "Well met, stranger. The harbor's no place to be on a night like this.", es: "Bien hallada, forastera. El puerto no es lugar para estar en una noche como esta." },
  { speaker: "Seraphine", channel: "Talk", text: "I'm looking for a ship heading south. Any idea who I should ask?", es: "Busco un barco que vaya hacia el sur. ¿Alguna idea de a quién debería preguntar?" },
  { system: true, text: "Kael Ironfist : Persuade : *success* : (14 + 6 = 20 vs. DC: 15)", es: "Kael Ironfist : Persuadir : *éxito* : (14 + 6 = 20 contra CD: 15)" },
  { speaker: "Brother Aldous", channel: "Party", text: "brb, gonna grab some food. dont start the quest without me pls", es: "Ahora vuelvo, voy a por algo de comer. No empecéis la misión sin mí, por favor." },
  { speaker: "Kael Ironfist", channel: "Talk", text: "*leans in and lowers his voice* Ask for Old Maren at the Moonstone Mask. She owes me a favor.", es: "*se inclina y baja la voz* Pregunta por la vieja Maren en la Máscara de Piedra Lunar. Me debe un favor." },
  { speaker: "Lyra Vex", channel: "Tell", text: "hey, are you joining the dungeon run tonight?", es: "Oye, ¿te apuntas a la mazmorra esta noche?" },
  { speaker: "Harbormaster", channel: "Shout", text: "The Sea Wyrm leaves at dawn! Last call for passengers!", es: "¡El Sierpe Marina zarpa al amanecer! ¡Último aviso para los pasajeros!" },
  { speaker: "Seraphine", channel: "Talk", text: "That's my ship, then. Thank you, friend.", es: "Ese es mi barco, entonces. Gracias, amigo.", untranslated: false },
];

const OWN_DEMO: Record<string, string> = {
  "claro, cuenta conmigo": "Sure, count me in",
  "nos vemos en el muelle al amanecer": "See you at the dock at dawn",
};

function fakeEnglish(es: string): string {
  return OWN_DEMO[es.trim().toLowerCase()] ?? es.replace(/ñ/g, "n");
}

const SETTINGS: Settings = {
  provider: "Microsoft",
  microsoftKey: "",
  microsoftRegion: "",
  deeplKey: "",
  googleFallback: true,
  noTranslate: ["Seraphine", "Moonstone Mask"],
  filterSystem: true,
  sendDelayMs: 2000,
  returnToApp: true,
  hotkey: true,
  alertPrivate: true,
  theme: "taberna",
  alwaysOnTop: true,
  compact: false,
  inactiveOpacity: 100,
  upperCase: false,
};

export function createDemoBackend(): Backend {
  let h: Handlers | null = null;
  let id = 1000;
  let settings = { ...SETTINGS };
  let pendingOwn: { ev: OwnEvent; timer: number } | null = null;
  const timers: number[] = [];

  const status = (): StatusEvent => ({
    active: "Google",
    note: settings.microsoftKey ? "" : "sin clave de Microsoft",
    noteWarn: false,
    cooldownS: 0,
    pending: 0,
    logFound: true,
    chain: settings.microsoftKey ? ["Microsoft", "Google"] : ["Google"],
    googleInChain: settings.googleFallback,
  });

  const time = (offsetMin = 0) => {
    const d = new Date(Date.now() - offsetMin * 60000);
    return d.toTimeString().slice(0, 5);
  };

  function play() {
    if (!h) return;
    h.notice({
      id: id++,
      text: "Vista previa con una partida de ejemplo. Prueba a escribir, pulsa Esc mientras se consume la mecha, cambia de pestaña o abre los ajustes.",
      tone: "info",
    });
    h.notice({ id: id++, text: "Log encontrado. Traduciendo en tiempo real.", tone: "accent" });
    h.status(status());
    let t = 300;
    SCRIPT.forEach((s, i) => {
      t += i < 4 ? 450 : 900;
      timers.push(
        window.setTimeout(() => {
          const lineId = id++;
          const translate = !s.system || !settings.filterSystem;
          h?.line({
            id: lineId,
            kind: s.system ? "system" : "chat",
            time: time(SCRIPT.length - i),
            speaker: s.speaker,
            channel: s.channel,
            text: s.text,
            translate,
          });
          if (translate)
            timers.push(window.setTimeout(() => h?.translation({ id: lineId, text: s.es, echo: false }), 380));
        }, t),
      );
    });
    timers.push(
      window.setTimeout(() => {
        h?.notice({
          id: id++,
          text: "Para usar Microsoft Translator (gratis hasta 2 millones de caracteres al mes) pega tu clave en Ajustes. Mientras tanto se traduce con Google.",
          tone: "info",
        });
      }, t + 500),
    );
  }

  return {
    demo: true,
    async subscribe(handlers) {
      h = handlers;
      return () => {
        if (h !== handlers) return;
        h = null;
        timers.forEach(clearTimeout);
      };
    },
    async ready() {
      play();
    },
    async getSettings() {
      return { ...settings };
    },
    async updateSettings(patch) {
      settings = { ...settings, ...patch };
      h?.status(status());
      return { ...settings };
    },
    async providerInfo(p: Provider) {
      const key = p === "Microsoft" ? settings.microsoftKey : p === "DeepL" ? settings.deeplKey : "x";
      return {
        state: key ? "ok" : "noKey",
        usageCount: p === "Microsoft" ? 184_320 : null,
        usageLimit: p === "Microsoft" ? 2_000_000 : null,
      };
    },
    async checkKey(p, key) {
      await new Promise((r) => setTimeout(r, 700));
      if (key.length < 8) return { ok: false, message: `${p} rechazó la clave: clave no válida.` };
      if (p === "Microsoft") settings.microsoftKey = key;
      else settings.deeplKey = key;
      settings.provider = p;
      h?.status(status());
      return { ok: true, message: "✓ Clave válida. Microsoft Translator listo.", usageCount: 184_320, usageLimit: 2_000_000 };
    },
    async send(text, raw) {
      if (!h) return;
      const ev: OwnEvent = { id: id++, es: text, status: raw ? "esperando" : "traduciendo", raw };
      h.own(ev);
      const delay = settings.sendDelayMs;
      window.setTimeout(() => {
        let en = raw ? text : fakeEnglish(text);
        if (settings.upperCase) en = en.toUpperCase();
        const waiting: OwnEvent = { ...ev, en, status: "esperando", delayMs: delay };
        h?.own(waiting);
        const timer = window.setTimeout(() => {
          pendingOwn = null;
          h?.own({ ...waiting, status: "escribiendo" });
          window.setTimeout(() => {
            h?.own({ ...waiting, status: "confirmado" });
            const lineId = id++;
            h?.line({ id: lineId, kind: "chat", time: time(), speaker: "Tú (Aria)", channel: "Talk", text: en, translate: true, echo: true });
            h?.translation({ id: lineId, text, echo: true });
          }, 700);
        }, delay);
        pendingOwn = { ev: waiting, timer };
      }, raw ? 0 : 450);
    },
    async cancelLast() {
      if (!pendingOwn) return null;
      clearTimeout(pendingOwn.timer);
      h?.own({ ...pendingOwn.ev, status: "cancelado" });
      const text = pendingOwn.ev.es;
      pendingOwn = null;
      return text;
    },
    async setModifiers() {},
    async minimize() {},
    async close() {},
    async onFocusChange(cb) {
      const on = () => cb(true);
      const off = () => cb(false);
      window.addEventListener("focus", on);
      window.addEventListener("blur", off);
      return () => {
        window.removeEventListener("focus", on);
        window.removeEventListener("blur", off);
      };
    },
  };
}
