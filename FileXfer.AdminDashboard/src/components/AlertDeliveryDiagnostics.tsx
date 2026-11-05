import { useEffect, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { HelpTrigger } from "./ContextualHelp";

export interface AlertDelivery {
  id: string; eventId: string; transport: string; title: string; status: string;
  failedAttempts: number; failureCode?: string | null; failureExplanation: string; remediation: string;
  occurredUtc: string; updatedUtc: string; nextAttemptUtc?: string | null;
  rfc?: string | null; fileId?: number | null; fileName?: string | null; traceId?: string | null; relatedItem: string;
}
export interface AlertDeliveryPage {
  page: number; pageSize: number; totalCount: number; enabled: boolean; emailConfigured: boolean;
  webhookConfigured: boolean; maxDeliveryAttempts: number; configurationIssues: string[]; items: AlertDelivery[];
}
const states: Record<string, string> = {
  DeadLetter: "Intentos agotados", Pending: "Esperando reintento", Processing: "En entrega", Delivered: "Entregada"
};
const control = "min-h-10 rounded-lg border border-white/20 bg-[#10141b] px-3 py-2 text-xs text-slate-100 disabled:cursor-not-allowed disabled:opacity-40";
const date = (value: string) => new Date(value).toLocaleString("es-MX");

export function AlertDeliveryDiagnostics({ active = true }: { active?: boolean }) {
  const { authorizedFetch } = useAuth();
  const [status, setStatus] = useState("DeadLetter");
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
        const response = await authorizedFetch(`/admin/monitoring/alerts?status=${status}&page=${page}&pageSize=${pageSize}`, { signal: abort.signal });
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
  }, [active, authorizedFetch, page, pageSize, revision, status]);

  return <section className="mt-6 min-w-0 rounded-2xl border border-white/10 bg-[#141920] p-4 sm:p-6" aria-labelledby="alert-delivery-title">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <p className="text-[10px] font-semibold uppercase tracking-widest text-amber-200">Notificaciones y reintentos</p>
        <div className="mt-1 flex items-center gap-2"><h3 id="alert-delivery-title" className="text-lg font-semibold text-white">Entrega de alertas</h3><HelpTrigger title="Entrega de alertas" description="Estos registros cuentan los intentos de notificar un evento por correo o webhook. Una alerta puede corresponder a un archivo, una sesión o un evento del sistema." nextStep="Filtra por Intentos agotados y revisa el canal, la causa y el ID del evento antes de solicitar un reproceso." /></div>
        <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-300">Identifique qué aviso falló, por qué y a qué archivo o evento corresponde. Los intentos son de entrega de la notificación; no de subida del archivo.</p>
      </div>
      <button type="button" className={control} disabled={loading} onClick={() => setRevision(value => value + 1)}>{loading ? "Consultando…" : "Actualizar alertas"}</button>
    </div>
    <div className="mt-4 flex flex-wrap items-end gap-3">
      <label className="grid gap-1 text-xs text-slate-300">Estado de entrega
        <select className={control} value={status} onChange={event => { setStatus(event.target.value); setPage(1); }}>
          <option value="DeadLetter">Intentos agotados</option><option value="Pending">Esperando reintento</option>
          <option value="Processing">En entrega</option><option value="Delivered">Entregadas</option><option value="All">Todos</option>
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
      {data.items.length === 0 && <p className="mt-4 rounded-lg border border-white/10 p-4 text-sm text-slate-200">No hay alertas con este estado.</p>}
      <div className="mt-4 space-y-3">{data.items.map(item => <article key={item.id} className="rounded-xl border border-white/15 bg-[#0f131a] p-4 [overflow-wrap:anywhere]">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h4 className="text-sm font-semibold text-white">{item.title}</h4>
          <span className={`rounded-full border px-3 py-1 text-xs font-semibold ${item.status === "DeadLetter" ? "border-rose-300/30 bg-[#341820] text-rose-200" : item.status === "Delivered" ? "border-emerald-300/30 bg-[#10291f] text-emerald-200" : "border-amber-300/30 bg-[#282014] text-amber-100"}`}>{states[item.status] ?? item.status}</span>
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
