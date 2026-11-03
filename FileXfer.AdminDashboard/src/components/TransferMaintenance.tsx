import { FormEvent, useEffect, useRef, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { PendingCleanupResponse, PendingTransferDto, PendingTransfersResponse } from "../types/admin";

type MaintenanceAction = "remove" | "expire" | "reset";

export function canRemovePending(item: PendingTransferDto): boolean {
  return !item.isActive && (item.status === "Pending" || item.status === "Paused");
}

export function TransferMaintenance({ statisticsStartedUtc, onChanged }: {
  statisticsStartedUtc?: string | null;
  onChanged: () => void;
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
  const [revision, setRevision] = useState(0);
  const [action, setAction] = useState<MaintenanceAction | null>(null);
  const [reason, setReason] = useState("");
  const reasonInput = useRef<HTMLTextAreaElement>(null);
  const dialog = useRef<HTMLDivElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);

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
        setSelected(ids => ids.filter(id => data.items.some(item => item.uploadId === id && canRemovePending(item))));
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

  const selectable = pending?.items.filter(canRemovePending) ?? [];
  const allSelected = selectable.length > 0 && selectable.every(item => selected.includes(item.uploadId));
  const lastPage = Math.max(1, Math.ceil((pending?.totalCount ?? 0) / pageSize));
  const openAction = (next: MaintenanceAction) => { setReason(""); setError(null); setAction(next); };

  const execute = async (event: FormEvent) => {
    event.preventDefault();
    if (!action || busy || !canManage || reason.trim().length < 5 || reason.trim().length > 500) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const path = action === "reset" ? "statistics/reset" : action === "expire" ? "pending/cleanup-expired" : "pending/remove";
      const response = await authorizedFetch(`/admin/monitoring/${path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(action === "remove" ? { uploadIds: selected, reason: reason.trim() } : { reason: reason.trim() })
      });
      if (action === "reset") {
        const result = await readResponse<{ statisticsStartedUtc: string; message: string }>(response);
        setNotice(`${result.message} Nuevo corte: ${dateTime(result.statisticsStartedUtc)}.`);
      } else {
        const result = await readResponse<PendingCleanupResponse>(response);
        setNotice(`${result.message} Retiradas: ${result.removed}. Omitidas: ${result.skipped}. Con error: ${result.failed}. Espacio liberado: ${bytes(result.bytesReclaimed)}.`);
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
    <section className="rounded-3xl border border-white/10 bg-[#101319] p-5 sm:p-6" aria-labelledby="maintenance-title">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-amber-300/80">Continuidad y limpieza</p>
          <h3 id="maintenance-title" className="mt-1 text-xl font-semibold text-white">Transferencias pendientes</h3>
          <p className="mt-2 max-w-3xl text-xs leading-5 text-slate-400">Revise las copias parciales y su plazo de reanudación. Al retirarlas se libera su espacio y se conserva la constancia de cancelación. Una transmisión activa debe terminar o pausarse antes de poder retirarla.</p>
        </div>
        <button type="button" className={buttonClass} disabled={busy} onClick={() => setRevision(value => value + 1)}>{loading ? "Consultando…" : "Actualizar pendientes"}</button>
      </div>

      <div className="mt-5 rounded-xl border border-sky-400/20 bg-sky-400/[0.06] p-3 text-xs leading-5 text-sky-100">
        La limpieza automática revisa cada minuto el vencimiento individual del archivo. Si el servidor estuvo detenido, procesa los vencidos al iniciar. Los archivos activos se protegen y se revisan en el siguiente ciclo.
      </div>
      {!canManage && <p className="mt-3 text-xs text-amber-200">Su rol permite consultar. Para retirar pendientes, limpiar vencidas o reiniciar estadísticas se requiere una sesión con rol administrador.</p>}
      {notice && <p role="status" className="mt-4 rounded-xl border border-emerald-400/25 bg-emerald-400/10 p-3 text-sm text-emerald-100">{notice}</p>}
      {error && !action && <p role="alert" className="mt-4 rounded-xl border border-rose-400/25 bg-rose-400/10 p-3 text-sm text-rose-100">{error}</p>}

      <div className="mt-4 flex flex-wrap items-center gap-3">
        <button type="button" className={`${buttonClass} border-rose-400/30 text-rose-100`} disabled={!canManage || busy || loading || selected.length === 0} onClick={() => openAction("remove")}>Retirar seleccionadas ({selected.length})</button>
        <button type="button" className={buttonClass} disabled={!canManage || busy} onClick={() => openAction("expire")}>Limpiar vencidas ahora</button>
        <span className="text-xs text-slate-400">{pending ? `${pending.totalCount} pendientes en total` : "Consultando cola…"}</span>
      </div>

      <div className="mt-4 overflow-x-auto rounded-xl border border-white/10">
        <table className="w-full text-left text-xs">
          <thead className="bg-[#1c222c] text-slate-300"><tr>
            <th className="p-3"><input type="checkbox" aria-label="Seleccionar pendientes retirables de esta página" checked={allSelected} disabled={!canManage || busy || loading || selectable.length === 0} onChange={event => setSelected(event.target.checked ? selectable.map(item => item.uploadId) : [])} /></th>
            <th className="p-3">Archivo / RFC</th><th className="p-3">Estado</th><th className="p-3">Recibido / esperado</th><th className="p-3">Última actividad</th><th className="p-3">Vence la reanudación</th>
          </tr></thead>
          <tbody>{pending?.items.map(item => <tr key={item.uploadId} className="border-t border-white/[0.07] text-slate-300">
            <td className="p-3"><input type="checkbox" aria-label={`Retirar ${item.fileName}`} checked={selected.includes(item.uploadId)} disabled={!canManage || busy || loading || !canRemovePending(item)} onChange={event => setSelected(ids => event.target.checked ? [...ids, item.uploadId] : ids.filter(id => id !== item.uploadId))} /></td>
            <td className="max-w-xs break-words p-3"><span className="font-medium text-white">{item.fileName || "Sin nombre"}</span><span className="mt-1 block font-mono text-[10px] text-slate-500">{item.rfc}</span></td>
            <td className="p-3"><span className={`inline-flex whitespace-nowrap rounded-full border px-2 py-1 text-[10px] ${item.isActive ? "border-sky-400/30 bg-sky-400/10 text-sky-200" : item.isExpired ? "border-rose-400/30 bg-rose-400/10 text-rose-200" : "border-violet-400/30 bg-violet-400/10 text-violet-200"}`}>{item.isActive ? "En transmisión · protegida" : item.isExpired ? "Plazo vencido" : item.status === "Paused" ? "Pausada · reanudable" : "Pendiente"}</span></td>
            <td className="whitespace-nowrap p-3">{bytes(item.bytesReceived)} / {item.expectedLength == null ? "sin dato" : bytes(item.expectedLength)}</td>
            <td className="whitespace-nowrap p-3">{dateTime(item.lastActivityUtc)}</td>
            <td className="whitespace-nowrap p-3">{dateTime(item.resumeExpiresUtc)}</td>
          </tr>)}</tbody>
        </table>
        {pending?.items.length === 0 && <p className="p-6 text-center text-sm text-slate-400">No hay transferencias pendientes por revisar.</p>}
      </div>
      <div className="mt-4 flex flex-wrap items-center justify-between gap-3 text-xs text-slate-400">
        <label className="flex items-center gap-2">Por página <select className="rounded-lg border border-white/15 bg-[#171c24] px-2 py-1 text-white" value={pageSize} disabled={busy} onChange={event => { setPageSize(Number(event.target.value)); setPage(1); setSelected([]); }}>{[10, 25, 50, 100].map(size => <option key={size} value={size}>{size}</option>)}</select></label>
        <div className="flex items-center gap-3"><button type="button" className={buttonClass} disabled={busy || loading || page <= 1} onClick={() => { setPage(value => value - 1); setSelected([]); }}>Anterior</button><span>Página {page} de {lastPage}</span><button type="button" className={buttonClass} disabled={busy || loading || page >= lastPage} onClick={() => { setPage(value => value + 1); setSelected([]); }}>Siguiente</button></div>
      </div>

      <div className="mt-6 flex flex-wrap items-center justify-between gap-4 border-t border-white/10 pt-5">
        <div className="max-w-3xl"><h4 className="font-semibold text-white">Reiniciar estadísticas</h4><p className="mt-1 text-xs leading-5 text-slate-400">Inicia un nuevo período de medición para los resultados de transferencias. Conserva archivos, recibos, auditoría y pendientes reales. Salud, recursos y contadores de alertas siguen mostrando la operación real; no se ponen en cero.</p><p className="mt-2 text-[11px] text-amber-200">{statisticsStartedUtc ? `Último reinicio: ${dateTime(statisticsStartedUtc)}` : "Medición original, sin reinicio manual."}</p></div>
        <button type="button" className={buttonClass} disabled={!canManage || busy} onClick={() => openAction("reset")}>Reiniciar estadísticas…</button>
      </div>

      {action && <div className="fixed inset-0 z-[200] flex items-center justify-center bg-black/80 p-4" onKeyDown={event => {
        if (event.key === "Escape" && !busy) setAction(null);
        if (event.key === "Tab") {
          const focusable = dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled), textarea:not(:disabled)');
          if (!focusable?.length) return;
          const first = focusable[0], last = focusable[focusable.length - 1];
          if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
          else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        }
      }}>
        <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby="maintenance-confirm-title" aria-describedby="maintenance-confirm-detail" className="w-full max-w-lg rounded-2xl border border-amber-400/30 bg-[#101319] p-6 shadow-2xl">
          <h3 id="maintenance-confirm-title" className="text-xl font-semibold text-white">{action === "reset" ? "Confirmar nuevo período de estadísticas" : action === "expire" ? "Confirmar limpieza de vencidas" : `Confirmar retiro de ${selected.length} transferencia(s)`}</h3>
          <p id="maintenance-confirm-detail" className="mt-3 text-sm leading-6 text-slate-300">{action === "reset" ? "Los resultados de transferencias empezarán a contar desde este momento. Los archivos, recibos y auditoría permanecen. Salud, pendientes, recursos y contadores de alertas conservan el estado real." : action === "expire" ? "Se eliminarán las copias parciales cuyo plazo haya vencido. Las transmisiones activas se omiten y conservan su protección." : "Se eliminarán las copias parciales seleccionadas y ya no podrán reanudarse con esa sesión. Para enviarlas de nuevo deberá iniciarse una nueva carga."}</p>
          <form onSubmit={execute}>
            <label htmlFor="maintenance-reason" className="mt-4 block text-xs font-medium text-slate-300">Motivo para la bitácora (5 a 500 caracteres)</label>
            <textarea ref={reasonInput} id="maintenance-reason" minLength={5} maxLength={500} required rows={3} value={reason} disabled={busy} onChange={event => setReason(event.target.value)} className="mt-2 w-full rounded-xl border border-white/15 bg-[#080b10] p-3 text-sm text-white outline-none focus:border-amber-300/60" />
            {error && <p role="alert" className="mt-3 text-sm text-rose-200">{error}</p>}
            <div className="mt-5 flex justify-end gap-3"><button type="button" className={buttonClass} disabled={busy} onClick={() => setAction(null)}>Cancelar</button><button type="submit" className={`${buttonClass} border-amber-300/40 bg-amber-300/15 text-amber-100`} disabled={busy || reason.trim().length < 5}>{busy ? "Procesando, espere…" : "Confirmar operación"}</button></div>
          </form>
        </div>
      </div>}
    </section>
  );
}

const buttonClass = "rounded-xl border border-white/15 bg-white/[0.04] px-3 py-2 text-xs font-semibold text-slate-200 transition hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-amber-300 disabled:cursor-not-allowed disabled:opacity-40";

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
