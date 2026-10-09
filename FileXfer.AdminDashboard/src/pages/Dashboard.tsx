// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Archivo: FileXfer.AdminDashboard/src/pages/Dashboard.tsx
// Capa: Dashboard administrativo React/Vite
// Propósito: Pantalla principal del dashboard admin.
// Interacción: Consume /admin/monitoring/summary y renderiza salud/KPIs operativos.
// Regla de mantenimiento: No confundir errores de frontend con caída del backend; probar endpoint REST directo antes de tocar UI.
// Notas específicas:
// - El dashboard es una consola operativa: mostrar KPIs accionables, no métricas decorativas.
// Esta documentación está pensada para orientar mantenimiento futuro sin tener que reconstruir el flujo desde cero.

import { useEffect, useState } from "react";
import { SummaryResponse } from "../types/admin";

const REFRESH_INTERVAL_MS = 2_000;
const ACCESS_TOKEN_KEY = "filexfer.admin.access-token";

class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number
  ) {
    super(message);
  }
}

function getApiBaseUrl(): string {
  const base = import.meta.env.VITE_API_BASE_URL as string | undefined;
  if (!base) {
    return window.location.origin;
  }
  return base.replace(/\/+$/, "");
}

async function fetchSummary(
  accessToken: string,
  signal?: AbortSignal
): Promise<SummaryResponse> {
  const baseUrl = getApiBaseUrl();
  const url = `${baseUrl}/admin/monitoring/summary`;
  const resp = await fetch(url, {
    signal,
    headers: { Authorization: `Bearer ${accessToken}` }
  });

  if (!resp.ok) {
    const text = await resp.text();
    let message = text || `HTTP ${resp.status} al consultar resumen (sin cuerpo)`;
    try {
      const json = JSON.parse(text);
      const msg =
        json.title ??
        json.message ??
        json.error ??
        `HTTP ${resp.status} al consultar resumen`;
      message = typeof msg === "string" ? msg : JSON.stringify(json);
    } catch {
      // Las respuestas sin JSON conservan el texto original o el mensaje HTTP.
    }
    throw new ApiError(message, resp.status);
  }

  return (await resp.json()) as SummaryResponse;
}

