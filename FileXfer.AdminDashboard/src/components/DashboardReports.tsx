import { type FormEvent, useEffect, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { HelpTrigger } from "./ContextualHelp";

interface ReportPage<T> {
  page: number;
  pageSize: number;
  totalCount: number;
  items: T[];
}

interface AlertIncident {
  transport: string;
  failureCode: string;
  description: string;
  remediation: string;
  affectedCount: number;
  activeCount: number;
  exhaustedCount: number;
  firstObservedUtc: string;
  lastObservedUtc: string;
  lastSuccessfulDeliveryUtc?: string | null;
}

interface AuditEntry {
  id: string;
  timestampUtc: string;
  actorRfc: string | null;
  action: string;
  label: string;
  summary: string;
  deliveryId?: string | null;
  deliveryStatus?: string | null;
}

interface AuditPage extends ReportPage<AuditEntry> {
  actions: { value: string; label: string }[];
}

type AuditFilters = { from: string; to: string; rfc: string; action: string };
const emptyFilters: AuditFilters = { from: "", to: "", rfc: "", action: "" };
const control = "min-h-10 rounded-lg border border-white/20 bg-[#10141b] px-3 py-2 text-sm text-slate-100 disabled:cursor-not-allowed disabled:opacity-40";
const panel = "mt-6 min-w-0 rounded-2xl border border-white/10 bg-[#141920] p-4 sm:p-6";
const date = (value: string) => new Date(value).toLocaleString("es-MX");
const deliveryLabels: Record<string, string> = {
  Pending: "En cola", Processing: "En entrega", Delivered: "Aceptado por el canal", DeadLetter: "Intentos agotados"
};

function useReport<T>(path: string, active: boolean, refreshMs = 0, refreshKey = 0) {
  const { authorizedFetch } = useAuth();
  const [result, setResult] = useState<{ path: string; data: T | null; error: string | null } | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    if (!active) return;
    const abort = new AbortController();
    let running = false;
    const load = async () => {
      if (running) return;
      running = true;
      try {
        const response = await authorizedFetch(path, { signal: abort.signal });
        if (!response.ok) throw new Error(`No se pudo consultar el informe (HTTP ${response.status}).`);
        const data = await response.json() as T;
        if (!abort.signal.aborted) setResult({ path, data, error: null });
      } catch (cause) {
        if (!abort.signal.aborted) setResult({ path, data: null, error: cause instanceof Error ? cause.message : "No se pudo consultar el informe." });
      } finally {
        running = false;
      }
    };
    void load();
    const timer = refreshMs > 0 ? window.setInterval(() => { void load(); }, refreshMs) : undefined;
    return () => {
      abort.abort();
      if (timer !== undefined) window.clearInterval(timer);
    };
  }, [path, active, refreshMs, refreshKey, revision, authorizedFetch]);

  return {
    current: result?.path === path ? result : null,
    previousData: result?.data,
    reload: () => setRevision(value => value + 1)
  };
}

function ReportPager({ data, page, onPage, label }: { data: ReportPage<unknown>; page: number; onPage: (page: number) => void; label: string }) {
  const last = Math.max(1, Math.ceil(data.totalCount / data.pageSize));
  if (last === 1 && page === 1) return null;
  return <nav aria-label={label} className="mt-4 flex flex-wrap items-center justify-end gap-3 text-xs text-slate-300">
    <button type="button" className={control} disabled={page <= 1} onClick={() => onPage(Math.min(last, page - 1))}>Anterior</button>
    <span>Página {page} de {last}</span>
    <button type="button" className={control} disabled={page >= last} onClick={() => onPage(page + 1)}>Siguiente</button>
  </nav>;
}

function ReportStatus({ current, empty }: { current: { data: ReportPage<unknown> | null; error: string | null } | null; empty: string }) {
  if (!current) return <p role="status" className="mt-4 text-sm text-slate-300">Consultando…</p>;
  if (current.error) return <p role="alert" className="mt-4 rounded-lg border border-rose-400/30 bg-[#2b151b] p-3 text-sm text-rose-100">{current.error}</p>;
  return current.data?.items.length === 0 ? <p role="status" className="mt-4 text-sm text-slate-300">{empty}</p> : null;
}

