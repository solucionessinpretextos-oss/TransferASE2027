import { describe, expect, it } from "vitest";
import { canReconcilePending, canRemovePending, cleanupResultTone } from "./TransferMaintenance";
import { PendingTransferDto } from "../types/admin";

const pending: PendingTransferDto = {
  uploadId: "example-id",
  fileName: "document.pdf",
  rfc: "EXAMPLE010101A1",
  status: "Pending",
  bytesReceived: 1024,
  expectedLength: 2048,
  lastActivityUtc: "2026-11-03T01:00:00Z",
  resumeExpiresUtc: "2026-11-04T01:00:00Z",
  isActive: false,
  isExpired: false
};

describe("protección de acciones sobre pendientes", () => {
  it.each(["Pending", "Paused"])("permite retirar una carga %s inactiva", status => {
    expect(canRemovePending({ ...pending, status })).toBe(true);
  });
  it("protege una transferencia en transmisión aunque siga marcada Pending", () => {
    expect(canRemovePending({ ...pending, isActive: true })).toBe(false);
  });
  it.each(["Completed", "CatalogFailed", "Cancelled", "Expired", "Failed"])("no ofrece retirar un archivo %s", status => {
    expect(canRemovePending({ ...pending, status })).toBe(false);
  });
  it("distingue conciliación de registro y borrado del parcial", () => {
    const protectedRow = { ...pending, protectionReason: "Ruta inconsistente", canReconcile: true };
    expect(canRemovePending(protectedRow)).toBe(false);
    expect(canReconcilePending(protectedRow)).toBe(true);
    expect(canReconcilePending({ ...protectedRow, isActive: true })).toBe(false);
  });
  it("no representa como éxito una limpieza que omitió todos los archivos", () => {
    expect(cleanupResultTone({ removed: 0, retiredPreservingFile: 0, skipped: 32, failed: 0, bytesReclaimed: 0, message: "Omitidos" })).toBe("warning");
    expect(cleanupResultTone({ removed: 0, retiredPreservingFile: 1, skipped: 0, failed: 0, bytesReclaimed: 0, message: "Conservado" })).toBe("success");
  });
});
