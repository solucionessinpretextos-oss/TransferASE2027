// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/App.tsx
// Capa: Dashboard administrativo React/Vite
// Propósito: Archivo de configuración/arranque del dashboard React/Vite.
// Interacción: Define cómo se construye, sirve y monta el frontend administrativo.
// Regla de mantenimiento: Cambios en base path o VITE_API_BASE_URL afectan acceso local y publicación.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import { BrowserRouter, Routes, Route } from "react-router-dom";
import { AuthProvider, useAuth } from "./auth/AuthContext";
import { Layout } from "./components/Layout";
import { EfirmaLogin } from "./components/EfirmaLogin";
import { Dashboard } from "./pages/Dashboard";
import { FilePolicies } from "./pages/FilePolicies";

export default function App() {
  return (
    <AuthProvider>
      <AuthenticatedApplication />
    </AuthProvider>
  );
}

function AuthenticatedApplication() {
  const { status } = useAuth();

  if (status === "bootstrapping") {
    return (
      <main className="min-h-screen bg-slate-950 text-slate-100 grid place-items-center">
        <div className="text-center" role="status" aria-live="polite">
          <div className="mx-auto h-10 w-10 animate-spin rounded-full border-4 border-slate-700 border-t-emerald-500" />
          <p className="mt-4 text-sm text-slate-300">Validando sesión administrativa...</p>
        </div>
      </main>
    );
  }

  if (status !== "authenticated") return <EfirmaLogin />;

  return (
    <BrowserRouter basename={import.meta.env.BASE_URL}>
      <Layout>
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/policies" element={<FilePolicies />} />
        </Routes>
      </Layout>
    </BrowserRouter>
  );
}
