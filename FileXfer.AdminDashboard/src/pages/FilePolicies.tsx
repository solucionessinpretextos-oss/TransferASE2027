import { FormEvent, ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { HelpTrigger } from "../components/ContextualHelp";
import { FilePolicyDto, FilePolicyRevisionDto } from "../types/admin";
import "./FilePolicies.css";

type SortKey = "extension" | "isAllowed" | "requiresVerification" | "maxFileBytes" | "version" | "updatedUtc" | "updatedBy";
type SortDirection = "asc" | "desc";

const POLICY_COLLATOR = new Intl.Collator("es-MX", { sensitivity: "base", numeric: true });
const POLICY_COLUMNS = [
  { key: "extension", label: "Extensión", width: "12%" },
  { key: "isAllowed", label: "Estado", width: "12%" },
  { key: "requiresVerification", label: "Verificación", width: "13%" },
  { key: "maxFileBytes", label: "Límite por archivo", width: "15%" },
  { key: "version", label: "Versión", width: "9%" },
  { key: "updatedUtc", label: "Actualizada", width: "15%" },
  { key: "updatedBy", label: "Responsable", width: "14%" }
] satisfies { key: SortKey; label: string; width: string }[];

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
  const { authorizedFetch, session } = useAuth();
  const canManage = session?.roles.some(role => role.toLowerCase() === "admin") ?? false;
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
  const [pageSize, setPageSize] = useState(10);
  const [page, setPage] = useState(1);
  const [editorOpen, setEditorOpen] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(false);
  const latestHistory = useRef(0);
  const editorHeading = useRef<HTMLHeadingElement>(null);
  const editorTrigger = useRef<HTMLButtonElement | null>(null);
  const searchInput = useRef<HTMLInputElement>(null);

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
  useEffect(() => {
    if (editorOpen) editorHeading.current?.focus();
  }, [editorOpen, selected?.id]);

  const beginEdit = async (policy: FilePolicyDto, trigger?: HTMLButtonElement) => {
    if (trigger) {
      editorTrigger.current = trigger;
      setMessage(null);
      editorHeading.current?.focus();
    }
    const request = ++latestHistory.current;
    setEditorOpen(true);
    setSelected(policy);
    setExtension(policy.extension);
    setAllowed(policy.isAllowed);
    setVerify(policy.requiresVerification);
    setMaxMb(bytesToMb(policy.maxFileBytes));
    setReason("");
    setError(null);
    setHistory([]);
    setHistoryLoading(true);
    try {
      const revisions = await api<FilePolicyRevisionDto[]>(
        `/admin/file-policies/${encodeURIComponent(policy.extension)}/history`
      );
      if (request === latestHistory.current) setHistory(revisions);
    } catch (e) {
      if (request === latestHistory.current) setError(e instanceof Error ? e.message : "No fue posible cargar el historial.");
    } finally {
      if (request === latestHistory.current) setHistoryLoading(false);
    }
  };

  const newPolicy = (trigger: HTMLButtonElement) => {
    if (!canManage || busy) return;
    editorTrigger.current = trigger;
    editorHeading.current?.focus();
    latestHistory.current++;
    setEditorOpen(true);
    setHistoryLoading(false);
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

  const closeEditor = () => {
    latestHistory.current++;
    setEditorOpen(false);
    setMessage(null);
    setError(null);
    if (editorTrigger.current?.isConnected) editorTrigger.current.focus();
    else searchInput.current?.focus();
  };

  const save = async (event: FormEvent) => {
    event.preventDefault();
    if (!canManage || busy || !editorOpen) return;
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
    if (!selected || !canManage || busy) return;
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
  const totalPages = Math.max(1, Math.ceil(visiblePolicies.length / pageSize));
  const currentPage = Math.min(page, totalPages);
  const pageStart = visiblePolicies.length === 0 ? 0 : (currentPage - 1) * pageSize + 1;
  const pageEnd = Math.min(currentPage * pageSize, visiblePolicies.length);
  const pagedPolicies = useMemo(
    () => visiblePolicies.slice((currentPage - 1) * pageSize, currentPage * pageSize),
    [visiblePolicies, currentPage, pageSize]
  );

  useEffect(() => {
    setPage(1);
  }, [search, sort.key, sort.direction, pageSize]);

  useEffect(() => {
    setPage(current => Math.min(current, totalPages));
  }, [totalPages]);

  const toggleSort = (key: SortKey) => {
    setSort(current => current.key === key
      ? { key, direction: current.direction === "asc" ? "desc" : "asc" }
      : { key, direction: "asc" });
  };

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h2 className="text-2xl font-semibold">Políticas de archivos</h2>
          <p className="text-sm text-slate-400">
            {canManage ? "Controle extensiones, inspección profunda y límites opcionales. Cada cambio crea una versión auditable." : "Consulte las extensiones configuradas y revise su historial de cambios."}
          </p>
        </div>
        {canManage && <button type="button" disabled={busy} onClick={event => newPolicy(event.currentTarget)} className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold hover:bg-emerald-500 disabled:opacity-50">
          Nueva extensión
        </button>}
      </header>
      {!canManage && <p role="status" className="rounded-xl border border-sky-400/25 bg-[#101e2a] p-4 text-sm text-sky-100">Modo solo consulta. Puede buscar, ordenar y consultar el historial. Las propiedades y las versiones solo pueden modificarlas los administradores.</p>}

      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <Metric label="Configuradas" value={policies.length} description="Total de extensiones con una política, tanto habilitadas como bloqueadas." />
        <Metric label="Habilitadas" value={activeCount} description="Extensiones que se aceptan para carga cuando cumplen su límite por archivo y las demás reglas de validación." />
        <Metric label="Sin verificación profunda" value={bypassCount} danger={bypassCount > 0} description="Extensiones habilitadas cuya política omite la revisión de contraseña, estructura y reparación específica del formato." nextStep="Ordene por Verificación para localizar las políticas que aparecen como Omitida." />
      </div>

      <details className="rounded-xl border border-slate-800 bg-slate-900/60 px-4 py-3 text-sm">
        <summary className="cursor-pointer font-semibold text-amber-200">Cómo leer la tabla</summary>
        <ul className="mt-3 list-disc space-y-1.5 pl-5 text-slate-300">
          <li><strong>Estado.</strong> Habilitada permite cargar la extensión si cumple las demás reglas. Bloqueada rechaza la carga.</li>
          <li><strong>Verificación.</strong> Profunda revisa contraseña y estructura y aplica la reparación disponible para el formato.</li>
          <li><strong>Límite por archivo.</strong> Se aplica a cada archivo que usa la extensión.</li>
          <li><strong>Versión y responsable.</strong> {canManage ? "Abra Editar para revisar los cambios anteriores o guardar una versión con motivo." : "Abra Ver detalle para revisar los cambios anteriores y quién los hizo."}</li>
        </ul>
        <button type="button" onClick={() => searchInput.current?.focus()} className="mt-3 rounded-lg border border-amber-400/30 px-3 py-2 font-semibold text-amber-200 hover:bg-amber-400/10">Ir al buscador</button>
      </details>

      {!editorOpen && error && <div role="alert" className="rounded-xl border border-rose-600/60 bg-rose-900/40 px-4 py-3 text-sm text-rose-100">{error}</div>}

      <div className="space-y-6">
        <section className="file-policy-list min-w-0 rounded-xl border border-slate-800 bg-slate-900/60" aria-labelledby="file-policy-list-heading">
          <div className="flex flex-col gap-3 border-b border-slate-800 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <h3 id="file-policy-list-heading" className="text-sm font-semibold">Políticas efectivas</h3>
              <p role="status" className="mt-1 text-xs text-slate-400">
                Mostrando {pageStart}–{pageEnd} de {visiblePolicies.length} coincidencias · {policies.length} configuradas
              </p>
            </div>
            <label className="relative block w-full sm:max-w-xs">
              <span className="sr-only">Buscar por extensión</span>
              <span className="pointer-events-none absolute inset-y-0 left-3 flex items-center text-slate-500">⌕</span>
              <input
                ref={searchInput}
                type="search"
                value={search}
                onChange={event => setSearch(event.target.value)}
                placeholder="Buscar extensión, por ejemplo .pdf"
                className="w-full rounded-xl border border-white/10 bg-black/25 py-2.5 pl-9 pr-3 text-sm outline-none transition placeholder:text-slate-600 focus:border-amber-400/60 focus:ring-4 focus:ring-amber-400/5"
              />
            </label>
          </div>
          <div className="file-policy-compact-sort gap-2 border-b border-slate-800 px-4 py-3">
            <label className="flex min-w-0 flex-wrap items-center gap-2 text-sm text-slate-300">
              Ordenar por
              <select value={sort.key} onChange={event => setSort({ key: event.target.value as SortKey, direction: "asc" })} className="max-w-full rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-2 text-slate-100">
                {POLICY_COLUMNS.map(column => <option key={column.key} value={column.key}>{column.label}</option>)}
              </select>
            </label>
            <button type="button" onClick={() => toggleSort(sort.key)} aria-label={`Cambiar a orden ${sort.direction === "asc" ? "descendente" : "ascendente"}`} className="rounded-lg border border-slate-700 px-3 py-2 text-sm text-amber-200 hover:border-amber-400/40">
              {sort.direction === "asc" ? "Ascendente ↑" : "Descendente ↓"}
            </button>
          </div>
          <div>
            <table role="table" aria-labelledby="file-policy-list-heading" className="file-policy-table w-full table-fixed text-left text-sm">
              <colgroup>
                {POLICY_COLUMNS.map(column => <col key={column.key} style={{ width: column.width }} />)}
                <col style={{ width: "10%" }} />
              </colgroup>
              <thead role="rowgroup" className="bg-slate-900 text-xs text-slate-300">
                <tr role="row">
                  {POLICY_COLUMNS.map(column => <SortableHeader key={column.key} label={column.label} sortKey={column.key} current={sort} onSort={toggleSort} />)}
                  <th scope="col" role="columnheader" className="text-right">Acciones</th>
                </tr>
              </thead>
              <tbody role="rowgroup">
                {pagedPolicies.map(policy => (
                  <tr role="row" key={policy.id} className="border-t border-slate-800">
                    <PolicyCell label="Extensión" heading className="font-mono font-semibold">{policy.extension}</PolicyCell>
                    <PolicyCell label="Estado">{policy.isAllowed ? "Habilitada" : "Bloqueada"}</PolicyCell>
                    <PolicyCell label="Verificación" className={policy.isAllowed && !policy.requiresVerification ? "font-semibold text-amber-300" : ""}>
                      {policy.requiresVerification ? "Profunda" : "Omitida"}
                    </PolicyCell>
                    <PolicyCell label="Límite por archivo">{policy.maxFileBytes ? `${bytesToMb(policy.maxFileBytes)} MB` : "Sin límite"}</PolicyCell>
                    <PolicyCell label="Versión">v{policy.version}</PolicyCell>
                    <PolicyCell label="Actualizada" className="text-slate-300"><time dateTime={policy.updatedUtc}>{new Date(policy.updatedUtc).toLocaleString()}</time></PolicyCell>
                    <PolicyCell label="Responsable" className="text-slate-300">{policy.updatedBy}</PolicyCell>
                    <PolicyCell label="Acciones" className="text-right">
                      <button type="button" disabled={busy} aria-label={`${canManage ? "Editar" : "Ver detalle de"} ${policy.extension}`} onClick={event => void beginEdit(policy, event.currentTarget)} className="rounded-md border border-emerald-400/20 px-2 py-1.5 font-semibold text-emerald-300 hover:border-emerald-300/40 hover:text-emerald-200 disabled:opacity-50">{canManage ? "Editar" : "Ver detalle"}</button>
                    </PolicyCell>
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
          {visiblePolicies.length > 0 && (
            <div className="flex flex-wrap items-center justify-between gap-4 border-t border-slate-800 px-4 py-3">
              <label className="flex items-center gap-2 text-xs text-slate-400">
                Registros por página
                <select
                  aria-label="Registros por página"
                  value={pageSize}
                  onChange={event => setPageSize(Number(event.target.value))}
                  className="rounded-lg border border-white/10 bg-slate-950 px-2.5 py-2 text-sm font-semibold text-slate-200 outline-none focus:border-amber-400/60"
                >
                  {[10, 25, 50, 100].map(size => <option key={size} value={size}>{size}</option>)}
                </select>
              </label>

              <nav className="flex flex-wrap items-center gap-1" aria-label="Paginación de políticas">
                <button
                  type="button"
                  onClick={() => setPage(current => Math.max(1, current - 1))}
                  disabled={currentPage === 1}
                  className="rounded-lg border border-white/10 px-3 py-2 text-xs font-semibold text-slate-300 transition hover:border-amber-400/40 hover:text-amber-200 disabled:cursor-not-allowed disabled:opacity-35"
                >
                  Anterior
                </button>
                {paginationItems(currentPage, totalPages).map((item, index) => item === "ellipsis" ? (
                  <span key={`ellipsis-${index}`} className="px-1 text-xs text-slate-600" aria-hidden="true">…</span>
                ) : (
                  <button
                    key={item}
                    type="button"
                    aria-label={`Ir a la página ${item}`}
                    aria-current={item === currentPage ? "page" : undefined}
                    onClick={() => setPage(item)}
                    className={`h-9 min-w-9 rounded-lg border px-2 text-xs font-semibold transition ${item === currentPage
                      ? "border-amber-300/30 bg-amber-400/10 text-amber-200"
                      : "border-white/10 text-slate-400 hover:border-amber-400/40 hover:text-amber-200"}`}
                  >
                    {item}
                  </button>
                ))}
                <button
                  type="button"
                  onClick={() => setPage(current => Math.min(totalPages, current + 1))}
                  disabled={currentPage === totalPages}
                  className="rounded-lg border border-white/10 px-3 py-2 text-xs font-semibold text-slate-300 transition hover:border-amber-400/40 hover:text-amber-200 disabled:cursor-not-allowed disabled:opacity-35"
                >
                  Siguiente
                </button>
              </nav>

              <p className="text-xs tabular-nums text-slate-500">Página {currentPage} de {totalPages}</p>
            </div>
          )}
          {busy && policies.length === 0 && <p className="p-4 text-sm text-slate-400">Cargando políticas...</p>}
        </section>

        {editorOpen && (
          <section id="file-policy-editor" aria-labelledby="file-policy-editor-heading" className="scroll-mt-4 rounded-xl border border-slate-800 bg-slate-900/60 p-4 sm:p-5">
            <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <h3 id="file-policy-editor-heading" ref={editorHeading} tabIndex={-1} className="break-words text-lg font-semibold">{selected ? `${canManage ? "Editar" : "Consultar"} ${selected.extension}` : "Nueva política"}</h3>
                <p className="mt-1 text-sm text-slate-400">{canManage ? "Revise las reglas y guarde una nueva versión con su motivo." : "Consulte las reglas vigentes y el historial de la extensión."}</p>
              </div>
              <button type="button" disabled={busy} onClick={closeEditor} className="rounded-lg border border-slate-700 px-3 py-2 text-sm font-semibold text-slate-200 hover:bg-white/5 disabled:opacity-50">Volver a la tabla</button>
            </div>

            {error && <div role="alert" className="mb-4 rounded-lg border border-rose-600/60 bg-rose-900/40 px-4 py-3 text-sm text-rose-100">{error}</div>}
            {message && <div role="status" className="mb-4 rounded-lg border border-emerald-600/60 bg-emerald-900/40 px-4 py-3 text-sm text-emerald-100">{message}</div>}

            <div className="grid gap-5 lg:grid-cols-2">
              <form onSubmit={save} className="min-w-0 space-y-4 rounded-lg border border-slate-800 p-4">
                <div className="flex items-center gap-2">
                  <h4 className="font-semibold">Reglas de transferencia</h4>
                  <HelpTrigger title="Reglas de transferencia" description="La aceptación decide si se permite cargar el formato. La verificación profunda inspecciona el archivo y el límite controla su tamaño." nextStep={canManage ? "Para cambiar una regla, revise su efecto y escriba el motivo antes de guardar." : "Revise el historial para conocer el motivo de un cambio anterior."} />
                </div>
                <fieldset disabled={!canManage || busy} className="space-y-4">
                  <label className="block text-sm">Extensión
                    <input value={extension} disabled={!!selected} onChange={event => setExtension(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 disabled:text-slate-300" required />
                  </label>
                  <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={allowed} onChange={event => setAllowed(event.target.checked)} /> Aceptar esta extensión</label>
                  <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={verify} onChange={event => setVerify(event.target.checked)} /> Ejecutar verificación profunda</label>
                  {allowed && !verify && (
                    <div className="rounded-lg border border-amber-500/60 bg-amber-950/50 p-3 text-xs text-amber-100">
                      Al omitir la verificación profunda, el sistema conserva la validación de tamaño, hash y bloqueo de ejecutables. La revisión de contraseña, estructura y reparación específica queda desactivada.
                    </div>
                  )}
                  <label className="block text-sm">Tamaño máximo opcional (MB, vacío = sin límite)
                    <input type="number" min="0.01" step="0.01" value={maxMb} onChange={event => setMaxMb(event.target.value)} className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2" />
                  </label>
                  {canManage && <label className="block text-sm">Motivo del cambio
                    <textarea value={reason} onChange={event => setReason(event.target.value)} minLength={5} maxLength={512} className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2" required />
                  </label>}
                </fieldset>
                {canManage && <button type="submit" disabled={busy} className="w-full rounded-lg bg-emerald-600 px-4 py-2 font-semibold hover:bg-emerald-500 disabled:opacity-50">Guardar nueva versión</button>}
              </form>

              {selected && (
                <section className="min-w-0 rounded-lg border border-slate-800 p-4" aria-labelledby="file-policy-history-heading">
                  <div className="mb-3 flex items-center gap-2">
                    <h4 id="file-policy-history-heading" className="font-semibold">Historial de versiones</h4>
                    <HelpTrigger title="Historial de versiones" description="Cada versión conserva la configuración, la fecha, el responsable y el motivo del cambio." nextStep={canManage ? "Si restaura una configuración anterior, indique el motivo. Se guardará como una versión nueva." : undefined} />
                  </div>
                  {historyLoading && <p role="status" className="text-sm text-slate-400">Cargando historial…</p>}
                  {!historyLoading && !error && history.length === 0 && <p className="text-sm text-slate-400">Todavía no hay versiones para mostrar.</p>}
                  <div className="max-h-80 space-y-2 overflow-y-auto">
                    {history.map(revision => (
                      <div key={revision.id} className="rounded-lg border border-slate-800 p-3 text-sm [overflow-wrap:anywhere]">
                        <div className="flex flex-wrap justify-between gap-2"><strong>v{revision.version}</strong><time className="text-xs text-slate-400" dateTime={revision.changedUtc}>{new Date(revision.changedUtc).toLocaleString()}</time></div>
                        <p className="mt-1 text-slate-300">{revision.changeReason}</p>
                        <p className="mt-1 text-xs text-slate-400">{revision.changedBy} · {revision.isAllowed ? "habilitada" : "bloqueada"} · {revision.requiresVerification ? "verificada" : "sin verificación"}</p>
                        {canManage && revision.version !== selected.version && <button type="button" disabled={busy} onClick={() => void rollback(revision)} className="mt-2 text-amber-300 hover:text-amber-200 disabled:opacity-50">Restaurar esta configuración</button>}
                      </div>
                    ))}
                  </div>
                </section>
              )}
            </div>
          </section>
        )}
      </div>
    </div>
  );
}

function SortableHeader({
  label,
  sortKey,
  current,
  onSort
}: {
  label: string;
  sortKey: SortKey;
  current: { key: SortKey; direction: SortDirection };
  onSort: (key: SortKey) => void;
}) {
  const active = current.key === sortKey;
  const ariaSort = active ? (current.direction === "asc" ? "ascending" : "descending") : "none";
  return (
    <th scope="col" role="columnheader" aria-sort={ariaSort}>
      <button
        type="button"
        onClick={() => onSort(sortKey)}
        className={`inline-flex items-center gap-1 py-1 text-left font-semibold transition hover:text-amber-200 ${active ? "text-amber-300" : "text-slate-300"}`}
      >
        <span>{label}</span>
        <span aria-hidden="true" className="text-[10px]">{active ? (current.direction === "asc" ? "▲" : "▼") : "↕"}</span>
      </button>
    </th>
  );
}

function PolicyCell({ label, heading = false, className = "", children }: { label: string; heading?: boolean; className?: string; children: ReactNode }) {
  const Cell = heading ? "th" : "td";
  return (
    <Cell scope={heading ? "row" : undefined} role={heading ? "rowheader" : "cell"} className={className}>
      <span className="file-policy-table-label">{label}</span>
      <span className="file-policy-table-value">{children}</span>
    </Cell>
  );
}

function paginationItems(currentPage: number, totalPages: number): Array<number | "ellipsis"> {
  if (totalPages <= 7) return Array.from({ length: totalPages }, (_, index) => index + 1);

  const pages = new Set([1, totalPages, currentPage - 1, currentPage, currentPage + 1]);
  const ordered = [...pages].filter(value => value >= 1 && value <= totalPages).sort((left, right) => left - right);
  const items: Array<number | "ellipsis"> = [];
  ordered.forEach((value, index) => {
    if (index > 0 && value - ordered[index - 1] > 1) items.push("ellipsis");
    items.push(value);
  });
  return items;
}

function Metric({ label, value, description, nextStep, danger = false }: { label: string; value: number; description: string; nextStep?: string; danger?: boolean }) {
  return (
    <div className={`rounded-xl border p-4 ${danger ? "border-amber-600/60 bg-amber-950/30" : "border-slate-800 bg-slate-900/60"}`}>
      <div className="flex items-center justify-between gap-2">
        <p className="text-xs uppercase text-slate-300">{label}</p>
        <HelpTrigger title={label} description={description} nextStep={nextStep} />
      </div>
      <p className="mt-1 text-2xl font-semibold">{value}</p>
    </div>
  );
}
