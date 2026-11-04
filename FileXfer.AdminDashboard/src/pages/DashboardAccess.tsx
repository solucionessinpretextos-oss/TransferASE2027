import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { AccessLevel, RfcAccess, accessLabels, accessLevel, hasDashboardAccess, rolesForAccess, sameAccessRecord, validRfc } from "./dashboardAccessModel";

type Editor = {
  original: RfcAccess | null;
  rfc: string;
  name: string;
  notes: string;
  level: AccessLevel;
  reactivate: boolean;
  reviewing: boolean;
};
type Filter = "allowed" | "all" | "admin" | "ops" | "none" | "inactive";
const control = "mt-1.5 min-h-11 w-full rounded-xl border border-white/15 bg-[#10141b] px-3 py-2 text-sm text-white outline-none focus:border-amber-300 focus:ring-1 focus:ring-amber-300 disabled:opacity-50";
const button = "inline-flex min-h-11 items-center justify-center rounded-xl border border-white/15 bg-white/5 px-4 py-2 text-sm font-semibold text-slate-200 hover:bg-white/10 focus-visible:outline-2 focus-visible:outline-amber-300 disabled:cursor-not-allowed disabled:opacity-45";
const primary = `${button} border-amber-300/30 bg-amber-400/15 text-amber-100 hover:bg-amber-400/25`;

export function DashboardAccess() {
  const { session } = useAuth();
  // El servidor también exige admin + e.firma para cualquier escritura.
  if (!session?.roles.some(role => role.toLowerCase() === "admin")) {
    return <section className="rounded-2xl border border-amber-300/25 bg-[#17150e] p-6" role="alert">
      <h1 className="text-2xl font-semibold text-white">Acceso reservado a administradores</h1>
      <p className="mt-3 text-slate-300">Su perfil permite consultar el Dashboard. La administración de RFC requiere una sesión de administrador con e.firma.</p>
    </section>;
  }
  return <AccessManager actorRfc={session.rfc} />;
}

