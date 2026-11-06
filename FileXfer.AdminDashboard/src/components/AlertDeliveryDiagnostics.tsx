import { type FormEvent, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { HelpTrigger } from "./ContextualHelp";

export interface AlertDelivery {
  id: string; eventId: string; transport: string; title: string; status: string;
  failedAttempts: number; failureCode?: string | null; failureExplanation: string; remediation: string;
  occurredUtc: string; updatedUtc: string; nextAttemptUtc?: string | null;
  rfc?: string | null; fileId?: number | null; fileName?: string | null; traceId?: string | null; relatedItem: string;
  countsInCurrentPeriod: boolean;
}
export interface AlertDeliveryPage {
  page: number; pageSize: number; totalCount: number; enabled: boolean; emailConfigured: boolean;
  webhookConfigured: boolean; maxDeliveryAttempts: number; configurationIssues: string[]; items: AlertDelivery[];
  statisticsStartedUtc: string | null;
}
type AlertPeriod = "Current" | "All";
const states: Record<string, string> = {
  DeadLetter: "Intentos agotados", Pending: "Esperando reintento", Processing: "En entrega", Delivered: "Entregada"
};
const control = "min-h-10 rounded-lg border border-white/20 bg-[#10141b] px-3 py-2 text-xs text-slate-100 disabled:cursor-not-allowed disabled:opacity-40";
const date = (value: string) => new Date(value).toLocaleString("es-MX");
const alertsPath = "/admin/monitoring/alerts";

export function AlertDeliveryDiagnostics({ active = true, onReset }: { active?: boolean; onReset?: () => void }) {
  const { authorizedFetch, session } = useAuth();
  const isAdmin = session?.roles.some(role => role.toLowerCase() === "admin") ?? false;
  const [status, setStatus] = useState("DeadLetter");
  const [period, setPeriod] = useState<AlertPeriod>("Current");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(10);
  const [revision, setRevision] = useState(0);
  const [data, setData] = useState<AlertDeliveryPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    if (!active) return;
    const abort = new AbortController();
    let running = false;
    const load = async () => {
      if (running) return;
      running = true; setLoading(true);
      try {
        const response = await authorizedFetch(`${alertsPath}?status=${status}&page=${page}&pageSize=${pageSize}&period=${period}`, { signal: abort.signal });
        if (!response.ok) throw new Error(`No se pudo consultar la entrega de alertas (HTTP ${response.status}).`);
        const result = await response.json() as AlertDeliveryPage;
        if (!abort.signal.aborted) {
          setData(result); setError(null);
          const lastPage = Math.max(1, Math.ceil(result.totalCount / pageSize));
          if (page > lastPage) setPage(lastPage);
        }
      } catch (cause) {
        if (!abort.signal.aborted) { setData(null); setError(cause instanceof Error ? cause.message : "No se pudo consultar la entrega de alertas."); }
      } finally { running = false; if (!abort.signal.aborted) setLoading(false); }
    };
    setData(null); void load();
    const timer = window.setInterval(() => { void load(); }, 15_000);
    return () => { abort.abort(); window.clearInterval(timer); };
  }, [active, authorizedFetch, page, pageSize, period, revision, status]);

  return <section className="mt-6 min-w-0 rounded-2xl border border-white/10 bg-[#141920] p-4 sm:p-6" aria-labelledby="alert-delivery-title">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <p className="text-[10px] font-semibold uppercase tracking-widest text-amber-200">Notificaciones y reintentos</p>
        <div className="mt-1 flex items-center gap-2"><h3 id="alert-delivery-title" className="text-lg font-semibold text-white">Entrega de alertas</h3><HelpTrigger title="Entrega de alertas" description="Estos registros cuentan los intentos de notificar un evento por correo o webhook. Una alerta puede corresponder a un archivo, una sesión o un evento del sistema." nextStep="Filtra por Intentos agotados y revisa el canal, la causa y el ID del evento antes de solicitar un reproceso." /></div>
        <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-300">Identifique qué aviso falló, por qué y a qué archivo o evento corresponde. Los intentos son de entrega de la notificación; no de subida del archivo.</p>
      </div>
      <button type="button" className={control} disabled={loading} onClick={() => setRevision(value => value + 1)}>{loading ? "Consultando…" : "Actualizar alertas"}</button>
    </div>
    {data && <p className="mt-4 text-xs leading-5 text-slate-300">
      {data.statisticsStartedUtc ? <>Período de alertas iniciado el <time dateTime={data.statisticsStartedUtc}>{date(data.statisticsStartedUtc)}</time>.</> : "Período inicial, sin reinicio de estadísticas de alertas."}
    </p>}
    {active && isAdmin && <AlertStatisticsControls revision={revision} onReset={() => {
      setPage(1);
      setPeriod("Current");
      setRevision(value => value + 1);
      onReset?.();
    }} />}
    <div className="mt-4 flex flex-wrap items-end gap-3">
      <label className="grid gap-1 text-xs text-slate-300">Estado de entrega
        <select className={control} value={status} onChange={event => { setStatus(event.target.value); setPage(1); }}>
          <option value="DeadLetter">Intentos agotados</option><option value="Pending">Esperando reintento</option>
          <option value="Processing">En entrega</option><option value="Delivered">Entregadas</option><option value="All">Todos</option>
        </select>
      </label>
      <label className="grid gap-1 text-xs text-slate-300">Período de alertas
        <select className={control} value={period} onChange={event => { setPeriod(event.target.value as AlertPeriod); setPage(1); }}>
          <option value="Current">Período actual</option><option value="All">Todo el historial</option>
        </select>
      </label>
      <label className="grid gap-1 text-xs text-slate-300">Alertas por página
        <select className={control} value={pageSize} onChange={event => { setPageSize(Number(event.target.value)); setPage(1); }}>
          {[10, 25, 50, 100].map(size => <option key={size} value={size}>{size}</option>)}
        </select>
      </label>
    </div>
    {error && <p role="alert" className="mt-4 rounded-lg border border-rose-400/30 bg-[#2b151b] p-3 text-sm text-rose-100">{error}</p>}
    {data && <>
      <p className="mt-4 text-xs leading-5 text-slate-300">Alertamiento {data.enabled ? "habilitado" : "deshabilitado"} · SMTP {data.emailConfigured ? "configurado" : "sin configurar"} · Webhook {data.webhookConfigured ? "configurado" : "sin configurar"}. Configurado no significa entrega comprobada. Límite actual: {data.maxDeliveryAttempts} intentos fallidos por aviso y canal.</p>
      {data.configurationIssues.map(issue => <p key={issue} role="alert" className="mt-3 rounded-lg border border-amber-300/30 bg-[#282014] p-3 text-sm text-amber-100">{issue}</p>)}
      <p role="status" className="mt-3 text-xs text-slate-400">{data.totalCount} aviso(s) con el filtro seleccionado. Actualización cada 15 segundos mientras esta sección está abierta.</p>
      {data.items.length === 0 && <p className="mt-4 rounded-lg border border-white/10 p-4 text-sm text-slate-200">No hay alertas con este estado en el período seleccionado.{period === "Current" && data.statisticsStartedUtc && " Puedes consultar los avisos anteriores en Todo el historial."}</p>}
      <div className="mt-4 space-y-3">{data.items.map(item => <article key={item.id} className="rounded-xl border border-white/15 bg-[#0f131a] p-4 [overflow-wrap:anywhere]">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h4 className="text-sm font-semibold text-white">{item.title}</h4>
          <div className="flex flex-wrap items-center gap-2">
            {item.countsInCurrentPeriod === false && <span className="rounded-full border border-white/15 px-3 py-1 text-xs text-slate-300">Histórico · fuera del período actual</span>}
            <span className={`rounded-full border px-3 py-1 text-xs font-semibold ${item.status === "DeadLetter" ? "border-rose-300/30 bg-[#341820] text-rose-200" : item.status === "Delivered" ? "border-emerald-300/30 bg-[#10291f] text-emerald-200" : "border-amber-300/30 bg-[#282014] text-amber-100"}`}>{states[item.status] ?? item.status}</span>
          </div>
        </div>
        <p className="mt-2 text-sm text-slate-200">{item.relatedItem}</p>
        <dl className="mt-3 grid gap-3 text-xs sm:grid-cols-2 lg:grid-cols-4">
          <div><dt className="text-slate-400">Canal afectado</dt><dd className="mt-1 text-white">{item.transport === "email" ? "Correo SMTP" : item.transport === "webhook" ? "Webhook" : item.transport}</dd></div>
          <div><dt className="text-slate-400">Intentos fallidos acumulados</dt><dd className="mt-1 text-white">{item.failedAttempts} · límite actual {data.maxDeliveryAttempts}</dd></div>
          <div><dt className="text-slate-400">RFC relacionado</dt><dd className="mt-1 text-white">{item.rfc ?? "Evento sin RFC"}</dd></div>
          <div><dt className="text-slate-400">Último cambio</dt><dd className="mt-1 text-white">{date(item.updatedUtc)}</dd></div>
        </dl>
        {item.failureCode && <div className="mt-3 rounded-lg border border-amber-300/20 bg-[#211b13] p-3 text-xs leading-5 text-amber-100">
          <p><strong>Fallo de entrega: </strong>{item.failureExplanation}</p>
          <p className="mt-1"><strong>Cómo corregirlo: </strong>{item.remediation}</p>
          <p className="mt-1 font-mono">Código: {item.failureCode}</p>
        </div>}
        {item.status === "DeadLetter" && <p className="mt-3 text-xs leading-5 text-rose-200">Este aviso ya no se reintenta automáticamente. Corrija el canal y solicite el reproceso controlado de este evento. Reiniciar estadísticas no lo reenvía ni elimina su evidencia.</p>}
        {item.nextAttemptUtc && <p className="mt-3 text-xs text-slate-300">Próximo intento programado: {date(item.nextAttemptUtc)}</p>}
        <details className="mt-3 text-xs text-slate-400"><summary className="cursor-pointer text-slate-200">Identificadores para seguimiento</summary>
          <p className="mt-2">Evento: {item.eventId}</p><p>Entrega: {item.id}</p><p>Fecha del evento: {date(item.occurredUtc)}</p>
          {item.fileId != null && <p>Archivo del catálogo: {item.fileId} · {item.fileName ?? "Nombre histórico no disponible"}</p>}
          {item.traceId && <p>Traza: {item.traceId}</p>}
        </details>
      </article>)}</div>
      <div className="mt-4 flex items-center justify-end gap-3 text-xs text-slate-300">
        <button type="button" className={control} disabled={loading || page <= 1} onClick={() => setPage(value => value - 1)}>Anterior</button>
        <span>Página {page} de {Math.max(1, Math.ceil(data.totalCount / pageSize))}</span>
        <button type="button" className={control} disabled={loading || page * pageSize >= data.totalCount} onClick={() => setPage(value => value + 1)}>Siguiente</button>
      </div>
    </>}
  </section>;
}

