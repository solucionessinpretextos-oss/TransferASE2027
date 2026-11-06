// @vitest-environment jsdom
import { act } from "react-dom/test-utils";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AlertDeliveryDiagnostics, type AlertDeliveryPage } from "./AlertDeliveryDiagnostics";

const auth = vi.hoisted(() => ({ fetch: vi.fn(), roles: ["admin"] }));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ authorizedFetch: auth.fetch, session: { roles: auth.roles, rfc: "ADMIN-TEST" } }) }));

const endpoint = "/admin/monitoring/alerts";
const originalId = "11111111-1111-4111-8111-111111111111";
const deliveryId = "22222222-2222-4222-8222-222222222222";
const timestamp = "2026-11-06T15:30:00Z";
const alerts: AlertDeliveryPage = {
  page: 1, pageSize: 10, totalCount: 1, enabled: true, emailConfigured: true, webhookConfigured: false,
  maxDeliveryAttempts: 3, configurationIssues: [], statisticsStartedUtc: null, items: [{
    id: originalId, eventId: "event-id", transport: "email", title: "Aviso de prueba agotado", status: "DeadLetter",
    failedAttempts: 3, failureExplanation: "Fallo anterior", remediation: "Verifica el canal", occurredUtc: timestamp,
    updatedUtc: timestamp, relatedItem: "Evento de prueba", countsInCurrentPeriod: true
  }]
};
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status });
let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  auth.fetch.mockReset();
  auth.roles = ["admin"];
  window.sessionStorage.clear();
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
});

afterEach(async () => {
  await act(async () => root.unmount());
  container.remove();
  vi.useRealTimers();
});

function button(text: string) {
  return Array.from(container.querySelectorAll("button")).find(item => item.textContent?.includes(text));
}

async function click(text: string) {
  await act(async () => button(text)!.click());
}

describe("Acciones administrativas de entrega de alertas", () => {
  it("ofrece únicamente los canales habilitados y restringe las acciones al administrador", async () => {
    auth.roles = ["ops"];
    auth.fetch.mockImplementation(async () => json(alerts));
    await act(async () => root.render(<AlertDeliveryDiagnostics />));
    expect(button("Probar correo SMTP")).toBeUndefined();
    expect(button("Reprocesar aviso")).toBeUndefined();

    auth.roles = ["admin"];
    await act(async () => root.render(<AlertDeliveryDiagnostics />));
    expect(button("Probar correo SMTP")).toBeDefined();
    expect(button("Probar webhook")).toBeUndefined();
    expect(button("Reprocesar aviso")).toBeDefined();
  });

  it("reutiliza la solicitud tras un fallo de red y sigue el aviso hasta la aceptación del servidor SMTP", async () => {
    vi.useFakeTimers();
    const submitted: string[] = [];
    let polls = 0;
    auth.fetch.mockImplementation(async (url: string, options?: RequestInit) => {
      if (url === `${endpoint}/test`) {
        submitted.push(String(options?.body));
        if (submitted.length === 1) throw new TypeError("Connection interrupted");
        return json({ deliveryId }, 202);
      }
      if (url === `${endpoint}/${deliveryId}`) {
        polls++;
        return polls === 1 ? json({ id: deliveryId, status: "Pending", failedAttempts: 0 })
          : json({ id: deliveryId, status: "Delivered", failedAttempts: 1, deliveredUtc: timestamp });
      }
      return json(alerts);
    });

    await act(async () => root.render(<AlertDeliveryDiagnostics />));
    await click("Probar correo SMTP");
    expect(container.querySelector('[role="dialog"]')).not.toBeNull();
    expect(document.activeElement).toBe(button("Cancelar"));
    expect(submitted).toHaveLength(0);
    await click("Enviar prueba");
    expect(container.querySelector('[role="alert"]')?.textContent).toContain("evitar duplicarla");
    await click("Enviar prueba");
    expect(submitted).toHaveLength(2);
    expect(submitted[0]).toBe(submitted[1]);
    expect(JSON.parse(submitted[0])).toEqual({ channel: "email", requestId: expect.stringMatching(/^[a-f0-9-]{36}$/i) });
    expect(container.textContent).toContain("En cola o esperando reintento");

    await act(async () => vi.advanceTimersByTimeAsync(3_000));
    expect(container.textContent).toContain("El servidor SMTP aceptó el aviso. La recepción en el buzón se verifica por separado.");
    expect(container.textContent).toContain("1 intento fallido");
    await act(async () => vi.advanceTimersByTimeAsync(10_000));
    expect(polls).toBe(2);
  });

  it("requiere motivo y sigue un reproceso previamente registrado sin volver a enviar el aviso", async () => {
    const submitted: unknown[] = [];
    auth.fetch.mockImplementation(async (url: string, options?: RequestInit) => {
      if (url === `${endpoint}/${originalId}/reprocess`) {
        submitted.push(JSON.parse(String(options?.body)));
        return json({ deliveryId }, 409);
      }
      if (url === `${endpoint}/${deliveryId}`) return json({ id: deliveryId, status: "DeadLetter", failedAttempts: 3 });
      return json(alerts);
    });

    await act(async () => root.render(<AlertDeliveryDiagnostics />));
    await click("Reprocesar aviso");
    const dialog = container.querySelector('[role="dialog"]')!;
    const input = dialog.querySelector("textarea")!;
    expect(document.activeElement).toBe(input);
    expect(button("Confirmar reproceso")?.disabled).toBe(true);
    await act(async () => dialog.querySelector("form")!.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true })));
    expect(submitted).toHaveLength(0);
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")!.set!.call(input, "  Canal recuperado  ");
      input.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await click("Confirmar reproceso");
    expect(submitted).toEqual([{ reason: "Canal recuperado", requestId: expect.any(String) }]);
    expect(container.querySelector("[aria-labelledby='alert-action-tracking-title']")?.textContent).toContain("3 intentos fallidos");
    expect(container.querySelector('[role="dialog"]')).toBeNull();
  });
});