export function AlertIncidents({ active, refreshKey }: { active: boolean; refreshKey: number }) {
  const [period, setPeriod] = useState("Current");
  const [page, setPage] = useState(1);
  const report = useReport<ReportPage<AlertIncident>>(`/admin/monitoring/alerts/incidents?period=${period}&page=${page}&pageSize=10`, active, 15_000, refreshKey);
  const data = report.current?.data;
  return <section className={panel} aria-labelledby="alert-incidents-title">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <div className="flex items-center gap-2"><h3 id="alert-incidents-title" className="text-lg font-semibold text-white">Incidencias de alertas</h3><HelpTrigger title="Incidencias de alertas" description="Agrupa los avisos con fallo registrado por canal y código. La primera observación es la más antigua disponible en el historial de entregas." nextStep="Revisa los avisos activos y compara el último fallo con la entrega más reciente que aceptó el canal." /></div>
        <p className="mt-2 text-sm text-slate-300">Compare causas recurrentes, avisos afectados y la última entrega aceptada por cada canal.</p>
      </div>
      <button type="button" className={control} onClick={report.reload}>Actualizar incidencias</button>
    </div>
    <label className="mt-4 inline-grid gap-1 text-xs text-slate-300">Período de incidencias
      <select className={control} value={period} onChange={event => { setPeriod(event.target.value); setPage(1); }}>
        <option value="Current">Período actual</option><option value="All">Todo el historial disponible</option>
      </select>
    </label>
    <ReportStatus current={report.current} empty="No hay incidencias en el período seleccionado." />
    {data && <>
      <ul className="mt-4 grid gap-3 lg:grid-cols-2">
        {data.items.map(item => <li key={`${item.transport}:${item.failureCode}`} className="min-w-0 space-y-3 rounded-xl border border-white/15 bg-[#0f131a] p-4 [overflow-wrap:anywhere]">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h4 className="font-semibold text-white">{item.transport === "email" ? "Correo SMTP" : item.transport === "webhook" ? "Webhook" : item.transport}</h4>
            <code className="rounded-md border border-amber-300/25 px-2 py-1 text-xs text-amber-100">{item.failureCode}</code>
          </div>
          <p className="text-sm text-slate-200">{item.description}</p>
          <p className="text-xs text-amber-100">{item.affectedCount} afectados · {item.activeCount} activos · {item.exhaustedCount} agotados</p>
          <dl className="grid gap-2 text-xs text-slate-300 sm:grid-cols-2">
            <div><dt>Primera observación disponible</dt><dd className="mt-1 text-white">{date(item.firstObservedUtc)}</dd></div>
            <div><dt>Última observación</dt><dd className="mt-1 text-white">{date(item.lastObservedUtc)}</dd></div>
            <div className="sm:col-span-2"><dt>Última entrega aceptada por el canal</dt><dd className="mt-1 text-white">{item.lastSuccessfulDeliveryUtc ? date(item.lastSuccessfulDeliveryUtc) : "Sin registro disponible"}</dd></div>
          </dl>
          <p className="border-t border-white/10 pt-3 text-xs leading-5 text-slate-300">{item.remediation}</p>
        </li>)}
      </ul>
      <ReportPager data={data} page={page} onPage={setPage} label="Páginas de incidencias" />
    </>}
  </section>;
}

