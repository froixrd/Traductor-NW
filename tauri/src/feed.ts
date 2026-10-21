// Historial del chat: todo lo que llega, en orden. Cada pestaña lo filtra a su manera.
import type { LineEvent, NoticeEvent, OwnEvent, TranslationEvent } from "./types";

export type Item =
  | { key: string; kind: "line"; line: LineEvent; tr?: TranslationEvent }
  | { key: string; kind: "notice"; notice: NoticeEvent }
  | { key: string; kind: "own"; own: OwnEvent };

export type Action =
  | { type: "line"; e: LineEvent }
  | { type: "translation"; e: TranslationEvent }
  | { type: "own"; e: OwnEvent }
  | { type: "notice"; e: NoticeEvent };

// Como en la versión anterior se guardan unos cientos de mensajes; los más antiguos se descartan.
const MAX_ITEMS = 500;

function findFromEnd(items: Item[], key: string): number {
  for (let i = items.length - 1; i >= 0; i--) if (items[i].key === key) return i;
  return -1;
}

function append(items: Item[], item: Item): Item[] {
  const next = items.length >= MAX_ITEMS ? items.slice(items.length - MAX_ITEMS + 50) : items.slice();
  next.push(item);
  return next;
}

export function feedReducer(items: Item[], a: Action): Item[] {
  switch (a.type) {
    case "line":
      return append(items, { key: "l" + a.e.id, kind: "line", line: a.e });
    case "notice":
      return append(items, { key: "n" + a.e.id, kind: "notice", notice: a.e });
    case "translation": {
      const i = findFromEnd(items, "l" + a.e.id);
      if (i < 0) return items;
      const next = items.slice();
      next[i] = { ...(items[i] as Extract<Item, { kind: "line" }>), tr: a.e };
      return next;
    }
    case "own": {
      const i = findFromEnd(items, "o" + a.e.id);
      if (i < 0) return append(items, { key: "o" + a.e.id, kind: "own", own: a.e });
      const next = items.slice();
      next[i] = { key: "o" + a.e.id, kind: "own", own: a.e };
      return next;
    }
  }
}

export type Tab = "ambos" | "ingles" | "espanol";

// Lo que se ve en cada pestaña:
//  Ambos:   avisos, tus mensajes y lo que se traduce (traducción y debajo el original);
//  Inglés:  todas las líneas del juego tal cual, al instante;
//  Español: solo las traducciones.
export function visible(item: Item, tab: Tab): boolean {
  switch (item.kind) {
    case "notice":
    case "own":
      return tab === "ambos";
    case "line":
      if (tab === "ingles") return true;
      if (!item.line.translate) return false;
      if (tab === "ambos" && (item.line.echo || item.tr?.echo)) return false; // ya aparece como mensaje tuyo
      return true;
  }
}
