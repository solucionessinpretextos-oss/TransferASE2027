// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react-dom/test-utils";
import { createRoot, Root } from "react-dom/client";
import { TransferMaintenance } from "./TransferMaintenance";
import { StorageDiagnostics } from "./StorageDiagnostics";
import { PendingTransfersResponse, ServerHealthDto } from "../types/admin";

const mocks = vi.hoisted(() => ({ authorizedFetch: vi.fn() }));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({
  authorizedFetch: mocks.authorizedFetch, session: { roles: ["admin"] }
}) }));

let container: HTMLDivElement;
let root: Root;
const pending: PendingTransfersResponse = {
  page: 1, pageSize: 25, totalCount: 1, items: [{
    uploadId: "protected-id", fileName: "archivo-protegido.pdf", rfc: "TST010101AAA", status: "Pending",
    bytesReceived: 1024, expectedLength: 2048, lastActivityUtc: "2026-11-01T00:00:00Z",
    resumeExpiresUtc: "2026-11-02T00:00:00Z", isActive: false, isExpired: true,
    protectionReason: "La ruta no coincide con el contexto. Concilie conservando el archivo.", canReconcile: true
  }]
};
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json" } });
beforeEach(() => {
  mocks.authorizedFetch.mockReset();
  container = document.createElement("div"); document.body.appendChild(container); root = createRoot(container);
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(async () => { await act(async () => root.unmount()); container.remove(); });
function button(text: string): HTMLButtonElement {
  const match = Array.from(container.querySelectorAll("button")).find(item => item.textContent?.includes(text));
  if (!match) throw new Error(`No se encontró botón ${text}`);
  return match;
}
async function click(element: HTMLElement) { await act(async () => element.click()); }
async function reason(value: string) {
  await act(async () => {
    const area = container.querySelector("textarea")!;
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")!.set!.call(area, value);
    area.dispatchEvent(new Event("input", { bubbles: true }));
  });
}
async function submit() { await click(button("Confirmar operación")); }

describe("acciones reales del panel de mantenimiento", () => {
  it("envía el motivo de limpieza y muestra por qué no se retiró un archivo", async () => {
    const changed = vi.fn();
    mocks.authorizedFetch.mockImplementation(async (path: string, init?: RequestInit) => path.includes("/pending?") ? json(pending) : json({
      removed: 0, retiredPreservingFile: 0, skipped: 1, failed: 0, bytesReclaimed: 0, message: "La transferencia requiere conciliación.",
      items: [{ uploadId: "protected-id", fileName: "archivo-protegido.pdf", outcome: "Skipped", code: "PathMismatch", message: "La ruta no coincide; use Conciliar para conservar el archivo físico.", bytesReclaimed: 0 }]
    }));
    await act(async () => root.render(<TransferMaintenance onChanged={changed} />));
    await click(button("Limpiar vencidas ahora"));
    await reason("test");
    expect(button("Confirmar operación").disabled).toBe(true);
    expect(container.textContent).toContain("Escriba al menos 1 carácter(es) más");
    await reason("Limpieza de pendientes vencidos");
    expect(button("Confirmar operación").disabled).toBe(false);
    await submit();
    expect(mocks.authorizedFetch).toHaveBeenCalledWith("/admin/monitoring/pending/cleanup-expired", expect.objectContaining({
      method: "POST", body: JSON.stringify({ reason: "Limpieza de pendientes vencidos" })
    }));
    expect(container.querySelector('[role="dialog"]')).toBeNull();
    expect(container.textContent).toContain("Ningún pendiente fue retirado");
    expect(container.textContent).toContain("use Conciliar para conservar el archivo físico");
    expect(container.textContent).toContain("PathMismatch");
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it("ofrece conciliación con confirmación explícita y muestra el archivo conservado", async () => {
    mocks.authorizedFetch.mockImplementation(async (path: string) => path.includes("/pending?") ? json(pending) : json({
      removed: 0, retiredPreservingFile: 1, skipped: 0, failed: 0, bytesReclaimed: 0, message: "Registro cerrado; archivo conservado.",
      items: [{ uploadId: "protected-id", fileName: "archivo-protegido.pdf", outcome: "RetiredPreservingFile", code: "Reconciled", message: "Archivo físico conservado.", bytesReclaimed: 0 }]
    }));
    await act(async () => root.render(<TransferMaintenance onChanged={() => { }} />));
    expect(button("Retirar parciales").disabled).toBe(true);
    await click(container.querySelector<HTMLInputElement>('input[aria-label="Seleccionar archivo-protegido.pdf"]')!);
    expect(button("Conciliar seleccionadas").disabled).toBe(false);
    await click(button("Conciliar seleccionadas"));
    expect(container.querySelector('[role="dialog"]')?.textContent).toContain("Sus archivos físicos permanecerán intactos");
    await reason("Conciliación autorizada de registro legado"); await submit();
    expect(mocks.authorizedFetch).toHaveBeenCalledWith("/admin/monitoring/pending/reconcile", expect.objectContaining({
      method: "POST", body: JSON.stringify({ uploadIds: ["protected-id"], reason: "Conciliación autorizada de registro legado" })
    }));
    expect(container.textContent).toContain("Registros cerrados conservando el archivo: 1");
    expect(container.textContent).toContain("Espacio liberado: 0 B");
  });

  it("mantiene visible el error del servidor y el motivo cuando la solicitud falla", async () => {
    mocks.authorizedFetch.mockImplementation(async (path: string) => path.includes("/pending?") ? json(pending) : json({ message: "Su sesión no tiene permiso para limpiar." }, 403));
    await act(async () => root.render(<TransferMaintenance onChanged={() => { }} />));
    await click(button("Limpiar vencidas ahora")); await reason("Motivo de la operación"); await submit();
    expect(container.querySelector('[role="dialog"]')).not.toBeNull();
    expect(container.querySelector('[role="alert"]')?.textContent).toContain("Su sesión no tiene permiso para limpiar.");
    expect(container.querySelector("textarea")!.value).toBe("Motivo de la operación");
  });

  it("abre la limpieza solicitada desde el diagnóstico de salud", async () => {
    mocks.authorizedFetch.mockResolvedValue(json(pending));
    await act(async () => root.render(<TransferMaintenance onChanged={() => { }} requestAction={{ kind: "expire", nonce: 1 }} />));
    expect(container.querySelector('[role="dialog"]')?.textContent).toContain("Confirmar limpieza de vencidas");
  });
});

describe("comprobación inmediata de almacenamiento", () => {
  it("ejecuta el endpoint y enseña la causa comprobada con ruta e identidad", async () => {
    const checked = {
      isHealthy: true, storageOk: true, diskCapacityAvailable: false,
      storageDiagnostic: { code: "OperationalCapacityUnavailable", message: "Lectura, escritura y borrado correctos.", remediation: "No se requiere corregir permisos; la capacidad no pudo medirse.", path: "\\\\storage\\transfer", serviceIdentity: "DOMAIN\\service", checkedUtc: "2026-11-03T21:00:00Z", canRead: true, canWrite: true, canDelete: true, capacityAvailable: false }
    } as ServerHealthDto;
    mocks.authorizedFetch.mockResolvedValue(json(checked));
    const changed = vi.fn();
    await act(async () => root.render(<StorageDiagnostics server={{ ...checked, storageOk: false, storageDiagnostic: null }} onChanged={changed} />));
    await click(button("Comprobar almacenamiento ahora"));
    expect(mocks.authorizedFetch).toHaveBeenCalledWith("/admin/monitoring/health/recheck", { method: "POST" });
    expect(container.textContent).toContain("Lectura, escritura y borrado correctos");
    expect(container.textContent).toContain("DOMAIN\\service");
    expect(container.textContent).toContain("\\\\storage\\transfer");
    expect(container.textContent).toContain("Capacidad: sin medición");
    expect(changed).toHaveBeenCalledTimes(1);
  });
});
