import { FormEvent, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { HelpTrigger } from "./ContextualHelp";
import { PendingCleanupResponse, PendingTransferDto, PendingTransfersResponse } from "../types/admin";

type MaintenanceAction = "remove" | "expire" | "reconcile" | "reset";
export interface MaintenanceRequest { kind: "expire" | "review"; nonce: number }

export function canRemovePending(item: PendingTransferDto): boolean {
  return !item.isActive && !item.protectionReason && (item.status === "Pending" || item.status === "Paused");
}

export function canReconcilePending(item: PendingTransferDto): boolean {
  return !item.isActive && item.canReconcile === true && (item.status === "Pending" || item.status === "Paused");
}

export function cleanupResultTone(result: PendingCleanupResponse): "success" | "warning" | "error" {
  if (result.failed > 0 && result.removed + (result.retiredPreservingFile ?? 0) === 0) return "error";
  return result.skipped > 0 || result.failed > 0 || result.removed + (result.retiredPreservingFile ?? 0) === 0 ? "warning" : "success";
}

export function TransferMaintenance({ statisticsStartedUtc, onChanged, requestAction }: {
  statisticsStartedUtc?: string | null;
  onChanged: () => void;
  requestAction?: MaintenanceRequest | null;
}) {
  const { authorizedFetch, session } = useAuth();
  const canManage = session?.roles.some(role => role.toLowerCase() === "admin") ?? false;
  const [pending, setPending] = useState<PendingTransfersResponse | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [selected, setSelected] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [operationResult, setOperationResult] = useState<PendingCleanupResponse | null>(null);
  const [revision, setRevision] = useState(0);
  const [action, setAction] = useState<MaintenanceAction | null>(null);
  const [reason, setReason] = useState("");
  const reasonInput = useRef<HTMLTextAreaElement>(null);
  const dialog = useRef<HTMLDivElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);
  const section = useRef<HTMLElement>(null);
  const resultPanel = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!requestAction) return;
    section.current?.scrollIntoView?.({ behavior: "smooth", block: "start" });
    section.current?.focus({ preventScroll: true });
    if (requestAction.kind === "expire" && canManage && !busy) {
      setReason(""); setError(null); setAction("expire");
    }
  }, [requestAction?.nonce]);

  useEffect(() => {
    if (operationResult) resultPanel.current?.scrollIntoView?.({ behavior: "smooth", block: "nearest" });
  }, [operationResult]);

  useEffect(() => {
    if (!action) return;
    previousFocus.current = document.activeElement as HTMLElement | null;
    reasonInput.current?.focus();
    return () => previousFocus.current?.focus();
  }, [action]);

  useEffect(() => {
    if (busy || action) { setLoading(false); return; }
    const controller = new AbortController();
    let loadingNow = false;
    const load = async () => {
      if (loadingNow) return;
      loadingNow = true;
      setLoading(true);
      try {
        const response = await authorizedFetch(`/admin/monitoring/pending?page=${page}&pageSize=${pageSize}`, {
          signal: controller.signal
        });
        const data = await readResponse<PendingTransfersResponse>(response);
        if (controller.signal.aborted) return;
        const lastPage = Math.max(1, Math.ceil(data.totalCount / pageSize));
        if (page > lastPage) { setPage(lastPage); return; }
        setPending(data);
        setSelected(ids => ids.filter(id => data.items.some(item => item.uploadId === id && (canRemovePending(item) || canReconcilePending(item)))));
        setError(null);
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "No se pudo consultar la cola.");
      } finally {
        loadingNow = false;
        if (!controller.signal.aborted) setLoading(false);
      }
    };
    void load();
    const timer = window.setInterval(load, 15_000);
    return () => { controller.abort(); window.clearInterval(timer); };
  }, [authorizedFetch, page, pageSize, revision, busy, action]);

  const selectable = pending?.items.filter(item => canRemovePending(item) || canReconcilePending(item)) ?? [];
  const removableIds = pending?.items.filter(item => selected.includes(item.uploadId) && canRemovePending(item)).map(item => item.uploadId) ?? [];
  const reconcilableIds = pending?.items.filter(item => selected.includes(item.uploadId) && canReconcilePending(item)).map(item => item.uploadId) ?? [];
  const allSelected = selectable.length > 0 && selectable.every(item => selected.includes(item.uploadId));
  const lastPage = Math.max(1, Math.ceil((pending?.totalCount ?? 0) / pageSize));
  const resultTone = operationResult ? cleanupResultTone(operationResult) : null;
  const openAction = (next: MaintenanceAction) => { if (!canManage || busy) return; setReason(""); setError(null); setAction(next); };

  const execute = async (event: FormEvent) => {
    event.preventDefault();
    if (!action || busy || !canManage) return;
    if (reason.trim().length < 5 || reason.trim().length > 500) {
      setError("Escriba un motivo de entre 5 y 500 caracteres para realizar la operación.");
      return;
    }
    setBusy(true);
    setError(null);
    setNotice(null);
    setOperationResult(null);
    try {
      const path = action === "reset" ? "statistics/reset" : action === "expire" ? "pending/cleanup-expired" : action === "reconcile" ? "pending/reconcile" : "pending/remove";
      const response = await authorizedFetch(`/admin/monitoring/${path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(action === "remove" || action === "reconcile"
          ? { uploadIds: action === "remove" ? removableIds : reconcilableIds, reason: reason.trim() }
          : { reason: reason.trim() })
      });
      if (action === "reset") {
        const result = await readResponse<{ statisticsStartedUtc: string; message: string }>(response);
        setNotice(`${result.message} Nuevo corte: ${dateTime(result.statisticsStartedUtc)}.`);
      } else {
        const result = await readResponse<PendingCleanupResponse>(response);
        setOperationResult(result);
      }
      setAction(null);
      setSelected([]);
      setRevision(value => value + 1);
      onChanged();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "La operación no pudo completarse. Intente actualizar los datos.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <section ref={section} id="pending-maintenance" tabIndex={-1} className="min-w-0 scroll-mt-5 rounded-2xl border border-white/10 bg-[#14171d] p-4 focus-visible:outline-2 focus-visible:outline-amber-300 sm:p-5" aria-labelledby="maintenance-title">
      <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-3">
        <div>
          <p className="text-[10px] font-semibold uppercase tracking-[0.17em] text-amber-200/85">Continuidad y limpieza</p>
          <div className="mt-1 flex items-center gap-2"><h3 id="maintenance-title" className="text-xl font-semibold text-white">Transferencias pendientes</h3><HelpTrigger title="Qué acción elegir en pendientes" description="Retirar elimina una carga parcial. Conciliar cierra el registro y conserva su archivo físico. Limpiar vencidas aplica la revisión de plazos de reanudación." nextStep="Lee el estado y la fecha límite de cada archivo antes de elegir. La pantalla de confirmación muestra qué operación quedará registrada." /></div>
          <p className="mt-1 max-w-3xl text-xs leading-5 text-slate-300">Retirar elimina un parcial propio. Conciliar cierra el registro y conserva su archivo físico. La bitácora guarda el resultado, el responsable y el motivo.</p>
        </div>
        <button type="button" className={buttonClass} disabled={busy} onClick={() => setRevision(value => value + 1)}>{loading ? "Consultando…" : "Actualizar pendientes"}</button>
      </div>

      <p className="mt-3 rounded-lg border border-amber-300/15 bg-amber-300/5 px-3 py-2 text-xs leading-5 text-slate-300">
        <strong className="mr-2 text-amber-100">Limpieza automática</strong>
        El servidor revisa los vencimientos al iniciar y cada minuto. Las transmisiones activas conservan su protección hasta el siguiente ciclo.
      </p>
      {!canManage && <p className="mt-3 text-xs text-amber-200">Su rol permite consultar. Retirar, conciliar, limpiar vencidas y reiniciar estadísticas requiere una sesión de administrador.</p>}
      {notice && <p role="status" className="mt-4 rounded-xl border border-emerald-400/25 bg-emerald-400/10 p-3 text-sm text-emerald-100">{notice}</p>}
      {operationResult && (
        <div ref={resultPanel} role={resultTone === "error" ? "alert" : "status"} className={`mt-4 rounded-xl border p-3 sm:p-4 ${resultTone === "success" ? "border-emerald-400/30 bg-[#10271f] text-emerald-100" : resultTone === "error" ? "border-rose-400/30 bg-[#2b151b] text-rose-100" : "border-amber-300/30 bg-[#2a2110] text-amber-100"}`}>
          <h4 className="text-sm font-semibold">Resultado de la operación</h4>
          <p className="mt-1 text-sm leading-5">{operationResult.message}</p>
          <ul className="mt-3 flex flex-wrap gap-2 text-xs">
            <li className={resultMetricClass}>Parciales eliminados: <strong>{operationResult.removed}</strong></li>
            <li className={resultMetricClass}>Registros cerrados conservando el archivo: <strong>{operationResult.retiredPreservingFile ?? 0}</strong></li>
            <li className={resultMetricClass}>Omitidos: <strong>{operationResult.skipped}</strong></li>
            <li className={resultMetricClass}>Errores: <strong>{operationResult.failed}</strong></li>
            <li className={resultMetricClass}>Espacio liberado: <strong>{bytes(operationResult.bytesReclaimed)}</strong></li>
          </ul>
          {operationResult.removed + (operationResult.retiredPreservingFile ?? 0) === 0 && (
            <p className="mt-3 text-xs font-semibold">Ningún pendiente fue retirado. Revise la causa y el siguiente paso de cada archivo.</p>
          )}
          {!!operationResult.items?.length && (
            <div tabIndex={0} role="region" aria-label="Resultados por archivo" className={`mt-3 ${tableScrollClass}`}>
              <table className="w-full min-w-[36rem] text-left text-xs">
                <thead className={tableHeaderClass}>
                  <tr><th scope="col" className={cellClass}>Archivo</th><th scope="col" className={cellClass}>Resultado</th><th scope="col" className={cellClass}>Causa y siguiente paso</th></tr>
                </thead>
                <tbody>{operationResult.items.map((item, index) => (
                  <tr key={`${item.uploadId}-${index}`} className={tableRowClass}>
                    <td className={`${cellClass} max-w-56 [overflow-wrap:anywhere] font-medium text-white`}>{item.fileName || item.uploadId}</td>
                    <td className={`${cellClass} min-w-32 font-semibold ${item.outcome === "Failed" ? "text-rose-200" : item.outcome === "Skipped" ? "text-amber-200" : "text-emerald-200"}`}>
                      {item.outcome === "Removed" ? "Parcial eliminado" : item.outcome === "RetiredPreservingFile" ? "Archivo conservado · registro cerrado" : item.outcome === "Skipped" ? "Omitido" : "Error"}
                    </td>
                    <td className={`${cellClass} min-w-56 leading-5`}>{item.message}<span className="mt-1 block font-mono text-[10px] text-slate-400">{item.code}</span></td>
                  </tr>
                ))}</tbody>
              </table>
            </div>
          )}
        </div>
      )}
      {error && !action && <p role="alert" className="mt-4 rounded-xl border border-rose-400/25 bg-rose-400/10 p-3 text-sm text-rose-100">{error}</p>}

      <div className="mt-4 flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Acciones de mantenimiento">
          <button type="button" className={`${buttonClass} border-rose-400/30 text-rose-100`} disabled={!canManage || busy || loading || removableIds.length === 0} onClick={() => openAction("remove")}>Retirar parciales ({removableIds.length})</button>
          <button type="button" className={`${buttonClass} border-amber-300/30 bg-amber-300/8 text-amber-100`} disabled={!canManage || busy} onClick={() => openAction("expire")}>Limpiar vencidas ahora</button>
          <button type="button" className={`${buttonClass} border-violet-400/30 text-violet-100`} disabled={!canManage || busy || loading || reconcilableIds.length === 0} onClick={() => openAction("reconcile")}>Conciliar seleccionadas ({reconcilableIds.length})</button>
        </div>
        <span className="rounded-md bg-white/5 px-2.5 py-1.5 text-xs font-medium tabular-nums text-slate-300">{pending ? `${pending.totalCount} pendientes en total` : "Consultando cola…"}</span>
      </div>

      <div tabIndex={0} role="region" aria-label="Lista de transferencias pendientes" className={`mt-3 ${tableScrollClass}`}>
        <table className="w-full min-w-[51rem] text-left text-xs">
          <thead className={tableHeaderClass}><tr>
            <th scope="col" className={cellClass}><input type="checkbox" className={checkboxClass} aria-label="Seleccionar pendientes retirables de esta página" checked={allSelected} disabled={!canManage || busy || loading || selectable.length === 0} onChange={event => setSelected(event.target.checked ? selectable.map(item => item.uploadId) : [])} /></th>
            <th scope="col" className={cellClass}>Archivo / RFC</th><th scope="col" className={cellClass}>Estado y causa</th><th scope="col" className={cellClass}>Recibido / esperado</th><th scope="col" className={cellClass}>Última actividad</th><th scope="col" className={cellClass}>Vence la reanudación</th>
          </tr></thead>
          <tbody>{pending?.items.map(item => <tr key={item.uploadId} className={`${tableRowClass} ${selected.includes(item.uploadId) ? "bg-amber-300/7" : "hover:bg-white/3"}`}>
            <td className={cellClass}><input type="checkbox" className={checkboxClass} aria-label={`Seleccionar ${item.fileName}`} checked={selected.includes(item.uploadId)} disabled={!canManage || busy || loading || !(canRemovePending(item) || canReconcilePending(item))} onChange={event => setSelected(ids => event.target.checked ? [...ids, item.uploadId] : ids.filter(id => id !== item.uploadId))} /></td>
            <td className={`${cellClass} min-w-36 max-w-64 [overflow-wrap:anywhere]`}>
              <span className="font-medium text-white">{item.fileName || "Sin nombre"}</span>
              <span className="mt-1 block font-mono text-[10px] text-slate-400">{item.rfc}</span>
            </td>
            <td className={`${cellClass} min-w-56 max-w-80`}><PendingState item={item} /></td>
            <td className={`${cellClass} whitespace-nowrap tabular-nums`}>{bytes(item.bytesReceived)} / {item.expectedLength == null ? "sin dato" : bytes(item.expectedLength)}</td>
            <td className={`${cellClass} whitespace-nowrap tabular-nums`}>{dateTime(item.lastActivityUtc)}</td>
            <td className={`${cellClass} whitespace-nowrap tabular-nums ${item.isExpired ? "text-rose-200" : ""}`}>{dateTime(item.resumeExpiresUtc)}</td>
          </tr>)}</tbody>
        </table>
        {pending?.items.length === 0 && <p className="p-6 text-center text-sm text-slate-400">No hay transferencias pendientes por revisar.</p>}
      </div>
      <div className="mt-3 flex flex-wrap items-center justify-between gap-2 text-xs text-slate-300">
        <label className="flex items-center gap-2">Por página <select className="min-h-10 rounded-lg border border-white/15 bg-[#101217] px-2 py-1 text-white focus-visible:outline-2 focus-visible:outline-amber-300" value={pageSize} disabled={busy} onChange={event => { setPageSize(Number(event.target.value)); setPage(1); setSelected([]); }}>{[10, 25, 50, 100].map(size => <option key={size} value={size}>{size}</option>)}</select></label>
        <div className="flex items-center gap-3"><button type="button" className={buttonClass} disabled={busy || loading || page <= 1} onClick={() => { setPage(value => value - 1); setSelected([]); }}>Anterior</button><span>Página {page} de {lastPage}</span><button type="button" className={buttonClass} disabled={busy || loading || page >= lastPage} onClick={() => { setPage(value => value + 1); setSelected([]); }}>Siguiente</button></div>
      </div>

      <div className="mt-5 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-white/10 bg-[#101217] p-3">
        <div className="max-w-3xl"><h4 className="text-sm font-semibold text-white">Período de estadísticas</h4><p className="mt-1 text-xs leading-5 text-slate-300">El reinicio abre un nuevo período de medición. Conserva archivos, recibos, auditoría, pendientes y el estado operativo actual.</p><p className="mt-1 text-xs text-amber-200">{statisticsStartedUtc ? `Último reinicio: ${dateTime(statisticsStartedUtc)}` : "Medición original, sin reinicio manual."}</p></div>
        <button type="button" className={buttonClass} disabled={!canManage || busy} onClick={() => openAction("reset")}>Reiniciar estadísticas…</button>
      </div>

      {action && <div className="fixed inset-0 z-[200] grid items-end justify-items-center overflow-y-auto bg-black/80 p-3 sm:items-center sm:p-5" onKeyDown={event => {
        if (event.key === "Escape" && !busy) setAction(null);
        if (event.key === "Tab") {
          const focusable = dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled), textarea:not(:disabled)');
          if (!focusable?.length) return;
          const first = focusable[0], last = focusable[focusable.length - 1];
          if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
          else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        }
      }}>
        <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby="maintenance-confirm-title" aria-describedby="maintenance-confirm-detail" className="max-h-[calc(100dvh-1.5rem)] w-full max-w-lg overflow-y-auto overscroll-contain rounded-2xl border border-amber-400/30 bg-[#14171d] p-4 shadow-2xl sm:max-h-[calc(100dvh-2.5rem)] sm:p-6">
          <p className="text-[10px] font-semibold uppercase tracking-[0.17em] text-amber-200/85">Mantenimiento administrativo</p>
          <h3 id="maintenance-confirm-title" className="mt-1 text-lg font-semibold text-white sm:text-xl">{action === "reset" ? "Confirmar nuevo período de estadísticas" : action === "expire" ? "Confirmar limpieza de vencidas" : action === "reconcile" ? `Conciliar ${reconcilableIds.length} registro(s) pendiente(s)` : `Confirmar retiro de ${removableIds.length} transferencia(s)`}</h3>
          <p id="maintenance-confirm-detail" className="mt-3 text-sm leading-6 text-slate-300">{action === "reset" ? "Los resultados de transferencias empezarán a contar desde este momento. Los archivos, recibos y auditoría permanecen. Salud, pendientes, recursos y contadores de alertas conservan el estado real." : action === "expire" ? "Se retirarán los pendientes cuyo plazo haya vencido. Se eliminan los parciales propios; cuando un archivo esté compartido se conserva el archivo y se cierra únicamente el registro vencido. Las sesiones activas se omiten. Al terminar verá la causa de cada resultado." : action === "reconcile" ? "Se cerrarán únicamente los registros pendientes seleccionados. Sus archivos físicos permanecerán intactos porque su ruta es compartida o necesita revisión. Se registra responsable y motivo; esta transferencia ya no podrá reanudarse y deberá iniciar un envío nuevo si lo necesita." : "Se eliminarán las copias parciales seleccionadas y ya no podrán reanudarse con esa sesión. Para enviarlas de nuevo deberá iniciarse una nueva carga."}</p>
          <form onSubmit={execute}>
            <label htmlFor="maintenance-reason" className="mt-4 block text-xs font-medium text-slate-300">Motivo para la bitácora (5 a 500 caracteres)</label>
            <textarea ref={reasonInput} id="maintenance-reason" aria-describedby="maintenance-reason-help" minLength={5} maxLength={500} required rows={3} value={reason} disabled={busy} onChange={event => setReason(event.target.value)} className="mt-2 w-full rounded-lg border border-white/15 bg-[#101217] p-2.5 text-base text-white outline-none focus:border-amber-300/60 sm:text-sm" />
            <p id="maintenance-reason-help" className={`mt-2 text-xs ${reason.trim().length < 5 ? "text-amber-200" : "text-slate-400"}`}>{reason.trim().length} / 500 caracteres útiles.{reason.trim().length < 5 ? ` Escriba al menos ${5 - reason.trim().length} carácter(es) más para habilitar Confirmar operación.` : " Motivo válido: puede confirmar la operación."}</p>
            {error && <p role="alert" className="mt-3 text-sm text-rose-200">{error}</p>}
            <div className="mt-4 flex flex-wrap justify-end gap-2 border-t border-white/10 pt-3"><button type="button" className={buttonClass} disabled={busy} onClick={() => setAction(null)}>Cancelar</button><button type="submit" className={`${buttonClass} border-amber-300/40 bg-amber-300/15 text-amber-100`} disabled={busy || reason.trim().length < 5}>{busy ? "Procesando, espere…" : "Confirmar operación"}</button></div>
          </form>
        </div>
      </div>}
    </section>
  );
}

function PendingState({ item }: { item: PendingTransferDto }) {
  const tone = item.isActive
    ? "border-sky-400/30 bg-sky-400/10 text-sky-200"
    : item.isExpired
      ? "border-rose-400/30 bg-rose-400/10 text-rose-200"
      : "border-violet-400/30 bg-violet-400/10 text-violet-200";
  const label = item.isActive ? "En transmisión · protegida" : item.isExpired ? "Plazo vencido" : item.status === "Paused" ? "Pausada · reanudable" : "Pendiente";

  return (
    <>
      <span className={`inline-flex rounded-md border px-2 py-1 text-[10px] font-semibold ${tone}`}>{label}</span>
      {item.protectionReason && (
        <p className="mt-2 text-xs leading-5 text-slate-300"><strong className="mr-1 text-amber-100">Protección</strong>{item.protectionReason}</p>
      )}
      {canReconcilePending(item) && <p className="mt-1 text-[11px] leading-5 text-violet-200">Seleccione y use Conciliar para cerrar el registro conservando su archivo.</p>}
    </>
  );
}

const buttonClass = "min-h-10 rounded-lg border border-white/15 bg-white/[0.04] px-3 py-2 text-xs font-semibold text-slate-200 transition-colors hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-amber-300 disabled:cursor-not-allowed disabled:opacity-40";
const resultMetricClass = "rounded-md border border-current/15 bg-black/15 px-2 py-1 tabular-nums";
const tableScrollClass = "max-h-[min(34rem,65dvh)] overflow-auto rounded-lg border border-white/10 bg-[#101217] focus-visible:outline-2 focus-visible:outline-amber-300";
const tableHeaderClass = "sticky top-0 z-10 bg-[#20242b] text-slate-200";
const tableRowClass = "border-t border-white/10 align-top text-slate-300";
const cellClass = "px-3 py-2.5";
const checkboxClass = "size-4 accent-amber-300 focus-visible:outline-2 focus-visible:outline-amber-300 disabled:opacity-35";

async function readResponse<T>(response: Response): Promise<T> {
  if (response.ok) return await response.json() as T;
  const content = await response.text();
  try {
    const body = JSON.parse(content) as { detail?: string; title?: string; message?: string; error?: string };
    throw new Error(body.detail ?? body.message ?? body.error ?? body.title ?? `La operación respondió HTTP ${response.status}.`);
  } catch (cause) {
    if (cause instanceof SyntaxError) throw new Error(`La operación respondió HTTP ${response.status}. Actualice e intente nuevamente.`);
    throw cause;
  }
}

function dateTime(value?: string | null): string {
  if (!value) return "Sin dato";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Sin dato" : date.toLocaleString();
}

function bytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(units.length - 1, Math.floor(Math.log(value) / Math.log(1024)));
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 2)} ${units[index]}`;
}
