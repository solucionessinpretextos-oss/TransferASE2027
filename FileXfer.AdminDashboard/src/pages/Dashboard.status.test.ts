import { describe, expect, it } from "vitest";
import { statusKind } from "./Dashboard";

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
});
