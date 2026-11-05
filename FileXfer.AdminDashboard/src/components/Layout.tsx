// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/components/Layout.tsx
// Capa: Dashboard administrativo React/Vite
// Propósito: Componente visual compartido del dashboard.
// Interacción: Envuelve páginas y navegación del dashboard.
// Regla de mantenimiento: Mantener layout simple para no ocultar alertas operativas.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import { ReactNode } from "react";
import { Link, NavLink, useLocation } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import { ContextualHelpProvider, HelpSteps, HelpTrigger } from "./ContextualHelp";
import aseLogoWhite from "../assets/ase-logo-white.png";

export function Layout({ children }: { children: ReactNode }) {
  const { session, logout } = useAuth();
  const { pathname } = useLocation();
  const guide = pathname.startsWith("/policies") ? helpGuides.policies : pathname.startsWith("/access") ? helpGuides.access : helpGuides.dashboard;

  return (
    <ContextualHelpProvider key={pathname}>
    <div className="min-h-screen bg-[#0d0f13] text-slate-100 lg:flex">
      <a href="#contenido-principal" className="sr-only z-[150] rounded-lg bg-amber-200 px-4 py-3 text-sm font-semibold text-[#17130c] focus:not-sr-only focus:fixed focus:top-3 focus:left-3">
        Ir al contenido principal
      </a>
      <aside aria-label="Centro de control Transfer ASE" className="relative grid gap-4 border-b border-white/10 bg-[#14171d] px-4 py-4 sm:px-6 lg:sticky lg:top-0 lg:flex lg:h-dvh lg:w-61 lg:shrink-0 lg:flex-col lg:self-start lg:overflow-y-auto lg:border-r lg:border-b-0 lg:px-4 lg:py-6">
        <div aria-hidden="true" className="absolute inset-x-0 top-0 h-px bg-linear-to-r from-amber-200/75 via-amber-400/30 to-transparent" />
        <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-3 lg:block">
          <Link to="/" aria-label="Inicio de Transfer ASE · Auditoría Superior del Estado de Sinaloa" className="inline-flex rounded-lg p-1 focus-visible:outline-2 focus-visible:outline-amber-300">
            <img
              src={aseLogoWhite}
              alt="Auditoría Superior del Estado de Sinaloa"
              className="h-auto w-36 sm:w-44 lg:w-48"
            />
          </Link>
          <div className="lg:mt-6 lg:px-2">
            <p className="text-[10px] font-semibold uppercase tracking-[0.17em] text-amber-200/85">Centro de control</p>
            <p className="mt-0.5 text-base font-semibold text-white lg:text-xl">Transfer ASE</p>
          </div>
        </div>
        <nav aria-label="Navegación principal" className="grid grid-cols-2 gap-2 lg:mt-3 lg:grid-cols-1">
          <NavLink
            to="/"
            end
            className={({ isActive }) => navClass(isActive)}
          >
            <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" className="size-[18px] shrink-0">
              <rect x="3" y="3" width="7" height="7" rx="1.2" /><rect x="14" y="3" width="7" height="7" rx="1.2" />
              <rect x="3" y="14" width="7" height="7" rx="1.2" /><rect x="14" y="14" width="7" height="7" rx="1.2" />
            </svg>
            <span>Dashboard</span>
          </NavLink>
          <NavLink
            to="/policies"
            className={({ isActive }) => navClass(isActive)}
          >
            <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="size-[18px] shrink-0">
              <path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9Z" /><path d="M14 3v6h6M8 13h8m-8 4h6" />
            </svg>
            <span>Políticas de archivos</span>
          </NavLink>
          {session?.roles.some(role => role.toLowerCase() === "admin") && (
            <NavLink to="/access" className={({ isActive }) => navClass(isActive)}>
              <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="size-[18px] shrink-0">
                <path d="M12 3 4 6v5c0 5 8 10 8 10s8-5 8-10V6Z" /><path d="m8.5 11.5 2.5 2.5 4.5-5" />
              </svg>
              <span>Accesos al Dashboard</span>
            </NavLink>
          )}
        </nav>
        <HelpTrigger title={guide.title} ariaLabel="Guía de esta pantalla" clickOnly description={<HelpSteps steps={guide.steps} />}
          className="min-h-11 rounded-lg border border-amber-300/30 bg-amber-400/8 px-3 py-2 text-left text-xs font-semibold text-amber-100 hover:bg-amber-400/15">
          <span aria-hidden="true" className="mr-2">ⓘ</span>Guía de esta pantalla
        </HelpTrigger>
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-xl border border-white/10 bg-[#101217] p-2.5 lg:mt-auto lg:block lg:p-3">
          <div className="min-w-0 px-1">
            <p className="text-[10px] font-medium uppercase tracking-[0.14em] text-slate-400">Sesión e.firma</p>
            <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 lg:gap-y-2">
              <p className="break-all font-mono text-xs font-semibold text-white">
                {session?.rfc ?? "—"}
              </p>
              {session?.roles.map(role => (
                <span key={role} className="rounded-md border border-amber-400/25 bg-amber-400/10 px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wide text-amber-100">
                  {role}
                </span>
              ))}
            </div>
          </div>
          <button
            type="button"
            onClick={() => void logout()}
            className="inline-flex min-h-11 shrink-0 items-center justify-center gap-2 rounded-lg border border-white/10 bg-white/5 px-2.5 text-xs font-medium text-slate-200 transition-colors hover:border-rose-400/50 hover:bg-rose-500/10 hover:text-rose-100 focus-visible:outline-2 focus-visible:outline-amber-300 lg:mt-3 lg:w-full"
          >
            <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="size-4">
              <path d="M10 4H5v16h5M10 12h11m-4-4 4 4-4 4" />
            </svg>
            Cerrar sesión
          </button>
        </div>
      </aside>
      <main id="contenido-principal" tabIndex={-1} className="relative min-w-0 flex-1 px-4 py-5 focus:outline-none sm:px-6 lg:px-7 lg:py-7 xl:px-8">
        <div aria-hidden="true" className="pointer-events-none absolute inset-0 overflow-hidden">
          <div className="absolute -right-40 -top-40 h-96 w-96 rounded-full bg-amber-500/[0.045] blur-3xl" />
        </div>
        <div className="relative mx-auto max-w-[1600px]">{children}</div>
      </main>
    </div>
    </ContextualHelpProvider>
  );
}

const helpGuides = {
  dashboard: { title: "Primer recorrido por el Dashboard", steps: [
    { title: "Empieza en Panorama", text: "Lee la salud operativa y las tarjetas superiores. Las diez presentaciones muestran el mismo corte; elige la que te resulte más clara. Los círculos con i explican cómo leer cada dato." },
    { title: "Entiende qué requiere atención", text: "Abre Diagnóstico para ver qué descontó puntos y cómo corregirlo. En Entrega de alertas, revisa si falló un aviso por correo o si el evento se relaciona con un archivo." },
    { title: "Sigue la historia de un archivo", text: "En Actividad, busca por nombre, RFC, traza o recibo. Consulta el estado antes de volver a cargar un archivo." },
    { title: "Actúa según tu permiso", text: "En Mantenimiento puedes revisar pendientes y vencimientos. Solo administradores pueden retirar, conciliar, limpiar o iniciar un nuevo período de estadísticas. Lee el efecto de cada operación antes de confirmarla." }
  ] },
  policies: { title: "Primer recorrido por Políticas de archivos", steps: [
    { title: "Encuentra la extensión", text: "Escribe, por ejemplo, .pdf en el buscador. Ordena los resultados y cambia el número de filas por página si necesitas comparar varias extensiones." },
    { title: "Lee la regla completa", text: "Revisa si se acepta, si exige verificación profunda, cuál es su límite y quién hizo el último cambio. Abre su detalle para consultar el historial." },
    { title: "Cambia una regla solo si tienes permiso", text: "Si eres administrador, selecciona Editar, escribe un motivo y guarda la nueva versión. Si eres usuario de consulta, puedes ver el detalle y el historial. Los cambios de política afectan las siguientes transferencias." }
  ] },
  access: { title: "Primer recorrido por Accesos al Dashboard", steps: [
    { title: "Identifica a la persona", text: "Busca su RFC en la lista o usa Autorizar RFC. Cada persona entra con su propia e.firma." },
    { title: "Asigna el permiso necesario", text: "Solo consulta puede observar; Administrador también puede cambiar reglas y ejecutar acciones. Elige el menor permiso que permita su trabajo." },
    { title: "Verifica antes de confirmar", text: "Revisa RFC y perfil antes de guardar. Retirar acceso al Dashboard conserva la cuenta del cliente. Los permisos nuevos se aplican al volver a iniciar sesión." }
  ] }
};

function navClass(isActive: boolean): string {
  return [
    "flex min-h-11 min-w-0 items-center gap-2 rounded-lg border px-2.5 py-2 text-xs font-medium leading-5 transition-colors focus-visible:outline-2 focus-visible:outline-amber-300 sm:px-3 sm:text-sm",
    isActive
      ? "border-amber-300/35 bg-amber-400/12 font-semibold text-amber-100"
      : "border-white/8 bg-white/[0.025] text-slate-300 hover:border-white/15 hover:bg-white/6 hover:text-white"
  ].join(" ");
}
