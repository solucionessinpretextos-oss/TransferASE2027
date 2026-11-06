import { type FormEvent, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth/AuthContext";

export type AlertChannel = "email" | "webhook";
export type AlertAction = { channel: AlertChannel; trigger: HTMLElement } & (
  { kind: "test" } | { kind: "reprocess"; id: string; title: string }
);

interface DeliveryProgress {
  id: string;
  eventId: string;
  status: string;
  failedAttempts: number;
  failureCode?: string | null;
  failureExplanation: string;
  remediation: string;
  updatedUtc: string;
  deliveredUtc?: string | null;
  nextAttemptUtc?: string | null;
}

const path = "/admin/monitoring/alerts";
const control = "min-h-10 rounded-lg border border-white/20 bg-[#10141b] px-3 py-2 text-sm text-slate-100 disabled:cursor-not-allowed disabled:opacity-40";
const labels: Record<AlertChannel, string> = { email: "correo SMTP", webhook: "webhook" };
const states: Record<string, string> = { Pending: "En cola o esperando reintento", Processing: "En entrega", Delivered: "Aceptada por el destino", DeadLetter: "Intentos agotados" };
const uuid = /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/i;
const terminal = (status?: string) => status === "Delivered" || status === "DeadLetter";
const date = (value: string) => new Date(value).toLocaleString("es-MX");

export function AlertDeliveryActions({ active, action, onDismiss, onChanged }: {
  active: boolean; action: AlertAction | null; onDismiss: () => void; onChanged: () => void;
}) {
  const { authorizedFetch, session } = useAuth();
  const [tracking, setTracking] = useState<{ id: string; channel: AlertChannel; title: string } | null>(null);
  const [progress, setProgress] = useState<DeliveryProgress | null>(null);
  const [trackingError, setTrackingError] = useState<string | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const requestIds = useRef(new Map<string, string>());
  const trackingId = tracking?.id;
  const completed = progress?.id === trackingId && terminal(progress?.status);

  useEffect(() => {
    if (trackingId) heading.current?.focus();
  }, [trackingId]);

  useEffect(() => {
    if (!active || !trackingId || completed) return;
    let disposed = false;
    let request: AbortController | null = null;
    let nextPoll: number | undefined;
    const load = async () => {
      const abort = new AbortController();
      request = abort;
      const timeout = window.setTimeout(() => abort.abort(), 12_000);
      try {
        const response = await authorizedFetch(`${path}/${encodeURIComponent(trackingId)}`, { signal: abort.signal });
        if (disposed) return;
        if (response.status === 401 || response.status === 403 || response.status === 404 || response.status === 410) {
          setTrackingError("La entrega ya no está disponible para esta sesión. Consulta la bitácora administrativa.");
          return;
        }
        if (!response.ok) throw new Error();
        const result = await response.json() as DeliveryProgress;
        if (disposed) return;
        if (result.id !== trackingId || !Object.hasOwn(states, result.status)) throw new Error();
        setProgress(result);
        setTrackingError(null);
        if (terminal(result.status)) { onChanged(); return; }
      } catch {
        if (disposed) return;
        setTrackingError("La consulta se interrumpió. El seguimiento volverá a intentarlo en unos segundos.");
      } finally {
        window.clearTimeout(timeout);
      }
      nextPoll = window.setTimeout(() => void load(), 3_000);
    };
    void load();
    return () => { disposed = true; request?.abort(); window.clearTimeout(nextPoll); };
  }, [active, authorizedFetch, completed, onChanged, trackingId]);

  const submit = async (selected: AlertAction, reason: string) => {
    const key = `filexfer:alert-action:${session?.rfc ?? "admin"}:${selected.kind}:${selected.kind === "test" ? selected.channel : selected.id}`;
    let requestId = requestIds.current.get(key);
    if (!requestId) {
      try { requestId = window.sessionStorage.getItem(key) ?? undefined; } catch { /* El seguimiento local sigue disponible. */ }
      if (!requestId || !uuid.test(requestId)) requestId = crypto.randomUUID();
      requestIds.current.set(key, requestId);
      try { window.sessionStorage.setItem(key, requestId); } catch { /* Se conserva la clave en esta pantalla. */ }
    }
    const forgetRequest = () => {
      requestIds.current.delete(key);
      try { window.sessionStorage.removeItem(key); } catch { /* El servidor confirmó el resultado de la solicitud. */ }
    };

    const endpoint = selected.kind === "test" ? `${path}/test` : `${path}/${encodeURIComponent(selected.id)}/reprocess`;
    const body = selected.kind === "test" ? { channel: selected.channel, requestId } : { reason, requestId };
    const abort = new AbortController();
    const timer = window.setTimeout(() => abort.abort(), 20_000);
    try {
      const response = await authorizedFetch(endpoint, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body), signal: abort.signal
      });
      const accepted = response.status === 202 || (response.status === 409 && selected.kind === "reprocess");
      const result = accepted ? await response.json() as { deliveryId?: string } : null;
      if (!result?.deliveryId || !uuid.test(result.deliveryId)) {
        if (response.status === 429) return "Ya hay una prueba reciente para este canal. Espera un minuto antes de solicitar otra.";
        if (response.status === 409) return "La acción no está disponible. Verifica el estado del aviso y la configuración del canal.";
        if (response.status === 410) {
          forgetRequest();
          return "El registro de esta solicitud venció. Consulta la bitácora para revisar la entrega.";
        }
        if (response.status === 401 || response.status === 403) return "La sesión no tiene permiso para ejecutar esta acción.";
        if (response.status === 404) return "El aviso seleccionado ya no está disponible.";
        if (response.status === 400) return "Revisa el motivo y vuelve a confirmar la solicitud.";
        throw new Error();
      }
      forgetRequest();
      setProgress(null);
      setTrackingError(null);
      setTracking({ id: result.deliveryId, channel: selected.channel, title: selected.kind === "test" ? `Prueba de ${labels[selected.channel]}` : `Reproceso de ${selected.title}` });
      onDismiss();
      onChanged();
      return null;
    } catch {
      return "No se pudo confirmar la solicitud. Al volver a intentar se conservará su identificador para evitar duplicarla.";
    } finally {
      window.clearTimeout(timer);
    }
  };

  if (!active) return null;
  return <>
    {tracking && <div className="mt-4 space-y-2 rounded-xl border border-white/15 bg-[#0f131a] p-4 text-sm text-slate-200" aria-labelledby="alert-action-tracking-title">
      <h4 ref={heading} tabIndex={-1} id="alert-action-tracking-title" className="font-semibold text-white">{tracking.title}</h4>
      <p className="text-xs text-slate-400 [overflow-wrap:anywhere]">Entrega: {tracking.id}</p>
      <div role="status" className="space-y-1 text-xs">
        {progress ? <>
          <p><strong>{states[progress.status]}</strong> · {progress.failedAttempts} {progress.failedAttempts === 1 ? "intento fallido" : "intentos fallidos"}</p>
          {progress.deliveredUtc && <p>Aceptación registrada el <time dateTime={progress.deliveredUtc}>{date(progress.deliveredUtc)}</time>.</p>}
          {progress.status === "Delivered" && tracking.channel === "email" && <p>El servidor SMTP aceptó el aviso. La recepción en el buzón se verifica por separado.</p>}
          {progress.nextAttemptUtc && <p>Próximo intento: <time dateTime={progress.nextAttemptUtc}>{date(progress.nextAttemptUtc)}</time>.</p>}
          {progress.failureCode && <p className="text-amber-200">{progress.failureExplanation} {progress.remediation}</p>}
        </> : <p>Solicitud registrada. Consultando el estado de la entrega…</p>}
        {trackingError && <p className="text-amber-200">{trackingError}</p>}
      </div>
    </div>}
    {action && <AlertActionConfirmation action={action} onDismiss={onDismiss} onSubmit={submit} />}
  </>;
}

