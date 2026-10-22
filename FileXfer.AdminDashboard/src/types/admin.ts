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
  alertingOk: boolean;
  alertingEnabled: boolean;
  alertQueuePending: number;
  alertQueueInFlight: number;
  alertDeadLetter: number;
  alertQueueCapacity: number;
  alertEnqueuedTotal: number;
  alertDeliveredTotal: number;
  alertDeduplicatedTotal: number;
  alertDroppedTotal: number;
  alertRetryTotal: number;
  alertTerminalFailureTotal: number;
  alertLastDeliveredUtc?: string | null;
  alertLastFailureUtc?: string | null;
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
  pausedResumableTransfers: number;
  resumedTransfersToday: number;
  resumedBytesToday: number;
  verificationBypassedToday: number;
  receiptsIssuedToday: number;
}

export interface OperationalKpisDto {
  operationalHealthScore: number;
  totalTransfersToday: number;
  successRateTodayPercent: number;
  failureRateTodayPercent: number;
  failureRateLastHourPercent: number;
  averageUploadDurationTodaySeconds: number;
  medianUploadDurationTodaySeconds: number;
  p95UploadDurationTodaySeconds: number;
  averageFileSizeTodayBytes: number;
  p95FileSizeTodayBytes: number;
  receiptCoverageTodayPercent: number;
  verificationCoverageTodayPercent: number;
  resumeReuseTodayPercent: number;
  catalogFailuresToday: number;
  completedWithoutReceiptToday: number;
  stalePendingTransfers: number;
  distinctRfcsToday: number;
  lastCompletedUtc?: string | null;
  lastFailedUtc?: string | null;
}

export interface SummaryResponse {
  server: ServerHealthDto;
  transfers: TransferSummaryDto;
  kpis: OperationalKpisDto;
  recentTransfers: RecentTransferDto[];
  timestampUtc: string;
}

export interface RecentTransferDto {
  uploadId: string;
  rfc: string;
  fileName: string;
  status: string;
  bytesReceived: number;
  resumedBytes: number;
  createdUtc: string;
  completedUtc?: string | null;
  policyExtension?: string | null;
  policyVersion?: number | null;
  verificationRequired: boolean;
  verificationPerformed: boolean;
  verificationOutcome?: string | null;
  lastError?: string | null;
  traceId?: string | null;
  receiptId?: string | null;
}

export interface FilePolicyDto {
  id: string;
  extension: string;
  isAllowed: boolean;
  requiresVerification: boolean;
  maxFileBytes?: number | null;
  version: number;
  updatedUtc: string;
  updatedBy: string;
  changeReason: string;
  isVerificationBypass: boolean;
}

export interface FilePolicyRevisionDto {
  id: string;
  extension: string;
  isAllowed: boolean;
  requiresVerification: boolean;
  maxFileBytes?: number | null;
  version: number;
  changedUtc: string;
  changedBy: string;
  changeReason: string;
}
