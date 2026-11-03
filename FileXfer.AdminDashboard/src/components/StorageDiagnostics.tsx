import { useEffect, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { ServerHealthDto } from "../types/admin";

export function StorageDiagnostics({ server, onChanged }: { server: ServerHealthDto; onChanged: () => void }) {
  const { authorizedFetch, session } = useAuth();
  const canManage = session?.roles.some(role => role.toLowerCase() === "admin") ?? false;
  const [checked, setChecked] = useState<ServerHealthDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setChecked(null); }, [server.storageDiagnostic?.checkedUtc]);
  const current = checked ?? server;
  const diagnostic = current.storageDiagnostic;

  const recheck = async () => {
    if (busy || !canManage) return;
    setBusy(true); setError(null);
    try {
      const response = await authorizedFetch("/admin/monitoring/health/recheck", { method: "POST" });
      if (!response.ok) {
        const body = await response.json().catch(() => null) as { message?: string; detail?: string; title?: string } | null;
        throw new Error(body?.message ?? body?.detail ?? body?.title ?? `La comprobación respondió HTTP ${response.status}.`);
      }
      setChecked(await response.json() as ServerHealthDto);
      onChanged();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "No fue posible completar la comprobación del almacenamiento.");
    } finally { setBusy(false); }
  };

  return <section className="mt-4 rounded-2xl border border-white/15 bg-[#101721] p-4" aria-labelledby="storage-diagnostic-title">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <h4 id="storage-diagnostic-title" className="text-sm font-semibold text-white">Diagnóstico comprobado del almacenamiento</h4>
      <button type="button" disabled={busy || !canManage} onClick={recheck} className="rounded-xl border border-sky-300/30 bg-sky-300/10 px-3 py-2 text-xs font-semibold text-sky-100 hover:bg-sky-300/15 disabled:cursor-not-allowed disabled:opacity-50">{busy ? "Comprobando lectura, escritura y borrado…" : "Comprobar almacenamiento ahora"}</button>
    </div>
    {busy && <p role="status" className="mt-3 text-xs text-sky-100">El servicio está comprobando su acceso mediante un archivo temporal de diagnóstico. Espere el resultado.</p>}
    {error && <p role="alert" className="mt-3 rounded-lg border border-rose-400/30 bg-[#2b151b] p-3 text-xs text-rose-100">{error}</p>}
    {diagnostic ? <>
      <p role="status" className={`mt-3 text-sm font-semibold ${current.storageOk ? "text-emerald-200" : "text-rose-200"}`}>{diagnostic.message}</p>
      <dl className="mt-3 grid gap-3 text-xs sm:grid-cols-2">
        <div className="sm:col-span-2"><dt className="font-semibold text-slate-400">Ruta verificada</dt><dd className="mt-1 break-all font-mono text-slate-100">{diagnostic.path || "Sin configurar"}</dd></div>
        <div><dt className="font-semibold text-slate-400">Cuenta que ejecutó la prueba</dt><dd className="mt-1 break-all text-slate-100">{diagnostic.serviceIdentity || "Sin dato"}</dd></div>
        <div><dt className="font-semibold text-slate-400">Última comprobación</dt><dd className="mt-1 text-slate-100">{diagnostic.checkedUtc ? new Date(diagnostic.checkedUtc).toLocaleString() : "Sin dato"}</dd></div>
      </dl>
      <div className="mt-4 flex flex-wrap gap-2">{[
        { label: "Lectura", ok: diagnostic.canRead }, { label: "Escritura", ok: diagnostic.canWrite }, { label: "Borrado del temporal", ok: diagnostic.canDelete }
      ].map(test => <span key={test.label} className={`rounded-lg border px-2 py-1.5 text-xs ${test.ok ? "border-emerald-400/25 bg-[#10271f] text-emerald-100" : "border-rose-400/25 bg-[#2b151b] text-rose-100"}`}>{test.label}: {test.ok ? "Correcta" : "No confirmada"}</span>)}</div>
      <p className="mt-3 text-xs leading-5 text-slate-300"><span className="font-semibold text-white">Acción para esta causa: </span>{diagnostic.remediation}</p>
      <p className="mt-3 text-xs text-slate-400">{diagnostic.capacityAvailable ? `Capacidad medida: ${bytes(diagnostic.freeBytes)} libres de ${bytes(diagnostic.totalBytes)}.` : "Capacidad: sin medición. Esto no significa 0 % de uso ni invalida una prueba correcta de lectura, escritura y borrado."}</p>
      <p className="mt-2 font-mono text-[10px] text-slate-500">Diagnóstico: {diagnostic.code}</p>
    </> : <p className="mt-3 text-xs leading-5 text-slate-400">El servidor todavía no devuelve un diagnóstico detallado de acceso. Use la comprobación para solicitarlo.</p>}
    {!canManage && <p className="mt-3 text-xs text-amber-100">Se requiere rol administrador para ejecutar una comprobación inmediata.</p>}
  </section>;
}

function bytes(value?: number | null): string {
  if (value == null || !Number.isFinite(value)) return "sin dato";
  if (value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(units.length - 1, Math.floor(Math.log(value) / Math.log(1024)));
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 2)} ${units[index]}`;
}