interface AlertStatisticsAuditPage {
  page: number; pageSize: number; totalCount: number; statisticsStartedUtc: string | null;
  items: { id: string; timestampUtc: string; actorRfc: string | null; reason: string; previousStartedUtc: string | null; exhaustedBefore: number }[];
}

function AlertStatisticsControls({ revision, onReset }: { revision: number; onReset: () => void }) {
  const { authorizedFetch } = useAuth();
  const [confirming, setConfirming] = useState(false);
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [resetError, setResetError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [auditOpen, setAuditOpen] = useState(false);
  const [auditPage, setAuditPage] = useState(1);
  const [audit, setAudit] = useState<AlertStatisticsAuditPage | null>(null);
  const [auditError, setAuditError] = useState<string | null>(null);
  const dialog = useRef<HTMLDivElement>(null);
  const reasonInput = useRef<HTMLTextAreaElement>(null);
  const inFlight = useRef(false);
  const reasonLength = reason.trim().length;

  useEffect(() => {
    if (!confirming) return;
    const previousFocus = document.activeElement as HTMLElement | null;
    reasonInput.current?.focus();
    return () => previousFocus?.focus({ preventScroll: true });
  }, [confirming]);

  useEffect(() => {
    if (!auditOpen) return;
    const abort = new AbortController();
    setAudit(null);
    setAuditError(null);
    const load = async () => {
      try {
        const response = await authorizedFetch(`${alertsPath}/statistics?page=${auditPage}&pageSize=10`, { signal: abort.signal });
        if (!response.ok) throw new Error(`No se pudo consultar la bitácora (HTTP ${response.status}).`);
        const result = await response.json() as AlertStatisticsAuditPage;
        if (!abort.signal.aborted) setAudit(result);
      } catch (cause) {
        if (!abort.signal.aborted) setAuditError(cause instanceof Error ? cause.message : "No se pudo consultar la bitácora.");
      }
    };
    void load();
    return () => abort.abort();
  }, [auditOpen, auditPage, authorizedFetch, revision]);

  const reset = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (inFlight.current || !confirming || reasonLength < 5 || reasonLength > 500) return;
    inFlight.current = true;
    setSaving(true);
    reasonInput.current?.focus();
    setResetError(null);
    setNotice(null);
    try {
      const response = await authorizedFetch(`${alertsPath}/statistics/reset`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason: reason.trim() })
      });
      if (!response.ok) throw new Error(`No se pudo reiniciar las estadísticas (HTTP ${response.status}). Actualiza antes de volver a intentarlo.`);
      const result = await response.json() as { statisticsStartedUtc: string; message: string };
      setNotice(`${result.message} Nuevo corte: ${date(result.statisticsStartedUtc)}.`);
      setConfirming(false);
      setAuditPage(1);
      onReset();
    } catch (cause) {
      setResetError(cause instanceof Error ? cause.message : "No se pudo confirmar el reinicio. Actualiza antes de volver a intentarlo.");
    } finally {
      inFlight.current = false;
      setSaving(false);
    }
  };

  const auditLastPage = Math.max(1, Math.ceil((audit?.totalCount ?? 0) / (audit?.pageSize ?? 10)));
  return <div className="mt-4 space-y-3">
    <div className="flex flex-wrap gap-2">
      <button type="button" className={`${control} border-amber-300/40 text-amber-100`} onClick={() => {
        setReason("");
        setResetError(null);
        setConfirming(true);
      }}>Reiniciar estadísticas de alertas…</button>
      <button type="button" className={control} aria-controls="alert-reset-audit" aria-expanded={auditOpen} onClick={() => setAuditOpen(value => !value)}>
        {auditOpen ? "Ocultar bitácora de reinicios" : "Ver bitácora de reinicios"}
      </button>
    </div>
    {notice && <p role="status" className="rounded-lg border border-emerald-400/30 bg-[#10291f] p-3 text-sm text-emerald-100">{notice}</p>}
    <div id="alert-reset-audit" hidden={!auditOpen} className="space-y-3 rounded-xl border border-white/15 bg-[#0f131a] p-4">
      <h4 className="text-sm font-semibold text-white">Bitácora de reinicios de alertas</h4>
      {auditError && <p role="alert" className="text-sm text-rose-200">{auditError}</p>}
      {!audit && !auditError && <p role="status" className="text-sm text-slate-300">Consultando bitácora…</p>}
      {audit && (audit.items.length === 0 ? <p className="text-sm text-slate-300">Sin reinicios registrados en esta página.</p> :
        <ul className="space-y-2">{audit.items.map(entry => <li key={entry.id} className="rounded-lg border border-white/10 p-3 text-xs leading-5 text-slate-300 [overflow-wrap:anywhere]">
          <p><time dateTime={entry.timestampUtc}>{date(entry.timestampUtc)}</time> · <strong className="text-white">{entry.actorRfc ?? "RFC sin registro"}</strong></p>
          <p className="mt-1 whitespace-pre-wrap text-slate-100">{entry.reason}</p>
          <p className="mt-1">Agotadas del período anterior: {entry.exhaustedBefore}. {entry.previousStartedUtc ? `Inicio anterior: ${date(entry.previousStartedUtc)}.` : "Período anterior sin corte manual."}</p>
        </li>)}</ul>)}
      {audit && auditLastPage > 1 && <nav aria-label="Páginas de la bitácora" className="flex flex-wrap items-center justify-end gap-3 text-xs text-slate-300">
        <button type="button" className={control} disabled={auditPage <= 1} onClick={() => setAuditPage(value => value - 1)}>Anterior</button>
        <span>Página {auditPage} de {auditLastPage}</span>
        <button type="button" className={control} disabled={auditPage >= auditLastPage} onClick={() => setAuditPage(value => value + 1)}>Siguiente</button>
      </nav>}
    </div>
    {confirming && <div className="fixed inset-0 z-[200] grid items-end justify-items-center overflow-y-auto bg-black/80 p-3 sm:items-center sm:p-5" onKeyDown={event => {
      if (event.key === "Escape" && !inFlight.current) { event.preventDefault(); setConfirming(false); }
      if (event.key !== "Tab") return;
      const focusable = dialog.current?.querySelectorAll<HTMLElement>("textarea, button:not(:disabled)");
      if (!focusable?.length) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }}>
      <div ref={dialog} role="dialog" aria-modal="true" aria-busy={saving} aria-labelledby="alert-reset-title" aria-describedby="alert-reset-detail" className="max-h-[calc(100dvh-1.5rem)] w-full max-w-lg overflow-y-auto overscroll-contain rounded-2xl border border-amber-400/30 bg-[#14171d] p-4 shadow-2xl sm:max-h-[calc(100dvh-2.5rem)] sm:p-6">
        <h4 id="alert-reset-title" className="text-lg font-semibold text-white">Confirmar nuevo período de alertas</h4>
        <p id="alert-reset-detail" className="mt-3 text-sm leading-6 text-slate-300">Los avisos que ya agotaron sus intentos quedan en el historial y salen del conteo del período actual. Los avisos pendientes siguen su curso. La bitácora conserva la fecha, el RFC y el motivo de este reinicio.</p>
        <form onSubmit={reset}>
          <label htmlFor="alert-reset-reason" className="mt-4 block text-xs font-medium text-slate-300">Motivo para la bitácora (5 a 500 caracteres)</label>
          <textarea ref={reasonInput} id="alert-reset-reason" aria-describedby="alert-reset-length" required minLength={5} maxLength={500} rows={3} value={reason} readOnly={saving} onChange={event => setReason(event.target.value)} className="mt-2 w-full rounded-lg border border-white/15 bg-[#101217] p-2.5 text-base text-white outline-none focus:border-amber-300/60 sm:text-sm" />
          <p id="alert-reset-length" className="mt-2 text-xs text-slate-400">{reasonLength} de 500 caracteres útiles.</p>
          {resetError && <p role="alert" className="mt-3 text-sm text-rose-200">{resetError}</p>}
          <div className="mt-4 flex flex-wrap justify-end gap-2 border-t border-white/10 pt-3">
            <button type="button" className={control} disabled={saving} onClick={() => setConfirming(false)}>Cancelar</button>
            <button type="submit" className={`${control} border-amber-300/40 bg-amber-300/15 text-amber-100`} disabled={saving || reasonLength < 5 || reasonLength > 500}>{saving ? "Reiniciando…" : "Confirmar reinicio"}</button>
          </div>
        </form>
      </div>
    </div>}
  </div>;
}
