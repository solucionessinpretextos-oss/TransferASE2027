import { createContext, useCallback, useContext, useEffect, useId, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import "./contextualHelp.css";

type Entry = { id: string; title: string; description: ReactNode; nextStep?: ReactNode; trigger: HTMLButtonElement };
type OpenEntry = Entry & { pinned: boolean };
type HelpContextValue = {
  active: OpenEntry | null;
  show: (entry: Entry, pin?: boolean) => void;
  scheduleHide: (id: string) => void;
  remove: (id: string) => void;
};
const HelpContext = createContext<HelpContextValue | null>(null);

export function ContextualHelpProvider({ children }: { children: ReactNode }) {
  const [active, setActive] = useState<OpenEntry | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const restoringFocus = useRef(false);
  const closeButton = useRef<HTMLButtonElement>(null);

  const cancelHide = useCallback(() => {
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = null;
  }, []);
  const show = useCallback((entry: Entry, pin = false) => {
    if (restoringFocus.current) return;
    cancelHide();
    setActive(current => current?.pinned && !pin ? current : { ...entry, pinned: pin });
  }, [cancelHide]);
  const scheduleHide = useCallback((id: string) => {
    cancelHide();
    timer.current = setTimeout(() => {
      setActive(current => current?.id === id && !current.pinned ? null : current);
    }, 450);
  }, [cancelHide]);
  const remove = useCallback((id: string) => {
    setActive(current => current?.id === id ? null : current);
  }, []);
  const close = useCallback(() => {
    cancelHide();
    setActive(null);
    if (active?.pinned && active.trigger.isConnected) {
      restoringFocus.current = true;
      try { active.trigger.focus({ preventScroll: true }); }
      finally { restoringFocus.current = false; }
    }
  }, [active, cancelHide]);

  useEffect(() => cancelHide, [cancelHide]);
  useEffect(() => {
    if (active?.pinned) closeButton.current?.focus({ preventScroll: true });
  }, [active?.id, active?.pinned]);
  useEffect(() => {
    if (!active) return;
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.defaultPrevented || document.querySelector('[role="dialog"][aria-modal="true"]')) return;
      event.preventDefault();
      close();
    };
    document.addEventListener("keydown", escape);
    return () => document.removeEventListener("keydown", escape);
  }, [active, close]);

  const value = useMemo(() => ({ active, show, scheduleHide, remove }), [active, show, scheduleHide, remove]);
  return <HelpContext.Provider value={value}>
    {children}
    {active && createPortal(
      <aside id={active.id} className="context-help-panel" role={active.pinned ? "dialog" : "tooltip"}
        aria-labelledby={`${active.id}-title`} onPointerEnter={cancelHide}
        onPointerLeave={() => { if (document.activeElement !== active.trigger) scheduleHide(active.id); }}>
        <header className="context-help-heading">
          <div><p className="context-help-eyebrow">Ayuda en pantalla</p><h2 id={`${active.id}-title`}>{active.title}</h2></div>
          {active.pinned && <button ref={closeButton} type="button" className="context-help-close" onClick={close} aria-label="Cerrar ayuda">×</button>}
        </header>
        <div className="context-help-description" key={active.id}>{active.description}</div>
        {active.nextStep && (active.pinned || typeof active.nextStep === "string") && <div className="context-help-next"><strong>Qué hacer</strong><div>{active.nextStep}</div></div>}
        <p className="context-help-hint">{active.pinned ? "Esc o Cerrar para volver al punto de partida." : "Pulsa el indicador de ayuda para mantener esta tarjeta abierta."}</p>
      </aside>, document.body)}
  </HelpContext.Provider>;
}

export function HelpTrigger({ title, description, nextStep, className, children, ariaLabel, clickOnly = false }: {
  title: string; description: ReactNode; nextStep?: ReactNode; className?: string;
  children?: ReactNode; ariaLabel?: string; clickOnly?: boolean;
}) {
  const id = useId();
  const help = useContext(HelpContext);
  const remove = help?.remove;
  const current = help?.active?.id === id ? help.active : null;
  useEffect(() => () => remove?.(id), [id, remove]);

  function show(trigger: HTMLButtonElement, pin = false) {
    help?.show({ id, title, description, nextStep, trigger }, pin);
  }
  return <button type="button"
    className={`context-help-trigger ${children == null ? "context-help-icon" : ""} ${className ?? ""}`}
    aria-label={ariaLabel ?? `Ayuda: ${title}`} aria-haspopup="dialog"
    aria-expanded={current?.pinned ?? false}
    aria-controls={current?.pinned ? id : undefined}
    aria-describedby={current && !current.pinned ? id : undefined}
    onFocus={event => { if (!clickOnly) show(event.currentTarget); }}
    onBlur={event => { if (!event.relatedTarget || !document.getElementById(id)?.contains(event.relatedTarget)) help?.scheduleHide(id); }}
    onPointerEnter={event => { if (!clickOnly && event.pointerType !== "touch") show(event.currentTarget); }}
    onPointerLeave={event => { if (document.activeElement !== event.currentTarget) help?.scheduleHide(id); }}
    onClick={event => show(event.currentTarget, true)}>
    {children ?? <span aria-hidden="true">i</span>}
  </button>;
}

export function HelpSteps({ steps }: { steps: ReadonlyArray<{ title: string; text: string }> }) {
  const [step, setStep] = useState(0);
  const selected = steps[step];
  if (!selected) return null;
  return <div className="context-help-steps">
    <div aria-live="polite"><p className="context-help-eyebrow">Paso {step + 1} de {steps.length}</p>
      <p><strong>{selected.title}</strong></p><p>{selected.text}</p></div>
    <div className="context-help-step-controls">
      <button type="button" disabled={step === 0} onClick={() => setStep(step - 1)}>Anterior</button>
      {step + 1 < steps.length
        ? <button type="button" onClick={() => setStep(step + 1)}>Siguiente</button>
        : <button type="button" onClick={() => setStep(0)}>Volver al primer paso</button>}
    </div>
  </div>;
}
