import { useEffect, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { ServerHealthDto } from "../types/admin";
import { HelpTrigger } from "./ContextualHelp";

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

  return (
    <section className="mt-4 min-w-0 rounded-xl border border-white/10 bg-[#14171d] p-3 sm:p-4" aria-labelledby="storage-diagnostic-title">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-[10px] font-semibold uppercase tracking-[0.17em] text-amber-200/85">Prueba de acceso</p>
          <div className="mt-1 flex items-center gap-2"><h4 id="storage-diagnostic-title" className="text-sm font-semibold text-white">Diagnóstico comprobado del almacenamiento</h4><HelpTrigger title="Acceso al almacenamiento" description="Verifica si la cuenta del servicio puede leer, escribir y borrar un archivo temporal en la ubicación de trabajo. La capacidad del disco se mide por separado." nextStep="Revisa cuál comprobación falló. Un administrador puede repetirla desde esta sección después de corregir la ruta o sus permisos." /></div>
        </div>
        <button
          type="button"
          disabled={busy || !canManage}
          onClick={recheck}
          className="min-h-10 rounded-lg border border-amber-300/25 bg-amber-300/8 px-3 py-2 text-xs font-semibold text-amber-100 transition-colors hover:bg-amber-300/15 focus-visible:outline-2 focus-visible:outline-amber-300 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {busy ? "Comprobando lectura, escritura y borrado…" : "Comprobar almacenamiento ahora"}
        </button>
      </div>

      {busy && <p role="status" className="mt-3 rounded-lg border border-amber-300/20 bg-amber-300/5 px-3 py-2 text-xs leading-5 text-amber-100">El servicio está probando su acceso con un archivo temporal de diagnóstico.</p>}
      {error && <p role="alert" className="mt-3 rounded-lg border border-rose-400/30 bg-[#2b151b] p-3 text-xs text-rose-100">{error}</p>}
      {diagnostic ? (
        <>
          <div role="status" className={`mt-3 rounded-lg border p-3 ${current.storageOk ? "border-emerald-400/20 bg-[#10211b] text-emerald-100" : "border-rose-400/25 bg-[#28191e] text-rose-100"}`}>
            <p className="text-[10px] font-semibold uppercase tracking-wide">{current.storageOk ? "Acceso operativo" : "Acceso requiere revisión"}</p>
            <p className="mt-1 text-sm leading-5">{diagnostic.message}</p>
          </div>
          <ul className="mt-3 grid gap-2 sm:grid-cols-3" aria-label="Resultados de la prueba de acceso">
            {[
              { label: "Lectura", ok: diagnostic.canRead },
              { label: "Escritura", ok: diagnostic.canWrite },
              { label: "Borrado del temporal", ok: diagnostic.canDelete }
            ].map(test => (
              <li key={test.label} className={`flex flex-wrap items-baseline justify-between gap-x-2 gap-y-1 rounded-lg border px-2.5 py-2 text-xs ${test.ok ? "border-emerald-400/20 bg-[#10211b] text-emerald-100" : "border-rose-400/20 bg-[#28191e] text-rose-100"}`}>
                <span>{test.label}</span>
                <strong>{test.ok ? "Correcta" : "No confirmada"}</strong>
              </li>
            ))}
          </ul>
          <div className="mt-3 rounded-lg border border-amber-300/15 bg-amber-300/5 p-3">
            <p className="text-xs font-semibold text-amber-100">Siguiente paso</p>
            <p className="mt-1 text-xs leading-5 text-slate-200">{diagnostic.remediation}</p>
          </div>
          <dl className="mt-3 grid gap-x-4 gap-y-3 rounded-lg border border-white/10 bg-[#101217] p-3 text-xs sm:grid-cols-2">
            <div className="min-w-0 sm:col-span-2">
              <dt className="font-semibold text-slate-400">Ruta verificada</dt>
              <dd className="mt-1 font-mono text-slate-100 [overflow-wrap:anywhere]">{diagnostic.path || "Sin configurar"}</dd>
            </div>
            <div className="min-w-0">
              <dt className="font-semibold text-slate-400">Cuenta que ejecutó la prueba</dt>
              <dd className="mt-1 text-slate-100 [overflow-wrap:anywhere]">{diagnostic.serviceIdentity || "Sin dato"}</dd>
            </div>
            <div>
              <dt className="font-semibold text-slate-400">Última comprobación</dt>
              <dd className="mt-1 tabular-nums text-slate-100">{diagnostic.checkedUtc ? new Date(diagnostic.checkedUtc).toLocaleString() : "Sin dato"}</dd>
            </div>
            <div className="border-t border-white/10 pt-2 sm:col-span-2">
              <dt className="sr-only">Capacidad</dt>
              <dd className="leading-5 text-slate-300">{diagnostic.capacityAvailable ? `Capacidad medida: ${bytes(diagnostic.freeBytes)} libres de ${bytes(diagnostic.totalBytes)}.` : "Capacidad: sin medición. La capacidad y el acceso se comprueban por separado."}</dd>
              <dt className="sr-only">Código de diagnóstico</dt>
              <dd className="mt-1 font-mono text-[10px] text-slate-400 [overflow-wrap:anywhere]">Diagnóstico: {diagnostic.code}</dd>
            </div>
          </dl>
        </>
      ) : <p className="mt-3 text-xs leading-5 text-slate-300">El servidor todavía no devuelve un diagnóstico detallado de acceso. Use la comprobación para solicitarlo.</p>}
      {!canManage && <p className="mt-3 text-xs text-amber-100">Se requiere rol administrador para ejecutar una comprobación inmediata.</p>}
    </section>
  );
}

function bytes(value?: number | null): string {
  if (value == null || !Number.isFinite(value)) return "sin dato";
  if (value <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(units.length - 1, Math.floor(Math.log(value) / Math.log(1024)));
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 2)} ${units[index]}`;
}
