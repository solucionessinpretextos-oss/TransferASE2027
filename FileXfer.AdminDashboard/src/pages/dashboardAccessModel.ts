export interface RfcAccess {
  rfc: string;
  displayName: string | null;
  roles: string[];
  isActive: boolean;
  notes: string | null;
}

export type AccessLevel = "none" | "ops" | "admin";
export const accessLabels: Record<AccessLevel, string> = {
  none: "Sin acceso", ops: "Consulta", admin: "Administrador"
};

export function accessLevel(user: RfcAccess): AccessLevel {
  const roles = user.roles.map(role => role.toLowerCase());
  return roles.includes("admin") ? "admin" : roles.includes("ops") ? "ops" : "none";
}

export function hasDashboardAccess(user: RfcAccess): boolean {
  return user.isActive && accessLevel(user) !== "none";
}

export function validRfc(rfc: string): boolean {
  return /^[A-Z&Ñ]{3,4}[0-9]{6}[A-Z0-9]{3}$/.test(rfc.trim().toUpperCase());
}

/** Retirar el Dashboard conserva la cuenta del cliente y sus permisos básicos. */
export function rolesForAccess(current: RfcAccess | null, level: AccessLevel): string[] {
  if (current && accessLevel(current) === level) return [...current.roles];
  const roles = (current?.roles ?? ["user"]).filter(role => !["admin", "ops"].includes(role.toLowerCase()));
  if (level !== "none") roles.push(level);
  return roles.length ? roles : ["user"];
}

export function sameAccessRecord(left: RfcAccess, right: RfcAccess): boolean {
  return left.rfc === right.rfc && left.isActive === right.isActive &&
    left.displayName === right.displayName && left.notes === right.notes &&
    [...left.roles].sort().join(",") === [...right.roles].sort().join(",");
}
