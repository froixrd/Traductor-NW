// Utilidades de texto para pintar el chat.

export interface ChannelTag {
  label: string;
  kind: "private" | "party" | "shout" | "dm" | "other";
}

export function channelTag(channel?: string | null): ChannelTag | null {
  if (!channel) return null;
  switch (channel.toLowerCase()) {
    case "talk":
      return null;
    case "tell":
      return { label: "privado", kind: "private" };
    case "whisper":
      return { label: "susurro", kind: "private" };
    case "party":
      return { label: "grupo", kind: "party" };
    case "shout":
      return { label: "grito", kind: "shout" };
    case "dm":
      return { label: "DM", kind: "dm" };
    case "server":
      return { label: "servidor", kind: "other" };
    default:
      return { label: channel.toLowerCase(), kind: "other" };
  }
}

// Partes de un mensaje: texto normal y *acciones entre asteriscos*.
export function splitEmotes(text: string): Array<{ text: string; emote: boolean }> {
  const parts: Array<{ text: string; emote: boolean }> = [];
  const re = /\*([^*]+)\*/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text))) {
    if (m.index > last) parts.push({ text: text.slice(last, m.index), emote: false });
    parts.push({ text: m[1].trim(), emote: true });
    last = m.index + m[0].length;
  }
  if (last < text.length) parts.push({ text: text.slice(last), emote: false });
  return parts;
}

// Tono fijo para cada nombre: el mismo jugador siempre tiene el mismo color de ficha.
export function speakerHue(name: string): number {
  let h = 2166136261;
  for (let i = 0; i < name.length; i++) {
    h ^= name.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return Math.abs(h) % 360;
}

export function initials(name: string): string {
  const words = name.replace(/[^\p{L}\p{N} ]/gu, " ").trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return "?";
  if (words.length === 1) return words[0].slice(0, 2);
  return (words[0][0] + words[words.length - 1][0]).toUpperCase();
}

export function same(a: string, b: string): boolean {
  return a.trim().toLowerCase() === b.trim().toLowerCase();
}

// Qué canal va a usar lo que estás escribiendo (para la etiqueta del campo de texto).
// Igual que en el núcleo: /p /s /w /dm /tk y /t "Nombre" se mantienen y se traduce el resto.
export function outgoingChannel(text: string): { label: string; kind: ChannelTag["kind"] | "raw" } | null {
  const t = text.trimStart();
  if (t.startsWith("!")) return { label: "comando, sin traducir", kind: "raw" };
  if (!t.startsWith("/")) return null;
  const tell = /^\/(?:t|tp|tell)\s+("([^"]*)"|\S+)\s/i.exec(t);
  if (tell) return { label: `privado a ${tell[2] ?? tell[1]}`, kind: "private" };
  const cmd = /^\/(\S+)(\s|$)/.exec(t);
  if (!cmd) return null;
  switch (cmd[1].toLowerCase()) {
    case "p":
    case "party":
      return { label: "grupo", kind: "party" };
    case "s":
    case "shout":
      return { label: "grito", kind: "shout" };
    case "w":
    case "whisper":
      return { label: "susurro", kind: "private" };
    case "dm":
      return { label: "DM", kind: "dm" };
    case "tk":
    case "talk":
      return { label: "hablar", kind: "other" };
    case "t":
    case "tp":
    case "tell":
      return { label: "privado", kind: "private" };
    default:
      return { label: "comando, sin traducir", kind: "raw" };
  }
}

const es = new Intl.NumberFormat("es-ES");
export const formatCount = (n: number) => es.format(n);
export const formatSeconds = (ms: number) =>
  (ms / 1000).toLocaleString("es-ES", { maximumFractionDigits: 1 }) + " s";