export function OperationalAudit({ active }: { active: boolean }) {
  const [draft, setDraft] = useState<AuditFilters>(emptyFilters);
  const [filters, setFilters] = useState<AuditFilters>(emptyFilters);
  const [filterError, setFilterError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const query = new URLSearchParams({ page: String(page), pageSize: "20" });
  if (filters.from) query.set("from", new Date(`${filters.from}T00:00:00`).toISOString());
  if (filters.to) query.set("to", new Date(`${filters.to}T23:59:59.999`).toISOString());
  if (filters.rfc) query.set("rfc", filters.rfc);
  if (filters.action) query.set("action", filters.action);
  const report = useReport<AuditPage>(`/admin/monitoring/audit?${query}`, active);
  const data = report.current?.data;
  const change = (field: keyof AuditFilters, value: string) => setDraft(previous => ({ ...previous, [field]: value }));

  function applyFilters(event: FormEvent) {
    event.preventDefault();
    if (draft.from && draft.to && draft.from > draft.to) {
      setFilterError("La fecha inicial debe ser anterior o igual a la fecha final.");
      return;
    }
    setFilterError(null);
    setFilters({ ...draft, rfc: draft.rfc.trim().toUpperCase() });
    setPage(1);
    report.reload();
  }

  return <section className={panel} aria-labelledby="operational-audit-title">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <div className="flex items-center gap-2"><h3 id="operational-audit-title" className="text-lg font-semibold text-white">Bitácora administrativa</h3><HelpTrigger title="Bitácora administrativa" description="Reúne reinicios, cambios de políticas, accesos, mantenimiento y acciones de alertas. El RFC corresponde a quien ejecutó la acción." nextStep="Filtra por rango de fechas, RFC responsable o tipo de evento para reconstruir un cambio." /></div>
        <p className="mt-2 text-sm text-slate-300">Consulte quién hizo cada cambio, cuándo y cuál fue el resultado registrado. Fechas en hora local.</p>
      </div>
      <button type="button" className={control} onClick={report.reload}>Actualizar bitácora</button>
    </div>
    <form onSubmit={applyFilters} className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
      <label className="grid gap-1 text-xs text-slate-300">Fecha inicial<input className={control} type="date" value={draft.from} onChange={event => change("from", event.target.value)} /></label>
      <label className="grid gap-1 text-xs text-slate-300">Fecha final<input className={control} type="date" value={draft.to} onChange={event => change("to", event.target.value)} /></label>
      <label className="grid gap-1 text-xs text-slate-300">RFC responsable<input className={control} value={draft.rfc} maxLength={13} onChange={event => change("rfc", event.target.value)} /></label>
      <label className="grid min-w-0 gap-1 text-xs text-slate-300">Tipo de evento<select className={control} value={draft.action} onChange={event => change("action", event.target.value)}>
        <option value="">Todos los eventos</option>
        {(data?.actions ?? report.previousData?.actions ?? []).map(action => <option key={action.value} value={action.value}>{action.label}</option>)}
      </select></label>
      <div className="flex flex-wrap gap-2 sm:col-span-2 xl:col-span-4">
        <button type="submit" className={control}>Aplicar filtros</button>
        <button type="button" className={control} onClick={() => { setDraft(emptyFilters); setFilters(emptyFilters); setFilterError(null); setPage(1); }}>Limpiar filtros</button>
      </div>
      {filterError && <p role="alert" className="text-sm text-rose-200 sm:col-span-2 xl:col-span-4">{filterError}</p>}
    </form>
    <ReportStatus current={report.current} empty="No hay eventos con estos filtros." />
    {data && <>
      <p className="mt-4 text-xs text-slate-300">{data.totalCount} eventos coinciden con los filtros.</p>
      <ol className="mt-3 space-y-2">
        {data.items.map(item => <li key={item.id} className="rounded-xl border border-white/15 bg-[#0f131a] p-4 [overflow-wrap:anywhere]">
          <div className="flex flex-wrap justify-between gap-2">
            <h4 className="text-sm font-semibold text-white">{item.label}</h4>
            <time className="text-xs text-slate-300" dateTime={item.timestampUtc}>{date(item.timestampUtc)}</time>
          </div>
          <p className="mt-2 text-xs text-amber-100">{item.actorRfc ?? "Sistema"}</p>
          <p className="mt-2 whitespace-pre-wrap text-sm text-slate-300">{item.summary}</p>
          {item.deliveryId && <p className="mt-2 text-xs text-slate-300">
            Entrega <code>{item.deliveryId}</code>{item.deliveryStatus && <> · {deliveryLabels[item.deliveryStatus] ?? item.deliveryStatus}</>}
          </p>}
        </li>)}
      </ol>
      <ReportPager data={data} page={page} onPage={setPage} label="Páginas de la bitácora administrativa" />
    </>}
  </section>;
}
