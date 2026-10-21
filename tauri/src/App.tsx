import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import type { Backend } from "./api";
import { Chat } from "./Chat";
import { feedReducer, type Tab } from "./feed";
import { Icon } from "./icons";
import { SettingsPanel } from "./Settings";
import { formatSeconds, outgoingChannel } from "./text";
import type { OwnEvent, Settings, StatusEvent } from "./types";

const TABS: Array<{ id: Tab; label: string }> = [
  { id: "ambos", label: "Ambos" },
  { id: "ingles", label: "Inglés" },
  { id: "espanol", label: "Español" },
];

function pill(status: StatusEvent | null): { dot: string; label: string; note: string; warn: boolean } {
  if (!status) return { dot: "dim", label: "Arrancando", note: "", warn: false };
  const name = status.active === "Google" ? "Google Translate" : status.active;
  if (!status.active) return { dot: "alert", label: "Sin traductor", note: status.note, warn: true };
  if (status.cooldownS >= 1)
    return {
      dot: "alert",
      label: `Pausa ${Math.ceil(status.cooldownS)} s`,
      note: status.pending > 0 ? `${status.pending} en cola` : "",
      warn: true,
    };
  if (!status.logFound) return { dot: "dim", label: "Esperando al juego", note: status.active, warn: false };
  if (status.pending >= 3) return { dot: "accent", label: status.active, note: `${status.pending} en cola`, warn: false };
  return { dot: "ok", label: name!, note: status.note, warn: status.noteWarn };
}

const IN_FLIGHT: OwnEvent["status"][] = ["traduciendo", "esperando", "cola", "escribiendo"];