function AlertActionConfirmation({ action, onDismiss, onSubmit }: {
  action: AlertAction; onDismiss: () => void; onSubmit: (action: AlertAction, reason: string) => Promise<string | null>;
}) {
  const reprocess = action.kind === "reprocess";
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dialog = useRef<HTMLDivElement>(null);
  const firstControl = useRef<HTMLButtonElement>(null);
  const reasonInput = useRef<HTMLTextAreaElement>(null);
  const inFlight = useRef(false);
  const submittedReason = useRef<string>();
  const reasonLength = reason.trim().length;
  const valid = !reprocess || (reasonLength >= 5 && reasonLength <= 500);

  useEffect(() => {
    if (reprocess) reasonInput.current?.focus();
    else firstControl.current?.focus();
    return () => { if (action.trigger.isConnected) action.trigger.focus({ preventScroll: true }); };
  }, [action, reprocess]);

  const confirm = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!valid || inFlight.current) return;
    inFlight.current = true;
    submittedReason.current ??= reason.trim();
    setSaving(true);
    setError(null);
    dialog.current?.focus();
    try {
      const result = await onSubmit(action, submittedReason.current);
      if (result) setError(result);
    } catch {
      setError("No se pudo preparar la solicitud. Verifica que el Dashboard se abrió mediante una conexión HTTPS válida.");
    } finally { inFlight.current = false; setSaving(false); }
  };

  return <div className="fixed inset-0 z-[200] grid items-end justify-items-center overflow-y-auto bg-black/80 p-3 sm:items-center sm:p-5" onKeyDown={event => {
    if (event.key === "Escape" && !inFlight.current) { event.preventDefault(); onDismiss(); }
    if (event.key !== "Tab") return;
    const focusable = dialog.current?.querySelectorAll<HTMLElement>("textarea:not([readonly]), button:not(:disabled)");
    if (!focusable?.length) { event.preventDefault(); return; }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && (document.activeElement === first || document.activeElement === dialog.current)) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  }}>
    <div ref={dialog} tabIndex={-1} role="dialog" aria-modal="true" aria-busy={saving} aria-labelledby="alert-operation-title" aria-describedby="alert-operation-detail" className="max-h-[calc(100dvh-1.5rem)] w-full max-w-lg overflow-y-auto rounded-2xl border border-amber-400/30 bg-[#14171d] p-4 shadow-2xl sm:p-6">
      <h4 id="alert-operation-title" className="text-lg font-semibold text-white">{reprocess ? "Confirmar reproceso del aviso" : `Confirmar prueba de ${labels[action.channel]}`}</h4>
      <p id="alert-operation-detail" className="mt-3 text-sm leading-6 text-slate-300">
        {reprocess ? <>Se enviará nuevamente <strong>{action.title}</strong> por {labels[action.channel]} al destino configurado. El aviso original permanecerá en el historial.</>
          : <>Se enviará un aviso sintético por {labels[action.channel]} al destino configurado en el servidor. La acción quedará registrada en la bitácora.</>}
      </p>
      <form onSubmit={confirm}>
        {reprocess && <>
          <label htmlFor="alert-operation-reason" className="mt-4 block text-sm text-slate-300">Motivo del reproceso (5 a 500 caracteres)</label>
          <textarea ref={reasonInput} id="alert-operation-reason" className="mt-2 w-full rounded-lg border border-white/15 bg-[#101217] p-2.5 text-base text-white sm:text-sm" required minLength={5} maxLength={500} rows={3} value={reason} readOnly={submittedReason.current !== undefined} onChange={event => setReason(event.target.value)} />
        </>}
        {error && <p role="alert" className="mt-3 text-sm text-rose-200">{error}</p>}
        <div className="mt-4 flex flex-wrap justify-end gap-2 border-t border-white/10 pt-3">
          <button ref={firstControl} type="button" className={control} disabled={saving} onClick={onDismiss}>Cancelar</button>
          <button type="submit" className={`${control} border-amber-300/40 bg-amber-300/15 text-amber-100`} disabled={saving || !valid}>{saving ? "Registrando solicitud…" : reprocess ? "Confirmar reproceso" : "Enviar prueba"}</button>
        </div>
      </form>
    </div>
  </div>;
}