export function Dashboard() {
  const [accessToken, setAccessToken] = useState(() =>
    window.sessionStorage.getItem(ACCESS_TOKEN_KEY) ?? ""
  );
  const [summary, setSummary] = useState<SummaryResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState<boolean>(true);

  useEffect(() => {
    if (!accessToken) {
      setSummary(null);
      setLoading(false);
      return;
    }

    let disposed = false;
    let controller: AbortController | null = null;

    const load = async () => {
      controller?.abort();
      controller = new AbortController();

      try {
        if (disposed) return;

        if (!summary) {
          setError(null);
        }

        const data = await fetchSummary(accessToken, controller.signal);
        if (!disposed) {
          setSummary(data);
          setError(null);
        }
      } catch (e) {
        if (disposed) return;

        if (e instanceof DOMException && e.name === "AbortError") {
          return;
        }

        if (e instanceof ApiError && (e.status === 401 || e.status === 403)) {
          window.sessionStorage.removeItem(ACCESS_TOKEN_KEY);
          setAccessToken("");
          setSummary(null);
          setError(
            e.status === 403
              ? "La cuenta no tiene rol admin u ops."
              : "La sesión expiró o el token no es válido."
          );
          return;
        }

        const msg =
          e instanceof Error
            ? e.message
            : "Error desconocido al cargar el resumen";
        setError(msg);

        if (!summary) {
          setSummary(null);
        }
      } finally {
        if (!disposed) {
          setLoading(false);
        }
      }
    };

    load();
    const id = window.setInterval(load, REFRESH_INTERVAL_MS);

    return () => {
      disposed = true;
      controller?.abort();
      window.clearInterval(id);
    };
  }, [accessToken]);

  const connect = (token: string) => {
    const normalized = token.trim();
    if (!normalized) return;
    window.sessionStorage.setItem(ACCESS_TOKEN_KEY, normalized);
    setError(null);
    setLoading(true);
    setAccessToken(normalized);
  };

  const disconnect = () => {
    window.sessionStorage.removeItem(ACCESS_TOKEN_KEY);
    setAccessToken("");
    setSummary(null);
    setError(null);
  };

  if (!accessToken) {
    return <AccessGate onConnect={connect} message={error} />;
  }

  if (loading && !summary && !error) {
    return (
      <div>
        <h2 className="text-2xl font-semibold mb-2">Dashboard general</h2>
        <p className="text-sm text-slate-400">Cargando resumen...</p>
      </div>
    );
  }

  if (error && !summary) {
    return (
      <div>
        <h2 className="text-2xl font-semibold mb-2">Dashboard general</h2>
        <div className="bg-rose-900/40 text-rose-100 border border-rose-600/60 rounded-xl px-4 py-3 text-xs">
          Error al cargar el resumen: {error}
        </div>
      </div>
    );
  }

  if (!summary) {
    return (
      <div>
        <h2 className="text-2xl font-semibold mb-2">Dashboard general</h2>
        <p className="text-sm text-slate-400">
          No hay datos disponibles por el momento.
        </p>
      </div>
    );
  }

  const { server, transfers, kpis } = summary;

  return (
    <div className="space-y-6">
      <header className="flex flex-col gap-1">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="text-2xl font-semibold">Dashboard general</h2>
            <p className="text-sm text-slate-400">
              Estado técnico, desempeño de transferencias y KPIs operativos.
            </p>
          </div>
          <button
            type="button"
            onClick={disconnect}
            className="rounded-lg border border-slate-700 px-3 py-2 text-xs hover:border-rose-500 hover:text-rose-200"
          >
            Cerrar sesión
          </button>
        </div>
      </header>

      {error && (
        <div className="bg-amber-900/40 text-amber-100 border border-amber-600/60 rounded-xl px-4 py-3 text-xs">
          Advertencia: {error}
        </div>
      )}

      <section className="space-y-3">
        <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-300">
          KPIs operativos
        </h3>
        <div className="grid grid-cols-1 md:grid-cols-5 gap-4">
          <KpiCard
            title="Índice operativo"
            value={formatPercent(kpis.operationalHealthScore)}
            helperText="Salud compuesta: servidor, fallas, vencidos y cola"
          />
          <KpiCard
            title="Éxito hoy"
            value={formatPercent(kpis.successRateTodayPercent)}
            helperText={`${kpis.totalTransfersToday} transferencias cerradas`}
          />
          <KpiCard
            title="Falla última hora"
            value={formatPercent(kpis.failureRateLastHourPercent)}
            helperText="Solo completadas/fallidas en ventana reciente"
          />
          <KpiCard
            title="Pendientes vencidos"
            value={kpis.stalePendingTransfers.toString()}
            helperText="Pending con más de 30 minutos"
          />
          <KpiCard
            title="RFCs activos hoy"
            value={kpis.distinctRfcsToday.toString()}
            helperText="RFCs con actividad registrada"
          />
        </div>
        <div className="grid grid-cols-1 md:grid-cols-5 gap-4">
          <KpiCard
            title="Duración promedio"
            value={formatSeconds(kpis.averageUploadDurationTodaySeconds)}
            helperText="Promedio de cargas completadas hoy"
          />
          <KpiCard
            title="Duración P95"
            value={formatSeconds(kpis.p95UploadDurationTodaySeconds)}
            helperText="95% de cargas completadas hoy bajo este tiempo"
          />
          <KpiCard
            title="Tamaño promedio"
            value={formatBytes(kpis.averageFileSizeTodayBytes)}
            helperText="Archivos completados hoy"
          />
          <KpiCard
            title="Última carga OK"
            value={formatDateTime(kpis.lastCompletedUtc)}
          />
          <KpiCard
            title="Última falla"
            value={formatDateTime(kpis.lastFailedUtc)}
          />
        </div>
      </section>

      <section className="space-y-3">
        <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-300">
          Transferencias en curso
        </h3>
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          <KpiCard
            title="Estado del servidor"
            value={server.isHealthy ? "OK" : "Con problemas"}
            helperText={server.message ?? undefined}
          />
          <KpiCard
            title="En transmisión"
            value={transfers.activeTransfers.toString()}
            helperText="Streams gRPC actualmente en curso"
          />
          <KpiCard
            title="En espera"
            value={transfers.waitingTransfers.toString()}
            helperText="Pendientes sin bytes recibidos"
          />
          <KpiCard
            title="Pendientes totales"
            value={transfers.totalPendingTransfers.toString()}
            helperText="En transmisión + en espera"
          />
        </div>
      </section>

      <section className="space-y-3">
        <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-300">
          Recursos del servidor
        </h3>
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          <KpiCard title="Uso CPU" value={server.cpuUsagePercent.toFixed(1) + " %"} />
          <KpiCard title="Uso memoria" value={server.memoryUsagePercent.toFixed(1) + " %"} />
          <KpiCard title="Uso disco" value={server.diskUsagePercent.toFixed(1) + " %"} />
          <KpiCard
            title="Servicios internos"
            value={
              server.databaseOk && server.storageOk && server.messageBusOk
                ? "OK"
                : "Con alertas"
            }
            helperText={buildServicesHelper(server)}
          />
        </div>
      </section>

      <section className="space-y-3">
        <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-300">
          Volumen y resultados
        </h3>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <KpiCard
            title="Bytes última hora"
            value={formatBytes(transfers.bytesTransferredLastHour)}
          />
          <KpiCard
            title="Bytes hoy"
            value={formatBytes(transfers.totalBytesTransferredToday)}
          />
          <KpiCard
            title="Throughput última hora"
            value={transfers.averageThroughputMbps.toFixed(2) + " Mbps"}
          />
        </div>
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          <KpiCard
            title="Completadas última hora"
            value={transfers.completedLastHour.toString()}
          />
          <KpiCard
            title="Fallidas última hora"
            value={transfers.failedLastHour.toString()}
          />
          <KpiCard
            title="Completadas hoy"
            value={transfers.completedToday.toString()}
          />
          <KpiCard
            title="Fallidas hoy"
            value={transfers.failedToday.toString()}
          />
        </div>
      </section>

      <p className="text-xs text-slate-500 mt-4">
        Última actualización: {new Date(summary.timestampUtc).toLocaleString()}
      </p>
    </div>
  );
}

