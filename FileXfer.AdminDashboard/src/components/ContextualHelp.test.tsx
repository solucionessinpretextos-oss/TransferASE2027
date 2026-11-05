// @vitest-environment jsdom
import { act } from "react-dom/test-utils";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { ContextualHelpProvider, HelpSteps, HelpTrigger } from "./ContextualHelp";

let root: Root;
let container: HTMLElement;
const panel = () => document.querySelector<HTMLElement>(".context-help-panel");
const pressEscape = () => document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", cancelable: true, bubbles: true }));
beforeEach(() => {
  (globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement("div"); document.body.appendChild(container); root = createRoot(container);
});
afterEach(async () => { await act(async () => root.unmount()); container.remove(); });

describe("ayuda contextual compartida", () => {
  it("muestra una sola tarjeta en el cuerpo del documento aun si el indicador está dentro de un gráfico o tabla recortada", async () => {
    await act(async () => root.render(<ContextualHelpProvider><div style={{ overflow: "hidden" }}>
      <HelpTrigger title="Transmisiones" description="Sesiones que envían datos." />
      <HelpTrigger title="Fallas" description="Cierres no exitosos." nextStep="Revisa la traza en Actividad." />
    </div></ContextualHelpProvider>));
    const [first, second] = container.querySelectorAll("button");
    await act(async () => first.dispatchEvent(new MouseEvent("pointerover", { bubbles: true })));
    expect(panel()?.parentElement).toBe(document.body);
    expect(panel()?.getAttribute("role")).toBe("tooltip");
    expect(panel()?.textContent).toContain("Sesiones que envían datos");
    await act(async () => second.focus());
    expect(document.querySelectorAll(".context-help-panel")).toHaveLength(1);
    expect(panel()?.textContent).toContain("Revisa la traza");
    expect(second.getAttribute("aria-describedby")).toBe(panel()?.id);
    await act(async () => { pressEscape(); });
    expect(panel()).toBeNull();
  });

  it("fija la ayuda al pulsarla, admite el recorrido con botones y devuelve el foco al cerrarla", async () => {
    await act(async () => root.render(<ContextualHelpProvider>
      <HelpTrigger title="Guía" clickOnly description={<HelpSteps steps={[{ title: "Lee", text: "Primero identifica el dato." }, { title: "Actúa", text: "Después elige el siguiente paso." }]} />}>Abrir guía</HelpTrigger>
    </ContextualHelpProvider>));
    const trigger = container.querySelector<HTMLButtonElement>("button")!;
    await act(async () => trigger.focus());
    expect(panel()).toBeNull();
    await act(async () => trigger.click());
    expect(panel()?.getAttribute("role")).toBe("dialog");
    expect(trigger.getAttribute("aria-expanded")).toBe("true");
    expect(document.activeElement?.getAttribute("aria-label")).toBe("Cerrar ayuda");
    const next = [...panel()!.querySelectorAll("button")].find(item => item.textContent === "Siguiente")!;
    await act(async () => next.click());
    expect(panel()?.textContent).toContain("Paso 2 de 2");
    expect(panel()?.textContent).toContain("Después elige");
    await act(async () => { pressEscape(); });
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });
});
