// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/components/Layout.tsx
// Capa: Dashboard administrativo React/Vite
// Propósito: Componente visual compartido del dashboard.
// Interacción: Envuelve páginas y navegación del dashboard.
// Regla de mantenimiento: Mantener layout simple para no ocultar alertas operativas.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import { ReactNode } from "react";
import { NavLink } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import aseLogoWhite from "../assets/ase-logo-white.png";

export function Layout({ children }: { children: ReactNode }) {
  const { session, logout } = useAuth();

  return (
    <div className="min-h-screen bg-[#07090d] text-slate-100 lg:flex">
      <aside className="relative flex border-b border-white/10 bg-[#11141a]/95 p-5 backdrop-blur-xl lg:sticky lg:top-0 lg:h-screen lg:w-72 lg:flex-col lg:border-b-0 lg:border-r">
        <div className="absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-amber-400/70 to-transparent" />
        <div className="flex min-w-0 items-center gap-4 lg:block">
          <img
            src={aseLogoWhite}
            alt="Auditoría Superior del Estado de Sinaloa"
            className="h-auto w-48 opacity-90 lg:w-56"
          />
          <div className="hidden lg:mt-6 lg:block">
            <p className="text-[10px] font-semibold uppercase tracking-[0.28em] text-amber-300/80">
              Centro de control
            </p>
            <h1 className="mt-1 text-lg font-semibold text-white">Transfer ASE</h1>
          </div>
        </div>
        <nav className="ml-auto flex items-center gap-2 lg:ml-0 lg:mt-8 lg:flex-col lg:items-stretch">
          <NavLink
            to="/"
            end
            className={({ isActive }) => navClass(isActive)}
          >
            <span className="h-2 w-2 rounded-full bg-current opacity-80" />
            Dashboard
          </NavLink>
          <NavLink
            to="/policies"
            className={({ isActive }) => navClass(isActive)}
          >
            <span className="h-2 w-2 rounded-sm border border-current opacity-80" />
            Políticas de archivos
          </NavLink>
        </nav>
        <div className="ml-4 hidden border-l border-white/10 pl-4 lg:ml-0 lg:mt-auto lg:block lg:border-l-0 lg:border-t lg:pl-0 lg:pt-5">
          <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500">Sesión e.firma</p>
          <p className="mt-2 truncate text-sm font-semibold text-slate-100" title={session?.rfc}>
            {session?.rfc ?? "—"}
          </p>
          <div className="mt-2 flex flex-wrap gap-1.5">
            {session?.roles.map((role) => (
              <span key={role} className="rounded-full border border-amber-400/20 bg-amber-400/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-amber-200">
                {role}
              </span>
            ))}
          </div>
          <button
            type="button"
            onClick={() => void logout()}
            className="mt-4 w-full rounded-xl border border-white/10 bg-white/[0.03] px-3 py-2.5 text-sm text-slate-300 transition hover:border-rose-400/50 hover:bg-rose-500/10 hover:text-rose-100"
          >
            Cerrar sesión
          </button>
        </div>
      </aside>
      <main className="relative min-w-0 flex-1 overflow-hidden px-4 py-6 sm:px-6 lg:px-8 lg:py-8">
        <div className="pointer-events-none absolute -right-40 -top-40 h-96 w-96 rounded-full bg-amber-500/[0.06] blur-3xl" />
        <div className="pointer-events-none absolute -bottom-52 left-1/3 h-96 w-96 rounded-full bg-emerald-500/[0.04] blur-3xl" />
        <div className="relative mx-auto max-w-[1600px]">{children}</div>
      </main>
    </div>
  );
}

function navClass(isActive: boolean): string {
  return [
    "flex items-center gap-3 rounded-xl px-3 py-2.5 text-sm font-medium transition",
    isActive
      ? "bg-amber-400/10 text-amber-200 ring-1 ring-inset ring-amber-400/20"
      : "text-slate-400 hover:bg-white/[0.04] hover:text-slate-100"
  ].join(" ");
}
