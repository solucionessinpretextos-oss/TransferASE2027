// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/main.tsx
// Capa: Dashboard administrativo React/Vite
// Propósito: Archivo de configuración/arranque del dashboard React/Vite.
// Interacción: Define cómo se construye, sirve y monta el frontend administrativo.
// Regla de mantenimiento: Cambios en base path o VITE_API_BASE_URL afectan acceso local y publicación.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
