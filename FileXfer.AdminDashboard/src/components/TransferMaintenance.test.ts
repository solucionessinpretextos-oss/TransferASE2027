import { describe, expect, it } from "vitest";
import { canRemovePending } from "./TransferMaintenance";
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
});
