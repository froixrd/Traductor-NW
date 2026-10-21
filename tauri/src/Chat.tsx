import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { Item, Tab } from "./feed";
import { visible } from "./feed";
import { Icon } from "./icons";
import type { LineEvent, OwnEvent, TranslationEvent } from "./types";
import { channelTag, initials, same, speakerHue, splitEmotes } from "./text";

function Message({ text, className }: { text: string; className?: string }) {
  return (
    <p className={className}>
      {splitEmotes(text).map((p, i) =>
        p.emote ? (
          <em key={i} className="emote">
            {p.text}
          </em>
        ) : (
          <span key={i}>{p.text}</span>
        ),
      )}
    </p>
  );
}

function Tag({ label, kind }: { label: string; kind: string }) {
  return <span className={`tag tag-${kind}`}>{label}</span>;
}

const LineView = memo(function LineView({
  line,
  tr,
  tab,
  grouped,
}: {
  line: LineEvent;
  tr?: TranslationEvent;
  tab: Tab;
  grouped: boolean;
}) {
  const pending = tab !== "ingles" && !tr;
  const untranslated = tab !== "ingles" && tr?.untranslated != null;
  const main = tab === "ingles" || !tr || untranslated ? line.text : tr.text;
  const showOriginal = tab === "ambos" && tr && !untranslated && !same(tr.text, line.text);

  if (line.kind === "system") {
    return (
      <div className={`sys${line.translate ? "" : " sys-dim"}${pending ? " pending" : ""}`}>
        <span className="sys-mark">
          <Icon name="dice" size={13} stroke={1.6} />
        </span>
        <div className="sys-body">
          <span className="sys-text">{main}</span>
          {showOriginal && <span className="original sys-original">{line.text}</span>}
          {untranslated && tab === "ambos" && <Tag label="sin traducir" kind="warn" />}
        </div>
        <time>{line.time}</time>
      </div>
    );
  }

  const name = line.speaker ?? "?";
  const hue = speakerHue(name);
  const tag = channelTag(line.channel);
  return (
    <article
      className={`msg${grouped ? " grouped" : ""}${pending ? " pending" : ""}`}
      style={{ ["--h" as string]: hue }}
    >
      <div className="token" aria-hidden="true">
        {grouped ? null : initials(name)}
      </div>
      <div className="msg-main">
        {!grouped && (
          <header>
            <span className="speaker">{name}</span>
            {tag && <Tag label={tag.label} kind={tag.kind} />}
            {untranslated && <Tag label="sin traducir" kind="warn" />}
            <time>{line.time}</time>
          </header>
        )}
        <Message text={main} className="body" />
        {pending && <p className="translating">traduciendo…</p>}
        {showOriginal && <Message text={line.text} className="original" />}
        {untranslated && tab === "ambos" && <p className="reason">{tr?.untranslated}</p>}
      </div>
    </article>
  );
});

const OWN_STATUS: Record<OwnEvent["status"], { label: string; tone: string }> = {
  traduciendo: { label: "traduciendo…", tone: "dim" },
  esperando: { label: "a punto de salir (Esc lo cancela)", tone: "accent" },
  cola: { label: "en cola: el traductor está en pausa", tone: "warn" },
  escribiendo: { label: "escribiendo en el juego…", tone: "accent" },
  confirmado: { label: "enviado", tone: "ok" },
  enviado: { label: "enviado", tone: "ok" },
  sin_confirmar: { label: "sin confirmar", tone: "warn" },
  fallido: { label: "no enviado", tone: "warn" },
  cancelado: { label: "cancelado", tone: "dim" },
};

const OwnView = memo(function OwnView({ own }: { own: OwnEvent }) {
  const st = OWN_STATUS[own.status];
  const done = own.status === "confirmado" || own.status === "enviado";
  const main = own.en ?? own.es;
  return (
    <div className={`own own-${own.status}`}>
      <div className="own-meta">
        <span className={`own-status tone-${st.tone}`}>
          {done && <Icon name="check" size={13} stroke={2.4} />}
          {st.label}
        </span>
        <span className="own-name">Tú</span>
      </div>
      <div className="bubble">
        <Message text={main} className="body" />
        {own.en && !same(own.en, own.es) && <Message text={own.es} className="original" />}
        {own.status === "esperando" && own.delayMs ? (
          <span className="fuse" style={{ animationDuration: `${own.delayMs}ms` }} />
        ) : null}
      </div>
    </div>
  );
});

export function Chat({ items, tab }: { items: Item[]; tab: Tab }) {
  const scroller = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);
  const [unseen, setUnseen] = useState(0);
  const shown = useMemo(() => items.filter((it) => visible(it, tab)), [items, tab]);
  const lastKey = shown.length ? shown[shown.length - 1].key : "";
  const prevLast = useRef(lastKey);

  // Solo baja al final si ya estabas abajo; si estás leyendo más arriba, no te mueve.
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    if (atBottom.current) el.scrollTop = el.scrollHeight;
    else if (lastKey !== prevLast.current) setUnseen((n) => n + 1);
    prevLast.current = lastKey;
  }, [shown, lastKey]);

  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    atBottom.current = true;
    setUnseen(0);
    el.scrollTop = el.scrollHeight;
  }, [tab]);

  const onScroll = () => {
    const el = scroller.current;
    if (!el) return;
    atBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 32;
    if (atBottom.current) setUnseen(0);
  };

  const toBottom = () => {
    const el = scroller.current;
    if (!el) return;
    el.scrollTo({ top: el.scrollHeight, behavior: "smooth" });
  };

  let prevSpeaker: string | null = null;
  return (
    <div className="chat-wrap">
      <div className="chat" ref={scroller} onScroll={onScroll} role="log" aria-live="polite">
        {shown.length === 0 && (
          <div className="empty">
            <Icon name="d20" size={34} stroke={1.2} />
            <p>
              {tab === "ingles"
                ? "Aquí aparece el chat del juego tal cual, al instante."
                : "Cuando alguien escriba en el juego, aparecerá aquí traducido."}
            </p>
          </div>
        )}
        {shown.map((it) => {
          let grouped = false;
          if (it.kind === "line" && it.line.kind === "chat") {
            const sig = (it.line.speaker ?? "") + "|" + (it.line.channel ?? "");
            grouped = sig === prevSpeaker;
            prevSpeaker = sig;
          } else {
            prevSpeaker = null;
          }
          switch (it.kind) {
            case "line":
              return <LineView key={it.key} line={it.line} tr={it.tr} tab={tab} grouped={grouped} />;
            case "own":
              return <OwnView key={it.key} own={it.own} />;
            case "notice":
              return (
                <p key={it.key} className={`notice tone-${it.notice.tone}`}>
                  {it.notice.text}
                </p>
              );
          }
        })}
      </div>
      {unseen > 0 && (
        <button className="unseen" onClick={toBottom}>
          <Icon name="down" size={14} stroke={2} />
          {unseen === 1 ? "1 mensaje nuevo" : `${unseen} mensajes nuevos`}
        </button>
      )}
    </div>
  );
}
