// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react-dom/test-utils";
import { createRoot, Root } from "react-dom/client";
import { ApplicationErrorBoundary } from "./ApplicationErrorBoundary";

let container: HTMLDivElement;
let root: Root;
beforeEach(() => {
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  vi.spyOn(console, "error").mockImplementation(() => {});
});
afterEach(async () => {
  await act(async () => root.unmount());
  container.remove();
  vi.restoreAllMocks();
});

describe("recuperación de errores de pantalla", () => {
  it("muestra una explicación y permite recuperar la vista cuando se resuelve la causa", async () => {
    let fail = true;
    function Screen() {
      if (fail) throw new Error("Detalle interno que no debe aparecer en pantalla");
      return <h1>Dashboard recuperado</h1>;
    }
    await act(async () => root.render(<ApplicationErrorBoundary><Screen /></ApplicationErrorBoundary>));
    expect(container.querySelector('[role="alert"]')?.textContent).toContain("No se pudo mostrar el Dashboard");
    expect(container.textContent).not.toContain("Detalle interno");
    expect(container.querySelector("a")?.getAttribute("href")).toBe(import.meta.env.BASE_URL);
    fail = false;
    await act(async () => container.querySelector("button")!.click());
    expect(container.textContent).toContain("Dashboard recuperado");
    expect(container.querySelector('[role="alert"]')).toBeNull();
  });

  it("conserva una explicación visible si la causa persiste al reintentar", async () => {
    function BrokenScreen(): never { throw new Error("Falla persistente"); }
    await act(async () => root.render(<ApplicationErrorBoundary><BrokenScreen /></ApplicationErrorBoundary>));
    await act(async () => container.querySelector("button")!.click());
    expect(container.querySelector('[role="alert"]')?.textContent).toContain("No se pudo mostrar el Dashboard");
    expect(container.textContent).not.toContain("Falla persistente");
  });
});
