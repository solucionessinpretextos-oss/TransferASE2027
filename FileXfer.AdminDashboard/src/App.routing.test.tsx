// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react-dom/test-utils";
import { createRoot, Root } from "react-dom/client";
import { ReactNode } from "react";
import App, { normalizeDashboardBasename } from "./App";

const auth = vi.hoisted(() => ({ status: "anonymous" }));
vi.mock("./auth/AuthContext", () => ({
  AuthProvider: ({ children }: { children: ReactNode }) => children,
  useAuth: () => ({ status: auth.status })
}));
vi.mock("./components/Layout", () => ({ Layout: ({ children }: { children: ReactNode }) => <main data-testid="authenticated-layout">{children}</main> }));
vi.mock("./components/EfirmaLogin", () => ({ EfirmaLogin: () => <main>Acceso con e.firma</main> }));
vi.mock("./pages/Dashboard", () => ({ Dashboard: () => <h2>Dashboard general</h2> }));
vi.mock("./pages/FilePolicies", () => ({ FilePolicies: () => <h2>Políticas de archivos</h2> }));

let container: HTMLDivElement;
let root: Root;
beforeEach(() => {
  vi.stubEnv("BASE_URL", "/admin/");
  auth.status = "anonymous";
  container = document.createElement("div"); document.body.appendChild(container);
  root = createRoot(container);
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(async () => {
  await act(async () => root.unmount());
  container.remove(); vi.unstubAllEnvs(); window.history.replaceState(null, "", "/");
});

describe("rutas administrativas después de autenticar", () => {
  it.each(["/admin", "/admin/", "/admin?origen=externo", "/admin/#inicio"])("mantiene contenido visible al pasar de login al Dashboard en %s", async (url) => {
    window.history.replaceState(null, "", url);
    await act(async () => root.render(<App />));
    expect(container.textContent).toContain("Acceso con e.firma");
    auth.status = "authenticated";
    await act(async () => root.render(<App />));
    expect(container.querySelector('[data-testid="authenticated-layout"]')).not.toBeNull();
    expect(container.textContent).toContain("Dashboard general");
    expect(container.textContent).not.toContain("Acceso con e.firma");
  });

  it("permite acceso directo a políticas con sesión autenticada", async () => {
    window.history.replaceState(null, "", "/admin/policies"); auth.status = "authenticated";
    await act(async () => root.render(<App />));
    expect(container.textContent).toContain("Políticas de archivos");
  });

  it("muestra recuperación visible cuando la ruta administrativa no existe", async () => {
    window.history.replaceState(null, "", "/admin/seccion-inexistente"); auth.status = "authenticated";
    await act(async () => root.render(<App />));
    expect(container.textContent).toContain("Esta sección no existe");
    const recovery = container.querySelector<HTMLAnchorElement>("a")!;
    expect(recovery.textContent).toBe("Volver al Dashboard");
    await act(async () => recovery.click());
    expect(container.textContent).toContain("Dashboard general");
    expect(window.location.pathname).toBe("/admin");
  });

  it.each([["/admin/", "/admin"], ["/admin", "/admin"], ["/", "/"]])("normaliza %s a %s para el Router", (base, expected) => {
    expect(normalizeDashboardBasename(base)).toBe(expected);
  });
});