export function App({ backend, initial }: { backend: Backend; initial: Settings }) {
  const [items, dispatch] = useReducer(feedReducer, []);
  const [settings, setSettings] = useState<Settings>(initial);
  const [status, setStatus] = useState<StatusEvent | null>(null);
  const [tab, setTab] = useState<Tab>("ambos");
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [text, setText] = useState("");
  const [focused, setFocused] = useState(true);
  const [preview, setPreview] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const textRef = useRef(text);
  textRef.current = text;

  // ---------- Eventos del núcleo ----------
  useEffect(() => {
    let off: (() => void) | undefined;
    let offFocus: (() => void) | undefined;
    let cancelled = false;
    (async () => {
      off = await backend.subscribe({
        line: (e) => dispatch({ type: "line", e }),
        translation: (e) => dispatch({ type: "translation", e }),
        own: (e) => dispatch({ type: "own", e }),
        notice: (e) => dispatch({ type: "notice", e }),
        status: setStatus,
        restore: (t) => {
          if (!textRef.current) setText(t);
        },
        focusInput: () => {
          setSettingsOpen(false);
          input.current?.focus();
        },
      });
      offFocus = await backend.onFocusChange(setFocused);
      if (cancelled) {
        off();
        offFocus();
        return;
      }
      await backend.ready();
    })();
    return () => {
      cancelled = true;
      off?.();
      offFocus?.();
    };
  }, [backend]);

  const update = useCallback(
    (patch: Partial<Settings>) => {
      setSettings((s) => ({ ...s, ...patch })); // al momento; el núcleo confirma después
      backend.updateSettings(patch).then(setSettings);
      if (patch.inactiveOpacity !== undefined) setPreview(true);
    },
    [backend],
  );

  // Vista previa de la transparencia un momento al moverla.
  useEffect(() => {
    if (!preview) return;
    const t = setTimeout(() => setPreview(false), 1000);
    return () => clearTimeout(t);
  }, [preview, settings.inactiveOpacity]);

  useEffect(() => {
    document.documentElement.dataset.theme = settings.theme;
  }, [settings.theme]);

  const opacity = (!focused || preview) && settings.inactiveOpacity < 100 ? settings.inactiveOpacity / 100 : 1;

  // ---------- Escribir y enviar ----------
  function send(raw: boolean) {
    const t = text.trim();
    if (!t) return;
    setText("");
    backend.send(t, raw);
  }

  async function cancelLast(): Promise<boolean> {
    const t = await backend.cancelLast();
    if (t == null) return false;
    if (!textRef.current) setText(t);
    input.current?.focus();
    return true;
  }

  // Alt, Ctrl y Mayús: el núcleo espera a que se suelten antes de pulsar Enter en el juego.
  useEffect(() => {
    let down = false;
    const track = (e: KeyboardEvent) => {
      const now = e.altKey || e.ctrlKey || e.shiftKey;
      if (now !== down) {
        down = now;
        backend.setModifiers(now);
      }
    };
    const reset = () => {
      if (down) {
        down = false;
        backend.setModifiers(false);
      }
    };
    window.addEventListener("keydown", track, true);
    window.addEventListener("keyup", track, true);
    window.addEventListener("blur", reset);
    return () => {
      window.removeEventListener("keydown", track, true);
      window.removeEventListener("keyup", track, true);
      window.removeEventListener("blur", reset);
    };
  }, [backend]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey && !e.shiftKey && !e.altKey && ["1", "2", "3"].includes(e.key)) {
        e.preventDefault();
        setTab(TABS[Number(e.key) - 1].id);
      } else if (e.key === "Escape" && !settingsOpen) {
        cancelLast();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settingsOpen]);

  // Línea de ayuda bajo el campo de texto.
  const inFlight = useMemo(() => {
    const list: OwnEvent[] = [];
    for (const it of items) if (it.kind === "own" && IN_FLIGHT.includes(it.own.status)) list.push(it.own);
    return list;
  }, [items]);

  let hint: { text: string; tone: string };
  if (inFlight.length > 0) {
    const last = inFlight[inFlight.length - 1];
    const short = last.es.length <= 40 ? last.es : last.es.slice(0, 39) + "…";
    if ((status?.cooldownS ?? 0) >= 1)
      hint = {
        text: `«${short}» está en cola: sale en cuanto ${status?.active ?? "el traductor"} vuelva a responder. Esc para cancelarlo.`,
        tone: "warn",
      };
    else if (last.status === "escribiendo")
      hint = { text: inFlight.length > 1 ? `Escribiendo en el juego… (${inFlight.length} mensajes)` : "Escribiendo en el juego…", tone: "accent" };
    else
      hint = {
        text:
          inFlight.length > 1
            ? `«${short}» y ${inFlight.length - 1} más a punto de salir. Esc para cancelar el último.`
            : `«${short}» a punto de salir. Esc para cancelarlo y corregirlo.`,
        tone: "accent",
      };
  } else {
    hint = {
      text:
        settings.sendDelayMs === 0
          ? "Enter traduce y envía al juego. Ctrl+Enter lo envía sin traducir."
          : `Enter traduce y envía al juego en ${formatSeconds(settings.sendDelayMs)}. Ctrl+Enter lo envía sin traducir.`,
      tone: "dim",
    };
  }

  const p = pill(status);
  const channel = outgoingChannel(text);

  return (
    <div className={`app${settings.compact ? " compact" : ""}`} style={{ opacity }}>
      <header className="titlebar" data-tauri-drag-region>
        <div className="brand" data-tauri-drag-region>
          <span className="logo" data-tauri-drag-region>
            <Icon name="d20" size={26} stroke={1.4} />
          </span>
          {!settings.compact && (
            <span className="brand-text" data-tauri-drag-region>
              <span className="brand-title" data-tauri-drag-region>
                Traductor de chat
              </span>
              <span className="brand-sub" data-tauri-drag-region>
                Neverwinter Nights, del inglés al español
              </span>
            </span>
          )}
        </div>
        <button
          className={`pill${p.warn ? " pill-warn" : ""}`}
          onClick={() => setSettingsOpen(true)}
          title="Estado del traductor. Pulsa para abrir los ajustes."
        >
          <span className={`dot dot-${p.dot}`} />
          <span className="pill-label">{p.label}</span>
          {p.note && <span className="pill-note">{p.note}</span>}
        </button>
        <div className="win-buttons">
          <button
            className={`icon-btn${settingsOpen ? " on" : ""}`}
            onClick={() => setSettingsOpen((v) => !v)}
            aria-label={settingsOpen ? "Cerrar ajustes" : "Ajustes"}
            title="Ajustes"
          >
            <Icon name="sliders" size={18} />
          </button>
          <button
            className={`icon-btn${settings.alwaysOnTop ? " on" : ""}`}
            onClick={() => update({ alwaysOnTop: !settings.alwaysOnTop })}
            aria-pressed={settings.alwaysOnTop}
            aria-label="Siempre encima del juego"
            title={settings.alwaysOnTop ? "Siempre encima: activado" : "Siempre encima: desactivado"}
          >
            <Icon name="pin" size={17} />
          </button>
          <button className="icon-btn" onClick={() => backend.minimize()} aria-label="Minimizar" title="Minimizar">
            <Icon name="minus" size={17} />
          </button>
          <button className="icon-btn close" onClick={() => backend.close()} aria-label="Cerrar" title="Cerrar">
            <Icon name="close" size={16} />
          </button>
        </div>
      </header>

      {!settings.compact && (
        <nav className="tabs" role="tablist">
          {TABS.map((t, i) => (
            <button
              key={t.id}
              role="tab"
              aria-selected={tab === t.id}
              className={tab === t.id ? "on" : ""}
              onClick={() => setTab(t.id)}
              title={`Ctrl+${i + 1}`}
            >
              {t.label}
            </button>
          ))}
          {settings.filterSystem && (
            <button className="filter-chip" onClick={() => setSettingsOpen(true)} title="Abre los ajustes">
              <Icon name="filter" size={13} stroke={2} />
              Solo chat de jugadores
            </button>
          )}
        </nav>
      )}

      <main className="stage">
        <Chat items={items} tab={tab} />
        {settingsOpen && (
          <>
            <div className="scrim" onClick={() => setSettingsOpen(false)} />
            <SettingsPanel
              backend={backend}
              settings={settings}
              status={status}
              update={update}
              onClose={() => {
                setSettingsOpen(false);
                input.current?.focus();
              }}
            />
          </>
        )}
      </main>

      <footer className="composer">
        <div className="compose-row">
          <div className={`input-wrap${channel ? " has-channel" : ""}`}>
            {channel && <span className={`tag tag-${channel.kind}`}>{channel.label}</span>}
            <input
              ref={input}
              autoFocus
              value={text}
              spellCheck
              lang="es"
              placeholder="Escribe en español y pulsa Enter"
              aria-label="Mensaje en español"
              onChange={(e) => setText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  send(e.ctrlKey);
                }
              }}
            />
          </div>
          <button
            className={`icon-btn field-btn${settings.upperCase ? " on" : ""}`}
            onClick={() => update({ upperCase: !settings.upperCase })}
            aria-pressed={settings.upperCase}
            aria-label="Enviar en mayúsculas"
            title="Enviar en mayúsculas"
          >
            <Icon name="caps" size={18} />
          </button>
          <button
            className="send-btn"
            onClick={() => {
              send(false);
              input.current?.focus();
            }}
            aria-label="Enviar al juego"
            title="Enviar al juego"
          >
            <Icon name="send" size={19} stroke={2} />
          </button>
        </div>
        {!settings.compact && <p className={`hint tone-${hint.tone}`}>{hint.text}</p>}
      </footer>
    </div>
  );
}
