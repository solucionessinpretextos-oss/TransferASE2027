// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/components/Layout.tsx
// Capa: Dashboard administrativo React/Vite
// Propósito: Componente visual compartido del dashboard.
// Interacción: Envuelve páginas y navegación del dashboard.
// Regla de mantenimiento: Mantener layout simple para no ocultar alertas operativas.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import { ReactNode } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";

export function Layout({ children }: { children: ReactNode }) {
  const { session, logout } = useAuth();

  return (
    <div className="min-h-screen flex bg-slate-950 text-slate-100">
      <aside className="w-64 border-r border-slate-800 p-4 flex flex-col">
        <h1 className="text-xl font-bold mb-4">FileXfer Admin</h1>
        <nav className="flex flex-col gap-2">
          <Link to="/" className="hover:text-emerald-300">Dashboard</Link>
          <Link to="/policies" className="hover:text-emerald-300">Políticas de archivos</Link>
        </nav>
        <div className="mt-auto border-t border-slate-800 pt-4">
          <p className="truncate text-xs font-semibold text-slate-200" title={session?.rfc}>
            {session?.rfc}
          </p>
          <p className="mt-1 text-xs text-slate-500">{session?.roles.join(", ")}</p>
          <button
            type="button"
            onClick={() => void logout()}
            className="mt-3 w-full rounded-lg border border-slate-700 px-3 py-2 text-sm hover:border-rose-500 hover:text-rose-200"
          >
            Cerrar sesión
          </button>
        </div>
      </aside>
      <main className="flex-1 p-6">{children}</main>
    </div>
  );
}
