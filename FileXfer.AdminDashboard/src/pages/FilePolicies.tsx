import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { FilePolicyDto, FilePolicyRevisionDto } from "../types/admin";

type SortKey = "extension" | "isAllowed" | "requiresVerification" | "maxFileBytes" | "version" | "updatedUtc" | "updatedBy";
type SortDirection = "asc" | "desc";

const POLICY_COLLATOR = new Intl.Collator("es-MX", { sensitivity: "base", numeric: true });

function bytesToMb(value?: number | null): string {
  return value ? (value / 1024 / 1024).toString() : "";
}

function mbToBytes(value: string): number | null {
  if (!value.trim()) return null;
  const mb = Number(value);
  if (!Number.isFinite(mb) || mb <= 0) throw new Error("El límite debe ser un número positivo en MB.");
  return Math.round(mb * 1024 * 1024);
}

export function FilePolicies() {
  const { authorizedFetch } = useAuth();
  const [policies, setPolicies] = useState<FilePolicyDto[]>([]);
  const [selected, setSelected] = useState<FilePolicyDto | null>(null);
  const [history, setHistory] = useState<FilePolicyRevisionDto[]>([]);
  const [extension, setExtension] = useState("");
  const [allowed, setAllowed] = useState(true);
  const [verify, setVerify] = useState(true);
  const [maxMb, setMaxMb] = useState("");
  const [reason, setReason] = useState("");
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [search, setSearch] = useState("");
  const [sort, setSort] = useState<{ key: SortKey; direction: SortDirection }>({ key: "extension", direction: "asc" });

  const api = useCallback(async <T,>(path: string, init?: RequestInit): Promise<T> => {
    const headers = new Headers(init?.headers);
    if (init?.body) headers.set("Content-Type", "application/json");
    const response = await authorizedFetch(path, { ...init, headers });
    if (!response.ok) {
      const body = await response.text();
      try {
        const parsed = JSON.parse(body) as { message?: string; title?: string };
        throw new Error(parsed.message ?? parsed.title ?? `HTTP ${response.status}`);
      } catch (parseError) {
        if (parseError instanceof SyntaxError) throw new Error(body || `HTTP ${response.status}`);
        throw parseError;
      }
    }
    return (await response.json()) as T;
  }, [authorizedFetch]);

  const load = useCallback(async () => {
    setBusy(true);
    try {
      const data = await api<FilePolicyDto[]>("/admin/file-policies");
      setPolicies(data);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "No fue posible cargar las políticas.");
    } finally {
      setBusy(false);
    }
  }, [api]);

  useEffect(() => { void load(); }, [load]);

  const beginEdit = async (policy: FilePolicyDto) => {
    setSelected(policy);
    setExtension(policy.extension);
    setAllowed(policy.isAllowed);
    setVerify(policy.requiresVerification);
    setMaxMb(bytesToMb(policy.maxFileBytes));
    setReason("");
    setMessage(null);
    try {
      setHistory(await api<FilePolicyRevisionDto[]>(
        `/admin/file-policies/${encodeURIComponent(policy.extension)}/history`
      ));
    } catch (e) {
      setError(e instanceof Error ? e.message : "No fue posible cargar el historial.");
    }
  };

  const newPolicy = () => {
    setSelected(null);
    setExtension(".");
    setAllowed(true);
    setVerify(true);
    setMaxMb("");
    setReason("");
    setHistory([]);
    setMessage(null);
    setError(null);
  };

  const save = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const normalized = extension.trim().startsWith(".")
        ? extension.trim().toLowerCase()
        : `.${extension.trim().toLowerCase()}`;
      const saved = await api<FilePolicyDto>(
        `/admin/file-policies/${encodeURIComponent(normalized)}`,
        {
          method: "PUT",
          body: JSON.stringify({
            isAllowed: allowed,
            requiresVerification: verify,
            maxFileBytes: mbToBytes(maxMb),
            expectedVersion: selected?.version ?? 0,
            reason
          })
        }
      );
      setMessage(`${saved.extension} guardada como versión ${saved.version}.`);
      await load();
      await beginEdit(saved);
    } catch (e) {
      setError(e instanceof Error ? e.message : "No fue posible guardar la política.");
    } finally {
      setBusy(false);
    }
  };

  const rollback = async (revision: FilePolicyRevisionDto) => {
    if (!selected) return;
    const rollbackReason = window.prompt(
      `Motivo para restaurar ${selected.extension} a la versión ${revision.version}:`
    );
    if (!rollbackReason) return;
    setBusy(true);
    try {
      const restored = await api<FilePolicyDto>("/admin/file-policies/rollback", {
        method: "POST",
        body: JSON.stringify({
          revisionId: revision.id,
          expectedVersion: selected.version,
          reason: rollbackReason
        })
      });
      setMessage(`Rollback aplicado. Nueva versión efectiva: ${restored.version}.`);
      await load();
      await beginEdit(restored);
    } catch (e) {
      setError(e instanceof Error ? e.message : "No fue posible aplicar el rollback.");
    } finally {
      setBusy(false);
    }
  };

  const activeCount = useMemo(() => policies.filter(p => p.isAllowed).length, [policies]);
  const bypassCount = useMemo(
    () => policies.filter(p => p.isAllowed && !p.requiresVerification).length,
    [policies]
  );
  const visiblePolicies = useMemo(() => {
    const term = search.trim().toLocaleLowerCase("es-MX");
    const filtered = term
      ? policies.filter(policy => policy.extension.toLocaleLowerCase("es-MX").includes(term))
      : policies;

    return [...filtered].sort((left, right) => {
      let result = 0;
      switch (sort.key) {
        case "extension":
          result = POLICY_COLLATOR.compare(left.extension, right.extension);
          break;
        case "isAllowed":
          result = Number(left.isAllowed) - Number(right.isAllowed);
          break;
        case "requiresVerification":
          result = Number(left.requiresVerification) - Number(right.requiresVerification);
          break;
        case "maxFileBytes":
          result = (left.maxFileBytes ?? Number.MAX_SAFE_INTEGER) - (right.maxFileBytes ?? Number.MAX_SAFE_INTEGER);
          break;
        case "version":
          result = left.version - right.version;
          break;
        case "updatedUtc":
          result = new Date(left.updatedUtc).getTime() - new Date(right.updatedUtc).getTime();
          break;
        case "updatedBy":
          result = POLICY_COLLATOR.compare(left.updatedBy, right.updatedBy);
          break;
      }
      return sort.direction === "asc" ? result : -result;
    });
  }, [policies, search, sort]);

  const toggleSort = (key: SortKey) => {
    setSort(current => current.key === key
      ? { key, direction: current.direction === "asc" ? "desc" : "asc" }
      : { key, direction: "asc" });
  };

  return (
    <div className="space-y-6">
      <header className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-2xl font-semibold">Políticas de archivos</h2>
          <p className="text-sm text-slate-400">
            Controle extensiones, inspección profunda y límites opcionales. Cada cambio crea una versión auditable.
          </p>
        </div>
        <button onClick={newPolicy} className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold hover:bg-emerald-500">
          Nueva extensión
        </button>
      </header>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <Metric label="Configuradas" value={policies.length} />
        <Metric label="Habilitadas" value={activeCount} />
        <Metric label="Sin verificación profunda" value={bypassCount} danger={bypassCount > 0} />
      </div>

      {error && <div className="rounded-xl border border-rose-600/60 bg-rose-900/40 px-4 py-3 text-sm text-rose-100">{error}</div>}
      {message && <div className="rounded-xl border border-emerald-600/60 bg-emerald-900/40 px-4 py-3 text-sm text-emerald-100">{message}</div>}

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-[1.25fr_1fr]">
        <section className="overflow-hidden rounded-xl border border-slate-800 bg-slate-900/60">
          <div className="flex flex-col gap-3 border-b border-slate-800 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <p className="text-sm font-semibold">Políticas efectivas</p>
              <p className="mt-1 text-xs text-slate-500">Mostrando {visiblePolicies.length} de {policies.length}</p>
            </div>
            <label className="relative block w-full sm:max-w-xs">
              <span className="sr-only">Buscar por extensión</span>
              <span className="pointer-events-none absolute inset-y-0 left-3 flex items-center text-slate-500">⌕</span>
              <input
                type="search"
                value={search}
                onChange={event => setSearch(event.target.value)}
                placeholder="Buscar extensión, por ejemplo .pdf"
                className="w-full rounded-xl border border-white/10 bg-black/25 py-2.5 pl-9 pr-3 text-sm outline-none transition placeholder:text-slate-600 focus:border-amber-400/60 focus:ring-4 focus:ring-amber-400/5"
              />
            </label>
          </div>
          <div className="overflow-x-auto">
            <table className="w-full min-w-[1080px] text-left text-sm">
              <thead className="bg-slate-900 text-xs uppercase text-slate-400">
                <tr>
                  <SortableHeader label="Extensión" sortKey="extension" current={sort} onSort={toggleSort} className="px-4 py-3" />
                  <SortableHeader label="Estado" sortKey="isAllowed" current={sort} onSort={toggleSort} />
                  <SortableHeader label="Verificación" sortKey="requiresVerification" current={sort} onSort={toggleSort} />
                  <SortableHeader label="Límite por archivo" sortKey="maxFileBytes" current={sort} onSort={toggleSort} />
                  <SortableHeader label="Versión" sortKey="version" current={sort} onSort={toggleSort} />
                  <SortableHeader label="Actualizada" sortKey="updatedUtc" current={sort} onSort={toggleSort} />
                  <SortableHeader label="Responsable" sortKey="updatedBy" current={sort} onSort={toggleSort} />
                  <th className="pr-4"><span className="sr-only">Acciones</span></th>
                </tr>
              </thead>
              <tbody>
                {visiblePolicies.map(policy => (
                  <tr key={policy.id} className="border-t border-slate-800">
                    <td className="px-4 py-3 font-mono">{policy.extension}</td>
                    <td>{policy.isAllowed ? "Habilitada" : "Bloqueada"}</td>
                    <td className={policy.isAllowed && !policy.requiresVerification ? "font-semibold text-amber-300" : ""}>
                      {policy.requiresVerification ? "Profunda" : "Omitida"}
                    </td>
                    <td>{policy.maxFileBytes ? `${bytesToMb(policy.maxFileBytes)} MB` : "Sin límite"}</td>
                    <td>v{policy.version}</td>
                    <td className="text-xs text-slate-400">{new Date(policy.updatedUtc).toLocaleString()}</td>
                    <td className="max-w-40 truncate text-xs text-slate-400" title={policy.updatedBy}>{policy.updatedBy}</td>
                    <td className="pr-4 text-right"><button onClick={() => void beginEdit(policy)} className="text-emerald-300 hover:text-emerald-200">Editar</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!busy && visiblePolicies.length === 0 && (
              <div className="px-4 py-10 text-center">
                <p className="text-sm font-semibold text-slate-300">No se encontraron extensiones</p>
                <p className="mt-1 text-xs text-slate-500">Pruebe con otra búsqueda o limpie el filtro.</p>
                {search && <button type="button" onClick={() => setSearch("")} className="mt-3 text-sm font-semibold text-amber-300 hover:text-amber-200">Limpiar búsqueda</button>}
              </div>
            )}
          </div>
          {busy && policies.length === 0 && <p className="p-4 text-sm text-slate-400">Cargando políticas...</p>}
        </section>

        <div className="space-y-6">
          <form onSubmit={save} className="space-y-4 rounded-xl border border-slate-800 bg-slate-900/60 p-4">
            <h3 className="font-semibold">{selected ? `Editar ${selected.extension}` : "Nueva política"}</h3>
            <label className="block text-sm">Extensión
              <input value={extension} disabled={!!selected} onChange={e => setExtension(e.target.value)} className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 disabled:opacity-60" required />
            </label>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={allowed} onChange={e => setAllowed(e.target.checked)} /> Aceptar esta extensión</label>
            <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={verify} onChange={e => setVerify(e.target.checked)} /> Ejecutar verificación profunda</label>
            {allowed && !verify && (
              <div className="rounded-lg border border-amber-500/60 bg-amber-950/50 p-3 text-xs text-amber-100">
                Advertencia crítica: se validarán tamaño, hash y bloqueo de ejecutables, pero no contraseña, estructura ni reparación específica del formato.
              </div>
            )}
            <label className="block text-sm">Tamaño máximo opcional (MB, vacío = sin límite)
              <input type="number" min="0.01" step="0.01" value={maxMb} onChange={e => setMaxMb(e.target.value)} className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2" />
            </label>
            <label className="block text-sm">Motivo del cambio
              <textarea value={reason} onChange={e => setReason(e.target.value)} minLength={5} maxLength={512} className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2" required />
            </label>
            <button disabled={busy} className="w-full rounded-lg bg-emerald-600 px-4 py-2 font-semibold hover:bg-emerald-500 disabled:opacity-50">Guardar nueva versión</button>
          </form>

          {selected && (
            <section className="rounded-xl border border-slate-800 bg-slate-900/60 p-4">
              <h3 className="mb-3 font-semibold">Historial y rollback</h3>
              <div className="max-h-80 space-y-2 overflow-y-auto">
                {history.map(revision => (
                  <div key={revision.id} className="rounded-lg border border-slate-800 p-3 text-xs">
                    <div className="flex justify-between gap-2"><strong>v{revision.version}</strong><span>{new Date(revision.changedUtc).toLocaleString()}</span></div>
                    <p className="mt-1 text-slate-300">{revision.changeReason}</p>
                    <p className="text-slate-500">{revision.changedBy} · {revision.isAllowed ? "habilitada" : "bloqueada"} · {revision.requiresVerification ? "verificada" : "sin verificación"}</p>
                    {revision.version !== selected.version && <button onClick={() => void rollback(revision)} className="mt-2 text-amber-300 hover:text-amber-200">Restaurar esta configuración</button>}
                  </div>
                ))}
              </div>
            </section>
          )}
        </div>
      </div>
    </div>
  );
}

function SortableHeader({
  label,
  sortKey,
  current,
  onSort,
  className = ""
}: {
  label: string;
  sortKey: SortKey;
  current: { key: SortKey; direction: SortDirection };
  onSort: (key: SortKey) => void;
  className?: string;
}) {
  const active = current.key === sortKey;
  const ariaSort = active ? (current.direction === "asc" ? "ascending" : "descending") : "none";
  return (
    <th className={className} aria-sort={ariaSort}>
      <button
        type="button"
        onClick={() => onSort(sortKey)}
        className={`inline-flex items-center gap-1.5 py-3 text-left font-semibold transition hover:text-amber-200 ${active ? "text-amber-300" : "text-slate-400"}`}
      >
        {label}
        <span aria-hidden="true" className="text-[10px]">{active ? (current.direction === "asc" ? "▲" : "▼") : "↕"}</span>
      </button>
    </th>
  );
}

function Metric({ label, value, danger = false }: { label: string; value: number; danger?: boolean }) {
  return <div className={`rounded-xl border p-4 ${danger ? "border-amber-600/60 bg-amber-950/30" : "border-slate-800 bg-slate-900/60"}`}><p className="text-xs uppercase text-slate-400">{label}</p><p className="mt-1 text-2xl font-semibold">{value}</p></div>;
}
