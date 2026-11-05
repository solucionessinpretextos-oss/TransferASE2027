// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react-dom/test-utils";
import { createRoot, type Root } from "react-dom/client";
import { FilePolicies } from "./FilePolicies";
import type { FilePolicyDto } from "../types/admin";

const auth = vi.hoisted(() => ({ authorizedFetch: vi.fn() }));
vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ authorizedFetch: auth.authorizedFetch, session: { roles: ["ops"] } }) }));

const longAccount = "ServicioDeProcesamientoDocumentalExternoSinSeparadores";
const policies: FilePolicyDto[] = Array.from({ length: 13 }, (_, index) => ({
  id: `policy-${index + 1}`,
  extension: `.archivo${index + 1}`,
  isAllowed: true,
  requiresVerification: true,
  isVerificationBypass: false,
  maxFileBytes: 1048576,
  version: index + 1,
  updatedUtc: "2026-11-05T00:00:00Z",
  updatedBy: index === 0 ? longAccount : `Responsable ${index + 1}`,
  changeReason: "Configuración inicial"
}));
let container: HTMLDivElement;
let root: Root;

beforeEach(async () => {
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  auth.authorizedFetch.mockReset();
  auth.authorizedFetch.mockImplementation(async (path: string) => Response.json(path.endsWith("/history") ? [] : policies));
  container = document.createElement("div");
  document.body.append(container);
  root = createRoot(container);
  await act(async () => root.render(<FilePolicies />));
});

afterEach(async () => {
  await act(async () => root.unmount());
  container.remove();
});

function buttonByText(text: string) {
  return Array.from(container.querySelectorAll("button")).find(button => button.textContent?.trim() === text)!;
}

describe("Tabla de políticas de archivos", () => {
  it("abre con diez registros y permite ajustar el tamaño de página", async () => {
    const size = container.querySelector<HTMLSelectElement>('select[aria-label="Registros por página"]')!;
    expect(size.value).toBe("10");
    expect(Array.from(size.options, option => option.value)).toEqual(["10", "25", "50", "100"]);
    expect(container.querySelectorAll("tbody tr")).toHaveLength(10);

    await act(async () => buttonByText("Siguiente").click());
    expect(container.querySelectorAll("tbody tr")).toHaveLength(3);
    expect(container.textContent).toContain("Página 2 de 2");

    await act(async () => {
      size.value = "25";
      size.dispatchEvent(new Event("change", { bubbles: true }));
    });
    expect(container.querySelectorAll("tbody tr")).toHaveLength(13);
    expect(container.textContent).toContain("Página 1 de 1");
  });

  it("expone todas las columnas, el valor completo del responsable y las etiquetas de vista compacta", async () => {
    const headers = Array.from(container.querySelectorAll("thead th"));
    expect(headers).toHaveLength(8);
    const cells = Array.from(container.querySelectorAll("tbody tr:first-child > :is(th, td)"));
    expect(cells.map(cell => cell.querySelector(".file-policy-table-label")?.textContent)).toEqual([
      "Extensión", "Estado", "Verificación", "Límite por archivo", "Versión", "Actualizada", "Responsable", "Acciones"
    ]);
    expect(cells[6].querySelector(".file-policy-table-value")?.textContent).toBe(longAccount);

    const sortColumn = container.querySelector<HTMLSelectElement>(".file-policy-compact-sort select")!;
    await act(async () => {
      sortColumn.value = "version";
      sortColumn.dispatchEvent(new Event("change", { bubbles: true }));
    });
    await act(async () => buttonByText("Ascendente ↑").click());
    expect(container.querySelector("tbody .file-policy-table-value")?.textContent).toBe(".archivo13");
  });

  it("lleva al detalle debajo de la tabla y devuelve el foco a la misma extensión", async () => {
    const list = container.querySelector("table")!;
    const opener = list.querySelector<HTMLButtonElement>('button[aria-label="Ver detalle de .archivo1"]')!;
    expect(container.querySelector("#file-policy-editor")).toBeNull();
    await act(async () => opener.click());
    const editor = container.querySelector("#file-policy-editor")!;
    expect(list.compareDocumentPosition(editor) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(document.activeElement?.textContent).toBe("Consultar .archivo1");
    expect(editor.querySelector("fieldset")?.disabled).toBe(true);
    await act(async () => buttonByText("Volver a la tabla").click());
    expect(document.activeElement).toBe(opener);
  });
});