function AccessManager({ actorRfc }: { actorRfc: string }) {
  const { authorizedFetch } = useAuth();
  const [users, setUsers] = useState<RfcAccess[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [activity, setActivity] = useState("");
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [search, setSearch] = useState("");
  const [filter, setFilter] = useState<Filter>("allowed");
  const [pageSize, setPageSize] = useState(10);
  const [page, setPage] = useState(1);
  const [lookup, setLookup] = useState<string | null>(null);
  const [editor, setEditor] = useState<Editor | null>(null);
  const editorRef = useRef<HTMLElement>(null);
  const inFlight = useRef(false);

  const request = useCallback(async (path: string, init?: RequestInit): Promise<Response> => {
    const headers = new Headers(init?.headers);
    if (init?.body) headers.set("Content-Type", "application/json");
    return authorizedFetch(path, { ...init, headers });
  }, [authorizedFetch]);

  const failure = async (response: Response): Promise<never> => {
    let detail = "";
    try { const body = await response.json(); detail = body.message ?? body.detail ?? body.title ?? ""; } catch { /* No mostrar HTML del proxy como error. */ }
    throw new Error(detail || (response.status === 403 ? "Su sesión no tiene permiso para administrar accesos." : response.status === 401 ? "La sesión venció. Inicie sesión nuevamente." : `No se pudo completar la operación (HTTP ${response.status}).`));
  };

  const load = useCallback(async () => {
    const response = await request("/admin/rfcs");
    if (!response.ok) await failure(response);
    const records = await response.json() as RfcAccess[];
    if (!Array.isArray(records)) throw new Error("El servidor no devolvió la lista de RFC esperada.");
    setUsers(records); setLoaded(true);
  }, [request]);

  const run = async (text: string, work: () => Promise<void>) => {
    if (inFlight.current) return;
    inFlight.current = true; setBusy(true); setActivity(text); setError(""); setMessage("");
    try { await work(); }
    catch (e) { setError(e instanceof Error ? e.message : "No fue posible completar la operación."); }
    finally { inFlight.current = false; setBusy(false); setActivity(""); }
  };

  useEffect(() => { void run("Consultando RFC y permisos vigentes…", load); }, [load]);
  useEffect(() => {
    if (editor || lookup !== null) editorRef.current?.focus();
  }, [editor?.rfc, editor?.reviewing, lookup === null]);

  const read = async (rfc: string): Promise<RfcAccess | null> => {
    const response = await request(`/admin/rfcs/${encodeURIComponent(rfc)}`);
    if (response.status === 404) return null;
    if (!response.ok) await failure(response);
    return await response.json() as RfcAccess;
  };
  const open = async (rfc: string, revoke = false) => {
    const normalized = rfc.trim().toUpperCase();
    if (!validRfc(normalized)) { setError("Escriba un RFC válido de 12 o 13 caracteres, incluida la homoclave."); return; }
    await run("Comprobando el RFC y sus permisos actuales…", async () => {
      const current = await read(normalized);
      if (revoke && !current) throw new Error("El RFC ya no existe. Actualice la lista.");
      setEditor({ original: current, rfc: normalized, name: current?.displayName ?? "", notes: current?.notes ?? "",
        level: revoke ? "none" : current && accessLevel(current) !== "none" ? accessLevel(current) : "ops",
        reactivate: false, reviewing: false });
      setLookup(null);
    });
  };
  const update = (change: Partial<Editor>) => setEditor(current => current ? { ...current, ...change } : null);
  const self = editor?.rfc === actorRfc.toUpperCase();
  const inactive = editor?.original?.isActive === false;
  const cannotSave = !editor || busy || (self && editor.level !== "admin") ||
    (inactive && editor.level !== "none" && !editor.reactivate);

  const review = (event: FormEvent) => {
    event.preventDefault();
    if (cannotSave) return;
    setError(""); update({ reviewing: true });
  };
  const save = async () => {
    if (!editor?.reviewing || cannotSave) return;
    const draft = editor;
    await run("Guardando permisos y actualizando las sesiones…", async () => {
      // Evita sobrescribir cambios observables realizados desde otra sesión mientras se editaba.
      const latest = await read(draft.rfc);
      if ((draft.original && (!latest || !sameAccessRecord(draft.original, latest))) || (!draft.original && latest)) {
        update({ reviewing: false });
        throw new Error("Este RFC cambió mientras lo editaba. Cancele y vuelva a abrirlo para revisar sus permisos actuales.");
      }
      const payload: RfcAccess = {
        rfc: draft.rfc, displayName: draft.name.trim() || null, notes: draft.notes.trim() || null,
        roles: rolesForAccess(latest, draft.level),
        isActive: latest ? latest.isActive || (draft.level !== "none" && draft.reactivate) : true
      };
      const response = await request(latest ? `/admin/rfcs/${encodeURIComponent(draft.rfc)}` : "/admin/rfcs", {
        method: latest ? "PUT" : "POST", body: JSON.stringify(payload)
      });
      if (!response.ok) await failure(response);
      const saved = await response.json() as RfcAccess;
      setUsers(current => [...current.filter(item => item.rfc !== saved.rfc), saved]);
      setEditor(null); setFilter("all"); setSearch(saved.rfc); setPage(1);
      setMessage(draft.level === "none"
        ? `Se retiró el acceso al Dashboard de ${saved.rfc}. Se conserva la cuenta del cliente. Los cambios de permisos invalidan sus sesiones anteriores y deberá iniciar sesión de nuevo.`
        : `${saved.rfc} autorizado con perfil ${accessLabels[draft.level].toLowerCase()}. El titular debe iniciar sesión con su propia e.firma para usar los permisos vigentes.`);
    });
  };

  const allowed = users.filter(hasDashboardAccess);
  const visible = useMemo(() => users.filter(user => {
    const term = search.trim().toLocaleUpperCase("es-MX");
    if (term && !`${user.rfc} ${user.displayName ?? ""}`.toLocaleUpperCase("es-MX").includes(term)) return false;
    if (filter === "allowed") return hasDashboardAccess(user);
    if (filter === "none") return !hasDashboardAccess(user);
    if (filter === "inactive") return !user.isActive;
    if (filter === "admin" || filter === "ops") return user.isActive && accessLevel(user) === filter;
    return true;
  }).sort((a, b) => a.rfc.localeCompare(b.rfc, "es-MX")), [users, search, filter]);
  const pages = Math.max(1, Math.ceil(visible.length / pageSize));
  const currentPage = Math.min(page, pages);
  const rows = visible.slice((currentPage - 1) * pageSize, currentPage * pageSize);

  return <div className="space-y-5">
    <header className="flex flex-wrap items-start justify-between gap-4">
      <div className="max-w-3xl">
        <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-amber-300">Identidad y permisos</p>
        <h1 className="mt-2 text-2xl font-semibold tracking-tight text-white sm:text-3xl">Accesos al Dashboard</h1>
        <p className="mt-2 text-sm leading-6 text-slate-400">Decida qué RFC pueden entrar y qué pueden hacer. Cada persona se identifica con su propia e.firma.</p>
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="button" className={button} disabled={busy || !!editor || lookup !== null} onClick={() => void run("Actualizando la lista de accesos…", load)}>Actualizar lista</button>
        <button type="button" className={primary} disabled={busy || !!editor || lookup !== null} onClick={() => { setLookup(""); setError(""); setMessage(""); }}>＋ Autorizar RFC</button>
      </div>
    </header>

    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
      {[
        ["Con acceso", allowed.length, "Cuentas activas con permisos para entrar al Dashboard.", "text-emerald-200"],
        ["Administradores", allowed.filter(user => accessLevel(user) === "admin").length, "Pueden administrar accesos, políticas y mantenimiento.", "text-amber-200"],
        ["Solo consulta", allowed.filter(user => accessLevel(user) === "ops").length, "Pueden observar indicadores y consultar información operativa.", "text-sky-200"],
        ["Sin acceso", users.length - allowed.length, "Cuentas de cliente sin permisos de Dashboard o cuentas inactivas.", "text-slate-300"]
      ].map(([label, count, help, color]) => <div key={label} className="rounded-2xl border border-white/10 bg-[#14181f] p-4" title={String(help)} tabIndex={0}>
        <p className="text-xs font-medium text-slate-400">{label}</p><p className={`mt-2 text-3xl font-semibold ${color}`}>{loaded ? count : "—"}</p>
        <p className="mt-2 text-xs leading-5 text-slate-400">{help}</p>
      </div>)}
    </div>

    {busy && <p role="status" className="rounded-xl border border-sky-400/20 bg-[#111e2a] p-4 text-sm text-sky-100">{activity}</p>}
    {error && <p role="alert" className="rounded-xl border border-rose-400/30 bg-[#2c171e] p-4 text-sm text-rose-100">{error}</p>}
    {message && <p role="status" className="rounded-xl border border-emerald-400/30 bg-[#10281f] p-4 text-sm leading-6 text-emerald-100">{message}</p>}

    {(lookup !== null || editor) && <section ref={editorRef} tabIndex={-1} aria-labelledby="access-editor-title" className="rounded-2xl border border-amber-300/30 bg-[#191b21] p-5 outline-none sm:p-6">
      <div className="flex items-start justify-between gap-3">
        <div><p className="text-[10px] font-semibold uppercase tracking-[0.15em] text-amber-200">{editor?.reviewing ? "2 · Confirmación" : "1 · Configurar acceso"}</p>
          <h2 id="access-editor-title" className="mt-1 text-xl font-semibold text-white">{editor?.reviewing ? "Revise el cambio antes de aplicarlo" : editor ? `Permisos de ${editor.rfc}` : "Autorizar un RFC"}</h2></div>
        <button type="button" className={button} disabled={busy} onClick={() => { setLookup(null); setEditor(null); setError(""); }}>Cancelar</button>
      </div>
      {lookup !== null && <form className="mt-5 max-w-xl" onSubmit={e => { e.preventDefault(); void open(lookup); }}>
        <label className="text-sm text-slate-200">RFC del titular<input className={`${control} font-mono uppercase`} value={lookup} maxLength={13} required autoComplete="off" spellCheck={false} disabled={busy} onChange={e => setLookup(e.target.value.toUpperCase())} placeholder="12 o 13 caracteres con homoclave" /></label>
        <p className="mt-2 text-xs leading-5 text-slate-400">Primero comprobamos si ya existe para conservar su cuenta y evitar duplicados. No necesita entregar aquí sus archivos CER o KEY.</p>
        <button className={`${primary} mt-4`} disabled={busy || !validRfc(lookup)}>Consultar RFC</button>
      </form>}
      {editor && !editor.reviewing && <form className="mt-5 space-y-4" onSubmit={review}>
        <p className="text-sm text-slate-400">{editor.original ? "RFC registrado. Revise sus datos y seleccione el permiso deseado." : "RFC nuevo. Se creará su cuenta con el perfil seleccionado."}</p>
        <div className="grid gap-4 md:grid-cols-2">
          <label className="text-sm text-slate-200">Nombre o referencia<input className={control} maxLength={256} value={editor.name} disabled={busy} onChange={e => update({ name: e.target.value })} placeholder="Nombre para identificar al titular" /></label>
          <label className="text-sm text-slate-200">Permiso del Dashboard<select className={control} value={editor.level} disabled={busy || self} onChange={e => update({ level: e.target.value as AccessLevel, reactivate: false })}>
            <option value="ops">Consulta</option><option value="admin">Administrador</option>
            {editor.original && <option value="none">Sin acceso al Dashboard</option>}
          </select></label>
        </div>
        <div className="rounded-xl border border-white/10 bg-[#10141b] p-4 text-sm leading-6 text-slate-300">
          {editor.level === "admin" ? "Administrador puede autorizar otros RFC, modificar políticas, retirar pendientes y reiniciar estadísticas. Otórguelo solo a quien deba operar estos controles." : editor.level === "ops" ? "Consulta permite ver indicadores, transferencias, diagnósticos disponibles y políticas. No permite administrar accesos ni ejecutar cambios administrativos." : "Sin acceso retira los permisos del Dashboard. Conserva la cuenta del cliente y los archivos enviados. Para seguir usando el cliente deberá iniciar sesión de nuevo."}
        </div>
        {self && <p className="text-sm text-amber-100">Esta es su propia cuenta. Puede editar la referencia y las notas, pero no retirarse el permiso de administrador.</p>}
        {inactive && <div className="rounded-xl border border-amber-300/25 bg-[#252016] p-4 text-sm leading-6 text-amber-100">
          <p>Esta cuenta está inactiva en todo Transfer ASE.</p>
          {editor.level !== "none" && <label className="mt-2 flex items-start gap-3"><input type="checkbox" className="mt-1 size-4 accent-amber-300" checked={editor.reactivate} disabled={busy} onChange={e => update({ reactivate: e.target.checked })} />
            <span>Reactivar la cuenta para permitir el ingreso. También habilita nuevamente su acceso al cliente según sus autorizaciones en BuDi.</span></label>}
        </div>}
        <label className="block text-sm text-slate-200">Notas administrativas <span className="text-slate-500">(opcional)</span><textarea className={control} rows={2} maxLength={1024} value={editor.notes} disabled={busy} onChange={e => update({ notes: e.target.value })} placeholder="Referencia interna. No escriba contraseñas ni llaves." /></label>
        <button className={primary} disabled={cannotSave}>Revisar cambio</button>
      </form>}
      {editor?.reviewing && <div className="mt-5 space-y-4">
        <dl className="grid gap-4 rounded-xl border border-white/10 bg-[#10141b] p-4 sm:grid-cols-3">
          <div><dt className="text-xs text-slate-400">RFC</dt><dd className="mt-1 break-all font-mono font-semibold text-white">{editor.rfc}</dd></div>
          <div><dt className="text-xs text-slate-400">Permiso anterior</dt><dd className="mt-1 text-slate-200">{editor.original ? `${accessLabels[accessLevel(editor.original)]}${inactive ? " · cuenta inactiva" : ""}` : "Sin cuenta registrada"}</dd></div>
          <div><dt className="text-xs text-slate-400">Nuevo permiso</dt><dd className={`mt-1 font-semibold ${editor.level === "none" ? "text-rose-200" : "text-amber-200"}`}>{accessLabels[editor.level]}{editor.reactivate && " · se reactiva la cuenta"}</dd></div>
        </dl>
        <p className="text-sm leading-6 text-slate-300">{editor.level === "none" ? "El titular dejará de poder entrar al Dashboard. Su cuenta de cliente y sus archivos se conservan." : editor.level === "admin" ? "El titular podrá administrar otros accesos y operar los controles administrativos del sistema." : "El titular podrá entrar al Dashboard para consultar la información operativa."} Los cambios de permisos invalidan sus sesiones anteriores, incluido el cliente. La operación queda registrada con su RFC como responsable.</p>
        <div className="flex flex-wrap gap-2">
          <button type="button" className={button} disabled={busy} onClick={() => update({ reviewing: false })}>Volver a editar</button>
          <button type="button" className={editor.level === "none" ? `${button} border-rose-400/40 bg-rose-500/15 text-rose-100` : primary} disabled={cannotSave} onClick={() => void save()}>{busy ? "Guardando…" : editor.level === "none" ? "Confirmar retiro de acceso" : "Confirmar permisos"}</button>
        </div>
      </div>}
    </section>}

    <section className="overflow-hidden rounded-2xl border border-white/10 bg-[#14181f]" aria-labelledby="access-list-title">
      <div className="border-b border-white/10 p-5">
        <h2 id="access-list-title" className="text-lg font-semibold text-white">Directorio de accesos</h2>
        <p className="mt-1 text-xs leading-5 text-slate-400">La lista incluye las cuentas registradas en Transfer ASE. Una cuenta de cliente por sí sola no autoriza la entrada al Dashboard.</p>
        <div className="mt-4 grid gap-3 sm:grid-cols-[minmax(0,1fr)_210px_130px]">
          <label className="text-xs text-slate-300">Buscar RFC o nombre<input className={control} type="search" value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} placeholder="Escriba para filtrar" /></label>
          <label className="text-xs text-slate-300">Mostrar<select className={control} value={filter} onChange={e => { setFilter(e.target.value as Filter); setPage(1); }}>
            <option value="allowed">Con acceso</option><option value="all">Todos los registrados</option><option value="admin">Administradores</option><option value="ops">Solo consulta</option><option value="none">Sin acceso</option><option value="inactive">Cuentas inactivas</option>
          </select></label>
          <label className="text-xs text-slate-300">Por página<select className={control} value={pageSize} onChange={e => { setPageSize(Number(e.target.value)); setPage(1); }}>{[10, 25, 50, 100].map(size => <option key={size}>{size}</option>)}</select></label>
        </div>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[660px] text-left text-sm">
          <thead className="bg-[#1b2029] text-xs text-slate-300"><tr>{["RFC / titular", "Perfil", "Acceso al Dashboard", "Administrar"].map(label => <th key={label} scope="col" className="px-5 py-3 font-medium">{label}</th>)}</tr></thead>
          <tbody className="divide-y divide-white/5">{rows.map(user => {
            const isSelf = user.rfc === actorRfc.toUpperCase();
            return <tr key={user.rfc} className="align-top hover:bg-white/[0.025]">
              <th scope="row" className="px-5 py-4 font-normal"><p className="font-mono font-semibold text-slate-100">{user.rfc} {isSelf && <span className="font-sans text-xs font-normal text-amber-200">· Usted</span>}</p><p className="mt-1 max-w-sm break-words text-xs text-slate-400">{user.displayName || "Sin referencia"}</p></th>
              <td className="px-5 py-4 text-slate-300">{accessLabels[accessLevel(user)]}</td>
              <td className="px-5 py-4"><span className={`inline-flex rounded-full border px-2.5 py-1 text-xs font-medium ${hasDashboardAccess(user) ? "border-emerald-400/25 bg-emerald-400/10 text-emerald-200" : "border-slate-400/20 bg-slate-400/10 text-slate-300"}`}>{hasDashboardAccess(user) ? "Autorizado" : user.isActive ? "No autorizado" : "Cuenta inactiva"}</span></td>
              <td className="px-5 py-3"><div className="flex gap-2">
                <button type="button" className={`${button} px-3 text-xs`} disabled={busy || !!editor || lookup !== null} aria-label={`Modificar acceso de ${user.rfc}`} onClick={() => void open(user.rfc)}>Modificar</button>
                {accessLevel(user) !== "none" && <button type="button" className={`${button} px-3 text-xs text-rose-200`} disabled={busy || isSelf || !!editor || lookup !== null} title={isSelf ? "No puede retirar su propio acceso." : "Retira solo los permisos del Dashboard; conserva la cuenta del cliente."} aria-label={`Retirar acceso de ${user.rfc}`} onClick={() => void open(user.rfc, true)}>Retirar acceso</button>}
              </div></td>
            </tr>;
          })}</tbody>
        </table>
      </div>
      {!rows.length && <p className="p-7 text-center text-sm text-slate-400">{!loaded ? "Esperando la lista del servidor." : "No hay RFC que coincidan con estos filtros. Puede mostrar todos los registrados o autorizar un RFC."}</p>}
      <div className="flex flex-wrap items-center justify-between gap-3 border-t border-white/10 px-5 py-3">
        <p className="text-xs text-slate-400">{visible.length ? (currentPage - 1) * pageSize + 1 : 0}–{Math.min(currentPage * pageSize, visible.length)} de {visible.length} RFC · Página {currentPage} de {pages}</p>
        <div className="flex gap-2"><button type="button" className={button} disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>Anterior</button><button type="button" className={button} disabled={currentPage >= pages} onClick={() => setPage(currentPage + 1)}>Siguiente</button></div>
      </div>
    </section>
    <aside className="rounded-xl border border-white/10 bg-[#10141b] px-5 py-4 text-xs leading-6 text-slate-400">
      <p><strong className="text-slate-200">Protección de acceso.</strong> Solo administradores autenticados con e.firma pueden guardar cambios. No puede retirarse su propio permiso ni dejar al sistema sin un administrador activo. No se solicita código MFA.</p>
      <p><strong className="text-slate-200">Trazabilidad.</strong> Cada cambio se registra en la auditoría del servidor con fecha, RFC responsable y permisos resultantes.</p>
    </aside>
  </div>;
}