function AccessGate(props: {
  onConnect: (token: string) => void;
  message: string | null;
}) {
  const [token, setToken] = useState("");

  return (
    <div className="mx-auto mt-12 max-w-xl rounded-2xl border border-slate-800 bg-slate-900/60 p-6 shadow-xl">
      <h2 className="text-2xl font-semibold">Acceso administrativo</h2>
      <p className="mt-2 text-sm text-slate-400">
        Ingrese un token de sesión emitido para un usuario con rol admin u ops.
        Se conservará únicamente durante esta pestaña.
      </p>
      {props.message && (
        <div className="mt-4 rounded-lg border border-rose-600/60 bg-rose-900/40 px-4 py-3 text-sm text-rose-100">
          {props.message}
        </div>
      )}
      <form
        className="mt-5 space-y-3"
        onSubmit={(event) => {
          event.preventDefault();
          props.onConnect(token);
        }}
      >
        <label className="block text-sm font-medium" htmlFor="access-token">
          Token de acceso
        </label>
        <input
          type="password"
          id="access-token"
          value={token}
          onChange={(event) => setToken(event.target.value)}
          autoComplete="off"
          spellCheck={false}
          className="w-full rounded-lg border border-slate-700 bg-slate-950 p-3 font-mono text-xs outline-none focus:border-emerald-500"
          aria-describedby="token-help"
        />
        <p id="token-help" className="text-xs text-slate-500">
          El servidor vuelve a validar firma, vigencia y rol en cada consulta.
        </p>
        <button
          type="submit"
          disabled={!token.trim()}
          className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-500 disabled:cursor-not-allowed disabled:opacity-50"
        >
          Conectar
        </button>
      </form>
    </div>
  );
}

function KpiCard(props: { title: string; value: string; helperText?: string }) {
  const { title, value, helperText } = props;
  return (
    <div className="rounded-2xl border border-slate-800 p-4 bg-slate-950/60 shadow-sm shadow-slate-900/40">
      <div className="text-xs uppercase tracking-wide text-slate-400">
        {title}
      </div>
      <div className="mt-1 text-2xl font-semibold">{value}</div>
      {helperText && (
        <div className="mt-1 text-xs text-slate-500">{helperText}</div>
      )}
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  if (bytes < 1024) return `${bytes.toFixed(0)} B`;
  const units = ["KB", "MB", "GB", "TB", "PB"];
  let i = -1;
  do {
    bytes /= 1024;
    i++;
  } while (bytes >= 1024 && i < units.length - 1);
  return `${bytes.toFixed(2)} ${units[i]}`;
}

function formatPercent(value: number): string {
  if (!Number.isFinite(value)) return "0.0 %";
  return `${value.toFixed(1)} %`;
}

function formatSeconds(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return "0 s";
  if (value < 60) return `${value.toFixed(1)} s`;
  const minutes = Math.floor(value / 60);
  const seconds = Math.round(value % 60);
  return `${minutes} min ${seconds} s`;
}

function formatDateTime(value?: string | null): string {
  if (!value) return "Sin dato";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Sin dato";
  return date.toLocaleString();
}

function buildServicesHelper(server: SummaryResponse["server"]): string {
  const parts: string[] = [];
  parts.push(`DB: ${server.databaseOk ? "OK" : "Error"}`);
  parts.push(`Storage: ${server.storageOk ? "OK" : "Error"}`);
  parts.push(`Bus: ${server.messageBusOk ? "OK" : "Error"}`);
  return parts.join(" · ");
}
