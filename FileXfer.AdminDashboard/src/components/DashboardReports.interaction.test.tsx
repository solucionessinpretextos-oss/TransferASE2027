// @vitest-environment jsdom
import { act } from "react-dom/test-utils";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { AlertIncidents, OperationalAudit } from "./DashboardReports";

const auth = vi.hoisted(() => ({ authorizedFetch: vi.fn() }));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => auth }));
const timestamp = "2026-11-05T20:00:00Z";
const json = (data: unknown) => new Response(JSON.stringify(data));
let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  auth.authorizedFetch.mockReset();
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(async () => { await act(async () => root.unmount()); container.remove(); });

it("consulta los grupos activos al abrir Diagnóstico y permite cambiar al historial", async () => {
  auth.authorizedFetch.mockImplementation(async () => json({ page: 1, pageSize: 10, totalCount: 1, items: [{
    transport: "email", failureCode: "smtp_535", description: "Autenticación rechazada", remediation: "Verificar permisos del buzón",
    affectedCount: 3, activeCount: 1, exhaustedCount: 2, firstObservedUtc: timestamp, lastObservedUtc: timestamp,
    lastSuccessfulDeliveryUtc: timestamp
  }] }));
  await act(async () => root.render(<AlertIncidents active={false} refreshKey={0} />));
  expect(auth.authorizedFetch).not.toHaveBeenCalled();
  await act(async () => root.render(<AlertIncidents active refreshKey={0} />));
  expect(auth.authorizedFetch).toHaveBeenCalledWith(expect.stringContaining("period=Current"), expect.anything());
  expect(container.textContent).toContain("3 afectados · 1 activos · 2 agotados");
  expect(container.textContent).toContain("Última entrega aceptada por el canal");
  await act(async () => {
    const select = container.querySelector("select")!;
    select.value = "All";
    select.dispatchEvent(new Event("change", { bubbles: true }));
  });
  expect(auth.authorizedFetch).toHaveBeenLastCalledWith(expect.stringContaining("period=All"), expect.anything());
});

it("envía filtros de bitácora y muestra el estado registrado de la entrega", async () => {
  auth.authorizedFetch.mockImplementation(async () => json({ page: 1, pageSize: 20, totalCount: 1,
    actions: [{ value: "Monitoring.AlertReprocess", label: "Reproceso de alerta" }], items: [{
      id: "audit", timestampUtc: timestamp, actorRfc: "CARJ721209SD2", action: "Monitoring.AlertReprocess", label: "Reproceso de alerta",
      summary: "Se corrigió la configuración", deliveryId: "test-delivery-id", deliveryStatus: "Delivered"
    }]
  }));
  await act(async () => root.render(<OperationalAudit active />));
  expect(container.textContent).toContain("Aceptado por el canal");
  await act(async () => {
    const rfc = container.querySelector('input[maxlength="13"]')!;
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!.call(rfc, "carj721209sd2");
    rfc.dispatchEvent(new Event("input", { bubbles: true }));
    const select = container.querySelector("select")!;
    select.value = "Monitoring.AlertReprocess";
    select.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await act(async () => container.querySelector("form")!.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true })));
  const endpoint = new URL(auth.authorizedFetch.mock.lastCall![0], "https://dashboard.invalid");
  expect(endpoint.pathname).toBe("/admin/monitoring/audit");
  expect(endpoint.searchParams.get("rfc")).toBe("CARJ721209SD2");
  expect(endpoint.searchParams.get("action")).toBe("Monitoring.AlertReprocess");
});
