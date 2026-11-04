// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react-dom/test-utils";
import { createRoot, Root } from "react-dom/client";
import { DashboardAccess } from "./DashboardAccess";
import { RfcAccess } from "./dashboardAccessModel";

const auth = vi.hoisted(() => ({ authorizedFetch: vi.fn(), roles: ["admin"], rfc: "ADM010101AAA" }));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ authorizedFetch: auth.authorizedFetch, session: { roles: auth.roles, rfc: auth.rfc } }) }));
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
let records: RfcAccess[];
let container: HTMLDivElement;
let root: Root;
let writeStatus: number;
beforeEach(() => {
  auth.roles = ["admin"]; auth.authorizedFetch.mockReset(); writeStatus = 200;
  records = [
    { rfc: auth.rfc, displayName: "Administrador de prueba", roles: ["user", "admin"], isActive: true, notes: "Conservar" },
    { rfc: "OPS010101AAA", displayName: "Consulta de prueba", roles: ["user", "ops"], isActive: true, notes: "Referencia previa" },
    { rfc: "USR010101AAA", displayName: "Cliente de prueba", roles: ["user"], isActive: true, notes: null }
  ];
  auth.authorizedFetch.mockImplementation(async (path: string, init?: RequestInit) => {
    if (!init?.method || init.method === "GET") {
      if (path === "/admin/rfcs") return json(records);
      const record = records.find(item => path.endsWith(item.rfc));
      return record ? json(record) : json({}, 404);
    }
    if (writeStatus !== 200) return json({ message: "El servidor rechazó el cambio." }, writeStatus);
    const saved = JSON.parse(String(init.body)) as RfcAccess;
    records = [...records.filter(item => item.rfc !== saved.rfc), saved];
    return json(saved);
  });
  container = document.createElement("div"); document.body.appendChild(container); root = createRoot(container);
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(async () => { await act(async () => root.unmount()); container.remove(); });
async function render() { await act(async () => root.render(<DashboardAccess />)); }
function button(text: string): HTMLButtonElement {
  const result = Array.from(container.querySelectorAll("button")).find(item => item.textContent?.includes(text));
  if (!result) throw new Error(`No existe botón ${text}`); return result;
}
function labelled(text: string): HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement {
  const label = Array.from(container.querySelectorAll("label")).find(item => item.textContent?.startsWith(text));
  if (!label) throw new Error(`No existe campo ${text}`); return label.querySelector("input,select,textarea")!;
}
async function click(element: HTMLElement) { await act(async () => element.click()); }
async function fill(label: string, value: string) {
  await act(async () => {
    const target = labelled(label);
    const proto = target instanceof HTMLSelectElement ? HTMLSelectElement.prototype : target instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, "value")!.set!.call(target, value);
    target.dispatchEvent(new Event(target instanceof HTMLSelectElement ? "change" : "input", { bubbles: true }));
  });
}
const writes = () => auth.authorizedFetch.mock.calls.filter(([, init]) => init?.method === "POST" || init?.method === "PUT");
async function lookup(rfc: string) {
  await click(button("Autorizar RFC")); await fill("RFC del titular", rfc); await click(button("Consultar RFC"));
}
async function modify(rfc: string, revoke = false) {
  await click(container.querySelector<HTMLButtonElement>(`button[aria-label="${revoke ? "Retirar" : "Modificar"} acceso de ${rfc}"]`)!);
}

describe("administración de accesos con e.firma", () => {
  it.each([["ops"], ["user"], []])("no consulta ni expone controles para un perfil sin admin: %j", async (...roles) => {
    auth.roles = roles as string[]; await render();
    expect(container.textContent).toContain("Acceso reservado a administradores");
    expect(auth.authorizedFetch).not.toHaveBeenCalled();
  });
  it("registra un RFC normalizado de consulta solo después de confirmar", async () => {
    await render(); await lookup("nue010101aaa"); await fill("Nombre o referencia", "Titular nuevo");
    await click(button("Revisar cambio")); expect(writes()).toHaveLength(0);
    await click(button("Confirmar permisos"));
    expect(writes()).toHaveLength(1);
    expect(writes()[0][0]).toBe("/admin/rfcs");
    expect(writes()[0][1].method).toBe("POST");
    expect(JSON.parse(writes()[0][1].body)).toEqual({ rfc: "NUE010101AAA", displayName: "Titular nuevo", roles: ["user", "ops"], isActive: true, notes: null });
    expect(container.textContent).toContain("autorizado con perfil consulta");
  });
  it("reutiliza una cuenta de cliente existente y conserva sus notas", async () => {
    records[2].notes = "Nota de origen";
    await render(); await lookup("USR010101AAA"); await fill("Permiso del Dashboard", "admin");
    await click(button("Revisar cambio")); await click(button("Confirmar permisos"));
    expect(writes()[0][1].method).toBe("PUT");
    expect(JSON.parse(writes()[0][1].body)).toMatchObject({ roles: ["user", "admin"], notes: "Nota de origen", displayName: "Cliente de prueba" });
    expect(records.filter(item => item.rfc === "USR010101AAA")).toHaveLength(1);
  });
  it("retira solo los roles del Dashboard sin desactivar la cuenta ni borrar registros", async () => {
    await render(); await modify("OPS010101AAA", true);
    await click(button("Revisar cambio")); expect(writes()).toHaveLength(0);
    await click(button("Confirmar retiro de acceso"));
    expect(writes()).toHaveLength(1);
    expect(JSON.parse(writes()[0][1].body)).toMatchObject({ roles: ["user"], isActive: true, notes: "Referencia previa" });
    expect(container.textContent).toContain("Se conserva la cuenta del cliente");
    expect(container.textContent).toContain("No autorizado");
  });
  it("protege la cuenta propia y no elimina roles al editar solo la referencia", async () => {
    records[0].roles = ["user", "ops", "admin"];
    await render();
    expect(container.querySelector<HTMLButtonElement>(`button[aria-label="Retirar acceso de ${auth.rfc}"]`)?.disabled).toBe(true);
    await modify(auth.rfc);
    expect(labelled("Permiso del Dashboard").disabled).toBe(true);
    await fill("Nombre o referencia", "Nueva referencia"); await click(button("Revisar cambio")); await click(button("Confirmar permisos"));
    expect(JSON.parse(writes()[0][1].body).roles).toEqual(["user", "ops", "admin"]);
  });
  it("mantiene el error del servidor y permite cancelar sin cambiar permisos", async () => {
    writeStatus = 403; await render(); await modify("OPS010101AAA", true);
    await click(button("Revisar cambio")); await click(button("Confirmar retiro de acceso"));
    expect(container.querySelector('[role="alert"]')?.textContent).toContain("El servidor rechazó el cambio");
    expect(container.textContent).toContain("Revise el cambio antes de aplicarlo");
    await click(button("Cancelar")); expect(records[1].roles).toContain("ops");
  });
  it("avisa si otro administrador cambió el registro durante la edición", async () => {
    await render(); await modify("OPS010101AAA", true);
    records[1].roles = ["user", "admin"];
    await click(button("Revisar cambio")); await click(button("Confirmar retiro de acceso"));
    expect(writes()).toHaveLength(0);
    expect(container.textContent).toContain("cambió mientras lo editaba");
  });
  it("requiere una reactivación explícita y no la aplica cuando se retiran permisos", async () => {
    records[1].isActive = false;
    await render(); await lookup("OPS010101AAA");
    expect(button("Revisar cambio").disabled).toBe(true);
    await click(container.querySelector<HTMLInputElement>('input[type="checkbox"]')!);
    expect(button("Revisar cambio").disabled).toBe(false);
    await fill("Permiso del Dashboard", "none");
    await click(button("Revisar cambio")); await click(button("Confirmar retiro de acceso"));
    expect(JSON.parse(writes()[0][1].body)).toMatchObject({ roles: ["user"], isActive: false });
  });
  it("no consulta RFC inválidos ni concede permisos al cancelar la revisión", async () => {
    await render(); await click(button("Autorizar RFC")); await fill("RFC del titular", "ABC");
    expect(button("Consultar RFC").disabled).toBe(true);
    expect(auth.authorizedFetch).toHaveBeenCalledTimes(1);
    await fill("RFC del titular", "NUE010101AAA"); await click(button("Consultar RFC"));
    await click(button("Revisar cambio")); await click(button("Cancelar")); expect(writes()).toHaveLength(0);
  });
  it("combina búsqueda, filtros y paginación con las cuatro capacidades", async () => {
    records.push(...Array.from({ length: 11 }, (_, index) => ({ rfc: `TES010101A${String(index).padStart(2, "0")}`, displayName: "Titular paginado", roles: ["ops"], isActive: true, notes: null })));
    await render();
    expect(container.querySelectorAll("tbody tr")).toHaveLength(10);
    await click(button("Siguiente")); expect(container.querySelectorAll("tbody tr")).toHaveLength(3);
    expect(Array.from((labelled("Por página") as HTMLSelectElement).options).map(o => o.value)).toEqual(["10", "25", "50", "100"]);
    await fill("Mostrar", "all"); await fill("Buscar RFC o nombre", "Cliente de prueba");
    expect(container.querySelectorAll("tbody tr")).toHaveLength(1);
    expect(container.querySelector("tbody")?.textContent).toContain("USR010101AAA");
    await fill("Mostrar", "allowed"); expect(container.querySelectorAll("tbody tr")).toHaveLength(0);
  });
});
