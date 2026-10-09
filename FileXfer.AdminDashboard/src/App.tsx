// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/App.tsx
// Capa: Dashboard administrativo React/Vite
// Propósito: Archivo de configuración/arranque del dashboard React/Vite.
// Interacción: Define cómo se construye, sirve y monta el frontend administrativo.
// Regla de mantenimiento: Cambios en base path o VITE_API_BASE_URL afectan acceso local y publicación.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import { BrowserRouter, Routes, Route } from "react-router-dom";
import { Layout } from "./components/Layout";
import { Dashboard } from "./pages/Dashboard";

export default function App() {
  return (
    <BrowserRouter basename={import.meta.env.BASE_URL}>
      <Layout>
        <Routes>
          <Route path="/" element={<Dashboard />} />
        </Routes>
      </Layout>
    </BrowserRouter>
  );
}
