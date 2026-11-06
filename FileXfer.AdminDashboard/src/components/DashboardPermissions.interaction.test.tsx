// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react-dom/test-utils";
import { createRoot, Root } from "react-dom/client";
import { FilePolicies } from "../pages/FilePolicies";
import { TransferMaintenance } from "./TransferMaintenance";
import { StorageDiagnostics } from "./StorageDiagnostics";
import { AlertDeliveryDiagnostics, AlertDeliveryPage } from "./AlertDeliveryDiagnostics";
import { ServerHealthDto } from "../types/admin";

const auth = vi.hoisted(() => ({ authorizedFetch: vi.fn(), roles: ["ops"] }));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ authorizedFetch: auth.authorizedFetch, session: { roles: auth.roles } }) }));
const policy = { id: "policy", extension: ".pdf", isAllowed: true, requiresVerification: true, maxFileBytes: null, version: 2, updatedUtc: "2026-11-05T00:00:00Z", updatedBy: "Admin", changeReason: "Nueva política" };
const history = [{ ...policy, id: "revision", version: 1, changedUtc: policy.updatedUtc, changedBy: "Admin", changeReason: "Primera versión" }];
const alerts: AlertDeliveryPage = { page: 1, pageSize: 10, totalCount: 1, enabled: true, emailConfigured: true, webhookConfigured: false, maxDeliveryAttempts: 3, configurationIssues: [], statisticsStartedUtc: null, items: [{
  id: "delivery-id", eventId: "event-id", transport: "email", title: "Login SAT fallido", status: "DeadLetter", failedAttempts: 3,
  failureCode: "SmtpException", failureExplanation: "La entrega SMTP falló.", remediation: "Revise el servidor de correo.", occurredUtc: policy.updatedUtc, updatedUtc: policy.updatedUtc,
  rfc: "CARJ721209SD2", relatedItem: "Autenticación rechazada por certificado fuera de vigencia; sin archivo asociado.", countsInCurrentPeriod: true
}] };
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status, headers: { "Content-Type": "application/json" } });
let container: HTMLDivElement; let root: Root;
beforeEach(() => {
  auth.authorizedFetch.mockReset(); auth.roles = ["ops"];
  container = document.createElement("div"); document.body.appendChild(container); root = createRoot(container);
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(async () => { await act(async () => root.unmount()); container.remove(); });
function button(text: string) { return Array.from(container.querySelectorAll("button")).find(item => item.textContent?.includes(text))!; }
async function click(element: HTMLElement) { await act(async () => element.click()); }

describe("Dashboard de consulta y diagnóstico", () => {
  it("permite consultar políticas e historial pero impide todas las modificaciones, incluso submit directo", async () => {
    auth.authorizedFetch.mockImplementation(async (path: string) => json(path.endsWith("/history") ? history : [policy]));
    await act(async () => root.render(<FilePolicies />));
    expect(button("Nueva extensión")).toBeUndefined();
    await click(button("Ver detalle"));
    expect(container.textContent).toContain("Consultar .pdf");
    expect(container.textContent).toContain("Primera versión");
    expect(container.querySelector("fieldset")!.disabled).toBe(true);
    expect(button("Guardar nueva versión")).toBeUndefined();
    expect(button("Restaurar esta configuración")).toBeUndefined();
    await act(async () => container.querySelector("form")!.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true })));
    expect(auth.authorizedFetch.mock.calls.every(call => !call[1]?.method || call[1].method === "GET")).toBe(true);
  });
  it("el administrador conserva la edición y envía la versión esperada", async () => {
    auth.roles = ["admin"];
    auth.authorizedFetch.mockImplementation(async (path: string, init?: RequestInit) => json(init?.method === "PUT" ? policy : path.endsWith("/history") ? history : [policy]));
    await act(async () => root.render(<FilePolicies />));
    await click(button("Editar"));
    expect(container.querySelector("fieldset")!.disabled).toBe(false);
    expect(button("Restaurar esta configuración")).toBeDefined();
    await act(async () => {
      const input = container.querySelector("textarea")!;
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")!.set!.call(input, "Cambio autorizado");
      input.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await act(async () => container.querySelector("form")!.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true })));
    expect(auth.authorizedFetch).toHaveBeenCalledWith("/admin/file-policies/.pdf", expect.objectContaining({ method: "PUT", body: expect.stringContaining('"expectedVersion":2') }));
  });
  it("no permite limpiar, conciliar, reiniciar ni ejecutar pruebas de escritura con rol consulta", async () => {
    auth.authorizedFetch.mockResolvedValue(json({ page: 1, pageSize: 25, totalCount: 0, items: [] }));
    await act(async () => root.render(<><TransferMaintenance onChanged={() => { }} /><StorageDiagnostics server={{ storageOk: true } as ServerHealthDto} onChanged={() => { }} /></>));
    for (const text of ["Limpiar vencidas", "Retirar parciales", "Conciliar seleccionadas", "Reiniciar estadísticas", "Comprobar almacenamiento"]) {
      expect(button(text).disabled).toBe(true); await click(button(text));
    }
    expect(container.querySelector('[role="dialog"]')).toBeNull();
    expect(auth.authorizedFetch.mock.calls.every(call => !call[1]?.method || call[1].method === "GET")).toBe(true);
  });
  it("identifica la alerta de login sin atribuirla a un archivo ni a un webhook", async () => {
    auth.authorizedFetch.mockResolvedValue(json(alerts));
    await act(async () => root.render(<AlertDeliveryDiagnostics />));
    expect(container.textContent).toContain("sin archivo asociado");
    expect(container.textContent).toContain("Correo SMTP");
    expect(container.textContent).toContain("Webhook sin configurar");
    expect(container.textContent).toContain("3 · límite actual 3");
    expect(container.textContent).toContain("ya no se reintenta automáticamente");
    expect(container.textContent).toContain("event-id");
    expect(auth.authorizedFetch).toHaveBeenCalledWith("/admin/monitoring/alerts?status=DeadLetter&page=1&pageSize=10&period=Current", expect.anything());
    expect(button("Reiniciar estadísticas de alertas")).toBeUndefined();
    expect(button("Ver bitácora de reinicios")).toBeUndefined();
  });
  it("muestra nombre, catálogo y traza si la alerta sí está asociada con un archivo", async () => {
    auth.authorizedFetch.mockResolvedValue(json({ ...alerts, items: [{ ...alerts.items[0], title: "Descarga bloqueada", fileId: 51, fileName: "Informe.pdf", relatedItem: "Informe.pdf", traceId: "trace-51" }] }));
    await act(async () => root.render(<AlertDeliveryDiagnostics />));
    expect(container.textContent).toContain("Archivo del catálogo: 51 · Informe.pdf");
    expect(container.textContent).toContain("Traza: trace-51");
    expect(container.textContent).not.toContain("sin archivo asociado");
  });
  it("no consulta alertas mientras la sección está oculta", async () => {
    await act(async () => root.render(<AlertDeliveryDiagnostics active={false} />));
    expect(auth.authorizedFetch).not.toHaveBeenCalled();
  });
  it("informa un error de consulta sin presentar datos antiguos como actuales", async () => {
    auth.authorizedFetch.mockResolvedValue(json({}, 503));
    await act(async () => root.render(<AlertDeliveryDiagnostics />));
    expect(container.querySelector('[role="alert"]')?.textContent).toContain("HTTP 503");
  });

  it("permite al administrador reiniciar con motivo y revisar el historial y la bitácora paginada", async () => {
    auth.roles = ["admin"];
    const onReset = vi.fn();
    const cut = "2026-11-06T12:00:00Z";
    let didReset = false;
    auth.authorizedFetch.mockImplementation(async (path: string) => {
      if (path.endsWith("/statistics/reset")) {
        didReset = true;
        return json({ statisticsStartedUtc: cut, exhaustedBefore: 1, message: "Nuevo período registrado." });
      }
      if (path.includes("/statistics?")) {
        const page = path.includes("page=2") ? 2 : 1;
        return json({ statisticsStartedUtc: cut, page, pageSize: 10, totalCount: 11, items: [{
          id: `reset-${page}`, timestampUtc: cut, actorRfc: "CARJ721209SD2", reason: page === 1 ? "Canal SMTP recuperado" : "Reinicio anterior",
          previousStartedUtc: null, exhaustedBefore: 1
        }] });
      }
      if (!didReset) return json(alerts);
      return path.endsWith("period=All")
        ? json({ ...alerts, statisticsStartedUtc: cut, items: [{ ...alerts.items[0], countsInCurrentPeriod: false }] })
        : json({ ...alerts, statisticsStartedUtc: cut, items: [], totalCount: 0 });
    });
    await act(async () => root.render(<AlertDeliveryDiagnostics onReset={onReset} />));
    const trigger = button("Reiniciar estadísticas de alertas");
    trigger.focus();
    await click(trigger);
    const dialog = container.querySelector('[role="dialog"]')!;
    const input = dialog.querySelector("textarea")!;
    expect(document.activeElement).toBe(input);
    expect(button("Confirmar reinicio").disabled).toBe(true);
    await act(async () => dialog.querySelector("form")!.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true })));
    expect(auth.authorizedFetch.mock.calls.some(call => call[1]?.method === "POST")).toBe(false);
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")!.set!.call(input, "  Canal SMTP recuperado  ");
      input.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await click(button("Confirmar reinicio"));
    expect(auth.authorizedFetch).toHaveBeenCalledWith("/admin/monitoring/alerts/statistics/reset", expect.objectContaining({
      method: "POST", body: JSON.stringify({ reason: "Canal SMTP recuperado" })
    }));
    expect(onReset).toHaveBeenCalledTimes(1);
    expect(container.querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(trigger);
    expect(container.querySelector(`time[datetime="${cut}"]`)).not.toBeNull();
    expect(container.textContent).not.toContain("Login SAT fallido");
    const period = Array.from(container.querySelectorAll("select")).find(item => Array.from(item.options).some(option => option.value === "All" && option.textContent === "Todo el historial"))!;
    await act(async () => { period.value = "All"; period.dispatchEvent(new Event("change", { bubbles: true })); });
    expect(container.textContent).toContain("Login SAT fallido");
    expect(container.textContent).toContain("Histórico · fuera del período actual");
    await click(button("Ver bitácora de reinicios"));
    expect(container.textContent).toContain("Canal SMTP recuperado");
    const auditNext = container.querySelector<HTMLButtonElement>('nav[aria-label="Páginas de la bitácora"] button:last-child')!;
    await click(auditNext);
    expect(auth.authorizedFetch).toHaveBeenCalledWith("/admin/monitoring/alerts/statistics?page=2&pageSize=10", expect.anything());
    expect(container.textContent).toContain("Reinicio anterior");
  });
});
