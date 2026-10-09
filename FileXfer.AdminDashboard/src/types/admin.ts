// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/types/admin.ts
// Capa: Dashboard administrativo React/Vite
// Propósito: Tipos TypeScript del dashboard: documentan la forma esperada del JSON del backend.
// Interacción: Dashboard.tsx los usa para renderizar datos con seguridad de tipos.
// Regla de mantenimiento: Actualizar junto con DTOs del backend cuando cambie el contrato.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

export interface ServerHealthDto {
  isHealthy: boolean;
  message?: string | null;
  cpuUsagePercent: number;
  memoryUsagePercent: number;
  diskUsagePercent: number;
  databaseOk: boolean;
  storageOk: boolean;
  messageBusOk: boolean;
}

export interface TransferSummaryDto {
  activeTransfers: number;
  waitingTransfers: number;
  totalPendingTransfers: number;
  completedLastHour: number;
  failedLastHour: number;
  completedToday: number;
  failedToday: number;
  bytesTransferredLastHour: number;
  totalBytesTransferredToday: number;
  averageThroughputMbps: number;
}

export interface OperationalKpisDto {
  operationalHealthScore: number;
  totalTransfersToday: number;
  successRateTodayPercent: number;
  failureRateLastHourPercent: number;
  averageUploadDurationTodaySeconds: number;
  p95UploadDurationTodaySeconds: number;
  averageFileSizeTodayBytes: number;
  stalePendingTransfers: number;
  distinctRfcsToday: number;
  lastCompletedUtc?: string | null;
  lastFailedUtc?: string | null;
}

export interface SummaryResponse {
  server: ServerHealthDto;
  transfers: TransferSummaryDto;
  kpis: OperationalKpisDto;
  timestampUtc: string;
}
