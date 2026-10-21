import { useEffect, useRef, useState, type ReactNode } from "react";
import type { Backend } from "./api";
import { Icon } from "./icons";
import { THEMES } from "./themes";
import type { KeyCheck, Provider, ProviderInfo, Settings as S, StatusEvent } from "./types";
import { formatCount, formatSeconds } from "./text";

const PROVIDERS: Array<{ id: Provider; label: string }> = [
  { id: "Microsoft", label: "Microsoft" },
  { id: "Google", label: "Google Translate" },
  { id: "DeepL", label: "DeepL" },
];

function Switch({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      className="switch"
      onClick={() => onChange(!checked)}
    >
      <span />
    </button>
  );
}

function Row({ title, children, desc }: { title: string; desc?: ReactNode; children?: ReactNode }) {
  return (
    <div className="row">
      <div className="row-text">
        <span className="row-title">{title}</span>
        {desc && <span className="row-desc">{desc}</span>}
      </div>
      {children}
    </div>
  );
}

function Segmented<T extends string>({
  value,
  options,
  onChange,
  label,
}: {
  value: T;
  options: Array<{ id: T; label: string }>;
  onChange: (v: T) => void;
  label: string;
}) {
  return (
    <div className="segmented" role="radiogroup" aria-label={label}>
      {options.map((o) => (
        <button
          key={o.id}
          role="radio"
          aria-checked={o.id === value}
          className={o.id === value ? "on" : ""}
          onClick={() => onChange(o.id)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function SettingsPanel({
  backend,
  settings,
  status,
  update,
  onClose,
}: {
  backend: Backend;
  settings: S;
  status: StatusEvent | null;
  update: (patch: Partial<S>) => void;
  onClose: () => void;
}) {
  const p = settings.provider;
  const keyed = p !== "Google";
  const [key, setKey] = useState(p === "DeepL" ? settings.deeplKey : settings.microsoftKey);
  const [region, setRegion] = useState(settings.microsoftRegion);
  const [check, setCheck] = useState<KeyCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [info, setInfo] = useState<ProviderInfo | null>(null);
  const [glossary, setGlossary] = useState(settings.noTranslate.join(", "));
  const keyInput = useRef<HTMLInputElement>(null);
  const panel = useRef<HTMLElement>(null);

  useEffect(() => {
    setKey(p === "DeepL" ? settings.deeplKey : settings.microsoftKey);
    setCheck(null);
    if (keyed) backend.providerInfo(p).then(setInfo);
    else setInfo(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [p]);

  useEffect(() => {
    panel.current?.focus();
  }, []);

  // Las palabras protegidas se guardan al dejar de escribir.
  useEffect(() => {
    const t = setTimeout(() => {
      const list = glossary
        .split(/[,;\n]/)
        .map((s) => s.trim())
        .filter(Boolean)
        .filter((s, i, a) => a.findIndex((x) => x.toLowerCase() === s.toLowerCase()) === i);
      if (list.join("\u0001") !== settings.noTranslate.join("\u0001")) update({ noTranslate: list });
    }, 700);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [glossary]);

  async function runCheck() {
    if (!key.trim()) {
      setCheck({ ok: false, message: "Pega la clave y pulsa Comprobar." });
      return;
    }
    setChecking(true);
    setCheck({ ok: true, message: "Comprobando la clave…" });
    try {
      const r = await backend.checkKey(p, key.trim(), region.trim());
      setCheck(r);
      backend.providerInfo(p).then(setInfo);
    } finally {
      setChecking(false);
    }
  }

  const keyStatus: { text: string; warn: boolean } = check
    ? { text: check.message, warn: !check.ok }
    : !info
      ? { text: "", warn: false }
      : info.state === "noKey"
        ? {
            text:
              p === "DeepL"
                ? "Pega tu clave y pulsa Comprobar. Mientras tanto se usa Google."
                : "Pega la clave de tu recurso Translator (plan gratuito F0). Mientras tanto se usa Google.",
            warn: false,
          }
        : info.state === "quotaExhausted"
          ? { text: "Cuota gratuita agotada: se usa otro traductor y se vuelve a probar cada hora.", warn: true }
          : info.state === "invalidKey"
            ? { text: "La clave guardada ha dejado de funcionar: pulsa Comprobar o pega otra.", warn: true }
            : { text: "Clave guardada. Si se acaba la cuota, se usa el siguiente traductor.", warn: false };

  const usage = info && info.usageLimit ? info.usageCount! / info.usageLimit : null;
  const chain = status?.chain ?? [];

  return (
    <aside
      className="settings"
      ref={panel}
      tabIndex={-1}
      aria-label="Ajustes"
      onKeyDown={(e) => {
        if (e.key === "Escape") {
          e.stopPropagation();
          onClose();
        }
      }}
    >
      <header className="settings-head">
        <h2>Ajustes</h2>
        <button className="icon-btn" onClick={onClose} aria-label="Cerrar ajustes">
          <Icon name="close" size={18} />
        </button>
      </header>

      <section>
        <h3>Traductor</h3>
        <Segmented
          label="Traductor"
          value={p}
          options={PROVIDERS}
          onChange={(v) => {
            update({ provider: v });
            if (v !== "Google") setTimeout(() => keyInput.current?.focus(), 0);
          }}
        />
        {keyed && (
          <>
            <div className="key-row">
              <input
                ref={keyInput}
                className="field"
                type="password"
                value={key}
                spellCheck={false}
                placeholder={p === "DeepL" ? "Pega aquí tu clave de DeepL" : "Pega aquí tu clave de Microsoft Translator"}
                aria-label={p === "DeepL" ? "Clave de DeepL" : "Clave de Microsoft"}
                onChange={(e) => setKey(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && !checking && runCheck()}
              />
              {p === "Microsoft" && (
                <input
                  className="field region"
                  value={region}
                  spellCheck={false}
                  placeholder="Región"
                  aria-label="Región del recurso de Microsoft Translator (vacío si es global)"
                  onChange={(e) => setRegion(e.target.value)}
                  onKeyDown={(e) => e.key === "Enter" && !checking && runCheck()}
                />
              )}
              <button className="btn-outline" onClick={runCheck} disabled={checking}>
                {checking ? "Comprobando…" : "Comprobar"}
              </button>
            </div>
            {keyStatus.text && <p className={`help${keyStatus.warn ? " tone-warn" : ""}`}>{keyStatus.text}</p>}

            <div className="usage">
              <div className="bar" aria-hidden="true">
                <span style={{ width: `${Math.min(100, (usage ?? 0) * 100)}%` }} className={usage && usage >= 0.9 ? "warn" : ""} />
              </div>
              <p className="help">
                {info?.usageLimit
                  ? p === "Microsoft"
                    ? `≈ ${formatCount(info.usageCount ?? 0)} de ${formatCount(info.usageLimit)} caracteres gratis este mes (contados por esta app)`
                    : `${formatCount(info.usageCount ?? 0)} de ${formatCount(info.usageLimit)} caracteres este mes`
                  : "Pulsa Comprobar para ver el consumo de este mes."}
              </p>
            </div>

            <Row
              title="Usar Google si Microsoft y DeepL no funcionan"
              desc={
                <span className={status && !status.googleInChain ? "tone-warn" : ""}>
                  Orden: {chain.join(" → ") || "—"}
                  {status && !status.googleInChain ? ". Si fallan, los mensajes se muestran sin traducir." : ""}
                </span>
              }
            >
              <Switch
                label="Usar Google de respaldo"
                checked={settings.googleFallback}
                onChange={(v) => update({ googleFallback: v })}
              />
            </Row>
          </>
        )}
      </section>

      <section>
        <h3>Traducción</h3>
        <label className="stack">
          <span className="row-title">No traducir</span>
          <input
            className="field"
            value={glossary}
            spellCheck={false}
            placeholder="Seraphine, Kael, Aguas Profundas"
            onChange={(e) => setGlossary(e.target.value)}
          />
          <span className="row-desc">Nombres, lugares o jerga del servidor, separados por comas. Se quedan tal cual.</span>
        </label>
        <Row
          title="Solo el chat de jugadores"
          desc="El combate y los avisos del sistema salen sin traducir en la pestaña Inglés. Así no se gastan peticiones en las peleas."
        >
          <Switch label="Solo el chat de jugadores" checked={settings.filterSystem} onChange={(v) => update({ filterSystem: v })} />
        </Row>
      </section>

      <section>
        <h3>Envío al juego</h3>
        <div className="stack">
          <div className="slider-head">
            <span className="row-title">Esperar antes de enviar</span>
            <output>{formatSeconds(settings.sendDelayMs)}</output>
          </div>
          <input
            type="range"
            min={0}
            max={5000}
            step={500}
            value={settings.sendDelayMs}
            aria-label="Espera antes de enviar al juego"
            onChange={(e) => update({ sendDelayMs: Number(e.target.value) })}
          />
          <span className="row-desc">Tiempo para corregir: Esc cancela el mensaje y lo devuelve al campo de texto.</span>
        </div>
        <Row title="Volver al traductor después de enviar">
          <Switch label="Volver al traductor después de enviar" checked={settings.returnToApp} onChange={(v) => update({ returnToApp: v })} />
        </Row>
        <Row title="Tecla rápida" desc="Ctrl+Mayús+Espacio salta entre el juego y el traductor.">
          <Switch label="Tecla rápida" checked={settings.hotkey} onChange={(v) => update({ hotkey: v })} />
        </Row>
        <Row title="Avisar de los mensajes privados" desc="Con un sonido y haciendo parpadear la ventana.">
          <Switch label="Avisar de los mensajes privados" checked={settings.alertPrivate} onChange={(v) => update({ alertPrivate: v })} />
        </Row>
      </section>

      <section>
        <h3>Colores</h3>
        <div className="themes" role="radiogroup" aria-label="Colores">
          {THEMES.map((t) => (
            <button
              key={t.id}
              role="radio"
              aria-checked={settings.theme === t.id}
              className={`theme-card${settings.theme === t.id ? " on" : ""}`}
              data-theme={t.id}
              onClick={() => update({ theme: t.id })}
            >
              <span className="theme-sample">
                <span className="theme-dot" />
                <span className="theme-lines">
                  <span />
                  <span />
                </span>
              </span>
              <span className="theme-name">{t.name}</span>
              <span className="theme-desc">{t.desc}</span>
            </button>
          ))}
        </div>
      </section>

      <section>
        <h3>Ventana</h3>
        <Row title="Siempre encima del juego">
          <Switch label="Siempre encima" checked={settings.alwaysOnTop} onChange={(v) => update({ alwaysOnTop: v })} />
        </Row>
        <Row title="Compacta" desc="Sin título, pestañas ni línea de ayuda, para dejarla pequeña sobre el juego.">
          <Switch label="Ventana compacta" checked={settings.compact} onChange={(v) => update({ compact: v })} />
        </Row>
        <div className="stack">
          <div className="slider-head">
            <span className="row-title">Transparencia cuando no la usas</span>
            <output>{settings.inactiveOpacity} %</output>
          </div>
          <input
            type="range"
            min={40}
            max={100}
            step={10}
            value={settings.inactiveOpacity}
            aria-label="Opacidad de la ventana cuando no la estás usando"
            onChange={(e) => update({ inactiveOpacity: Number(e.target.value) })}
          />
          <span className="row-desc">Para ver el juego a través. Al volver a la ventana se ve entera.</span>
        </div>
      </section>
    </aside>
  );
}
