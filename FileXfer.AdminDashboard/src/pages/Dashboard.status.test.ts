import { describe, expect, it } from "vitest";
import { buildExecutiveObservations, diskUsageDisplay, panelExplanation, statusKind } from "./Dashboard";
import { ServerHealthDto, SummaryResponse } from "../types/admin";

describe("semántica visual de estados del Dashboard", () => {
  it.each([
    ["Completed", "completed"],
    ["CompletedWithWarnings", "completed"],
    ["Uploading", "transmitting"],
    ["Verifying", "transmitting"],
    ["Pending", "pending"],
    ["Paused", "paused"],
    ["CatalogFailed", "catalogFailed"],
    ["Failed", "failed"],
    ["Rejected", "failed"],
    ["Cancelled", "cancelled"]
  ])("clasifica %s como %s", (status, expected) => {
    expect(statusKind(status)).toBe(expected);
  });

  it("no confunde una falla de catálogo con un estado completado", () => {
    expect(statusKind("CatalogFailed")).not.toBe("completed");
  });

  it("no convierte capacidad de disco desconocida en cero por ciento", () => {
    expect(diskUsageDisplay({ diskUsagePercent: 0, diskCapacityAvailable: false } as ServerHealthDto)).toBe("Sin medición");
    expect(diskUsageDisplay({ diskUsagePercent: 25, diskCapacityAvailable: true } as ServerHealthDto)).toContain("25");
  });

  it.each([
    "Pulso operativo",
    "Ruta de los archivos",
    "Constelación de servicios",
    "Recursos del servidor",
    "Verificación y evidencia"
  ])("mantiene explicación operativa para %s", (panel) => {
    expect(panelExplanation(panel)).toBeTruthy();
  });

  it("mantiene la atención por salud y vencimientos aunque se reinicien los contadores históricos", () => {
    const summary = {
      server: { isHealthy: true, alertDeadLetter: 0 },
      transfers: { failedLastHour: 0, pausedResumableTransfers: 0, resumedTransfersToday: 0 },
      kpis: { totalTransfersToday: 0, operationalHealthScore: 80, stalePendingTransfers: 0, expiredPendingTransfers: 2 },
      healthBreakdown: [{ key: "failures", label: "Fallas técnicas recientes", penalty: 20, maxPenalty: 30, detail: "", remediation: "" }]
    } as SummaryResponse;
    const attention = buildExecutiveObservations(summary)[1];
    expect(attention.title).toBe("Atención recomendada");
    expect(attention.body).toContain("2 pendientes con plazo vencido");
    expect(attention.body).toContain("Fallas técnicas recientes");
  });
});
