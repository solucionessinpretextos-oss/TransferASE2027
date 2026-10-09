// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/vite.config.ts
// Capa: Dashboard administrativo React/Vite
// Propósito: Archivo de configuración/arranque del dashboard React/Vite.
// Interacción: Define cómo se construye, sirve y monta el frontend administrativo.
// Regla de mantenimiento: Cambios en base path o VITE_API_BASE_URL afectan acceso local y publicación.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react-swc";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  base: "/admin/",
  build: {
    outDir: "../FileXfer.Server.Full/wwwroot/admin",
    emptyOutDir: true
  }
});
