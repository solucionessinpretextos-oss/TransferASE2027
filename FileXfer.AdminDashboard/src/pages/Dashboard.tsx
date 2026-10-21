// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Consola operativa con tres representaciones del mismo resumen: pulso, barras y lectura ejecutiva.

import { useEffect, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import { RecentTransferDto, SummaryResponse } from "../types/admin";

const REFRESH_INTERVAL_MS = 2_000;
const VIEW_PREFERENCE_KEY = "filexfer.admin.dashboard-view.v1";

type ViewMode =
  | "pulse"
  | "bars"
  | "text"
  | "rings"
  | "heatmap"
  | "flow"
  | "mosaic"
  | "timeline"
  | "radar"
  | "traffic";
type Tone = "gold" | "green" | "red" | "blue" | "slate";
type Severity = "good" | "warning" | "danger";

class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function fetchSummary(
  authorizedFetch: (path: string, init?: RequestInit) => Promise<Response>,
  signal?: AbortSignal
): Promise<SummaryResponse> {
  const response = await authorizedFetch("/admin/monitoring/summary", { signal });
  if (!response.ok) {
    const body = await response.text();
    let message = body || `HTTP ${response.status} al consultar el resumen`;
    try {
      const problem = JSON.parse(body) as { title?: string; message?: string; error?: string };
      message = problem.title ?? problem.message ?? problem.error ?? message;
    } catch {
      // Se conserva la respuesta de texto cuando el servidor no devuelve JSON.
    }
    throw new ApiError(message, response.status);
  }
  return (await response.json()) as SummaryResponse;
}

export function Dashboard() {
  const { authorizedFetch } = useAuth();
  const [summary, setSummary] = useState<SummaryResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [view, setView] = useState<ViewMode>(readPreferredView);

  useEffect(() => {
    let disposed = false;
    let controller: AbortController | null = null;

    const load = async () => {
      controller?.abort();
      controller = new AbortController();
      try {
        const data = await fetchSummary(authorizedFetch, controller.signal);
        if (!disposed) {
          setSummary(data);
          setError(null);
        }
      } catch (cause) {
        if (disposed || (cause instanceof DOMException && cause.name === "AbortError")) return;
        if (cause instanceof ApiError && (cause.status === 401 || cause.status === 403)) {
          setSummary(null);
          setError(cause.status === 403
            ? "La cuenta no tiene rol admin u ops."
            : "La sesión expiró o dejó de ser válida.");
          return;
        }
        setError(cause instanceof Error ? cause.message : "Error desconocido al cargar el resumen");
      } finally {
        if (!disposed) setLoading(false);
      }
    };

    void load();
    const timer = window.setInterval(load, REFRESH_INTERVAL_MS);
    return () => {
      disposed = true;
      controller?.abort();
      window.clearInterval(timer);
    };
  }, [authorizedFetch]);

  const chooseView = (next: ViewMode) => {
    setView(next);
    window.localStorage.setItem(VIEW_PREFERENCE_KEY, next);
  };

  if (loading && !summary && !error) return <DashboardSkeleton />;

  if (error && !summary) {
    return <EmptyState title="No fue posible abrir el centro de control" detail={error} tone="error" />;
  }

  if (!summary) {
    return <EmptyState title="Sin información disponible" detail="El servidor todavía no reporta actividad operativa." />;
  }

  return (
    <div className="space-y-7">
      <header className="flex flex-col gap-5 2xl:flex-row 2xl:items-end 2xl:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-3">
            <p className="text-[10px] font-semibold uppercase tracking-[0.28em] text-amber-300/80">Inteligencia operativa</p>
            <LiveBadge healthy={summary.server.isHealthy} />
          </div>
          <h2 className="mt-2 text-3xl font-semibold tracking-tight text-white sm:text-4xl">Dashboard general</h2>
          <p className="mt-2 max-w-2xl text-sm leading-6 text-slate-400">Una sola fuente de verdad, tres maneras de leerla. Cambie la vista sin alterar los datos.</p>
        </div>
        <ViewSwitcher selected={view} onChange={chooseView} />
      </header>

      {error && (
        <div className="rounded-2xl border border-amber-500/30 bg-amber-500/10 px-4 py-3 text-sm text-amber-100">
          Los últimos datos válidos siguen visibles. La actualización reportó: {error}
        </div>
      )}

      {view === "pulse" && <PulseView summary={summary} />}
      {view === "bars" && <BarsView summary={summary} />}
      {view === "text" && <TextView summary={summary} />}
      {view === "rings" && <RingsView summary={summary} />}
      {view === "heatmap" && <HeatmapView summary={summary} />}
      {view === "flow" && <FlowView summary={summary} />}
      {view === "mosaic" && <MosaicView summary={summary} />}
      {view === "timeline" && <TimelineView summary={summary} />}
      {view === "radar" && <RadarView summary={summary} />}
      {view === "traffic" && <TrafficView summary={summary} />}

      <RecentTransfers transfers={summary.recentTransfers} />

      <footer className="flex flex-col gap-2 border-t border-white/[0.06] pt-4 text-xs text-slate-500 sm:flex-row sm:items-center sm:justify-between">
        <span>Actualización automática cada 2 segundos</span>
        <span>Último corte: {formatDateTime(summary.timestampUtc)}</span>
      </footer>
    </div>
  );
}

function ViewSwitcher({ selected, onChange }: { selected: ViewMode; onChange: (mode: ViewMode) => void }) {
  const options: Array<{ mode: ViewMode; label: string; helper: string }> = [
    { mode: "pulse", label: "Pulso", helper: "Salud general" },
    { mode: "bars", label: "Barras", helper: "Comparación" },
    { mode: "text", label: "Ejecutiva", helper: "Lectura" },
    { mode: "rings", label: "Anillos", helper: "Proporciones" },
    { mode: "heatmap", label: "Térmico", helper: "Intensidad" },
    { mode: "flow", label: "Flujo", helper: "Proceso" },
    { mode: "mosaic", label: "Mosaico", helper: "Prioridades" },
    { mode: "timeline", label: "Cronología", helper: "Eventos" },
    { mode: "radar", label: "Radar", helper: "Equilibrio" },
    { mode: "traffic", label: "Semáforo", helper: "Decisión" }
  ];

  return (
    <div className="grid w-full grid-cols-2 gap-1 rounded-2xl border border-white/10 bg-white/[0.025] p-1.5 sm:grid-cols-5 2xl:grid-cols-10" aria-label="Vista del Dashboard">
      {options.map((option) => {
        const active = selected === option.mode;
        return (
          <button
            key={option.mode}
            type="button"
            aria-pressed={active}
            onClick={() => onChange(option.mode)}
            className={`flex min-w-0 items-center justify-center gap-2 rounded-xl px-2 py-2.5 text-left transition ${active
              ? "bg-amber-400/12 text-amber-100 ring-1 ring-inset ring-amber-300/25"
              : "text-slate-400 hover:bg-white/[0.04] hover:text-slate-200"}`}
          >
            <ViewGlyph mode={option.mode} />
            <span className="min-w-0">
              <span className="block truncate text-xs font-semibold">{option.label}</span>
              <span className="hidden text-[9px] text-current opacity-55 2xl:block">{option.helper}</span>
            </span>
          </button>
        );
      })}
    </div>
  );
}

function PulseView({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  const recentCompleted = summary.recentTransfers.filter(item => statusKind(item.status) === "completed").length;
  const recentFailed = summary.recentTransfers.filter(item => statusKind(item.status) === "failed").length;

  return (
    <div className="space-y-5">
      <div className="grid gap-5 xl:grid-cols-[0.82fr_1.6fr]">
        <Panel className="relative overflow-hidden">
          <div className="pointer-events-none absolute -right-24 -top-24 h-60 w-60 rounded-full bg-amber-500/10 blur-3xl" />
          <PanelHeading eyebrow="Estado compuesto" title="Pulso operativo" />
          <div className="relative mt-7 flex flex-col items-center">
            <HealthRing score={kpis.operationalHealthScore} healthy={server.isHealthy} />
            <p className="mt-5 max-w-xs text-center text-sm leading-6 text-slate-400">{healthExplanation(summary)}</p>
          </div>
          <div className="mt-7 grid grid-cols-3 gap-2 border-t border-white/[0.06] pt-5">
            <MiniMetric label="Éxito hoy" value={formatPercent(kpis.successRateTodayPercent)} />
            <MiniMetric label="RFC activos" value={String(kpis.distinctRfcsToday)} />
            <MiniMetric label="Vencidos" value={String(kpis.stalePendingTransfers)} danger={kpis.stalePendingTransfers > 0} />
          </div>
        </Panel>

        <Panel>
          <div className="flex items-start justify-between gap-4">
            <PanelHeading eyebrow="Actividad en tiempo real" title="Ruta de los archivos" />
            <span className="rounded-full border border-white/10 px-3 py-1 text-[10px] uppercase tracking-wider text-slate-500">En vivo</span>
          </div>
          <div className="mt-7 grid gap-3 md:grid-cols-4">
            <FlowNode index="01" label="En espera" value={transfers.waitingTransfers} detail="Listos para iniciar" tone="slate" />
            <FlowNode index="02" label="Transmitiendo" value={transfers.activeTransfers} detail="Streams en curso" tone="blue" active={transfers.activeTransfers > 0} />
            <FlowNode index="03" label="Reanudables" value={transfers.pausedResumableTransfers} detail="Parciales protegidos" tone="gold" />
            <FlowNode index="04" label="Completados" value={transfers.completedToday} detail="Durante el día" tone="green" />
          </div>
          <div className="mt-5 grid gap-3 sm:grid-cols-3">
            <SignalCard label="Transferido hoy" value={formatBytes(transfers.totalBytesTransferredToday)} detail={`${transfers.averageThroughputMbps.toFixed(2)} Mbps promedio`} />
            <SignalCard label="Recibos emitidos" value={String(transfers.receiptsIssuedToday)} detail="Evidencia firmada y encadenada" />
            <SignalCard label="Recuperación" value={formatBytes(transfers.resumedBytesToday)} detail={`${transfers.resumedTransfersToday} cargas reanudadas`} />
          </div>
        </Panel>
      </div>

      <div className="grid gap-5 xl:grid-cols-[1.15fr_0.85fr]">
        <Panel>
          <PanelHeading eyebrow="Dependencias críticas" title="Constelación de servicios" />
          <div className="mt-5 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <ServiceTile label="Base de datos" ok={server.databaseOk} />
            <ServiceTile label="Almacenamiento" ok={server.storageOk} />
            <ServiceTile label="Bus interno" ok={server.messageBusOk} />
            <ServiceTile label="Alertamiento" ok={!server.alertingEnabled || server.alertingOk} detail={server.alertingEnabled ? "Habilitado" : "Modo local"} />
          </div>
          <div className="mt-5 grid gap-4 sm:grid-cols-3">
            <ResourceDial label="CPU" value={server.cpuUsagePercent} />
            <ResourceDial label="Memoria" value={server.memoryUsagePercent} />
            <ResourceDial label="Disco" value={server.diskUsagePercent} />
          </div>
        </Panel>

        <Panel>
          <PanelHeading eyebrow="Últimos 20 registros" title="Huella de actividad" />
          <div className="mt-6 flex min-h-20 flex-wrap content-start gap-2" aria-label={`${recentCompleted} completados y ${recentFailed} fallidos en la actividad reciente`}>
            {summary.recentTransfers.length === 0 && <p className="text-sm text-slate-500">Aún no hay transferencias recientes.</p>}
            {summary.recentTransfers.map((transfer, index) => (
              <span key={`${transfer.traceId ?? transfer.fileName}-${index}`} title={`${transfer.fileName}: ${transfer.status}`} className={`h-5 w-5 rounded-md border ${statusDotClass(transfer.status)}`} />
            ))}
          </div>
          <div className="mt-6 grid grid-cols-2 gap-3">
            <MiniMetric label="OK recientes" value={String(recentCompleted)} />
            <MiniMetric label="Con falla" value={String(recentFailed)} danger={recentFailed > 0} />
          </div>
          <p className="mt-4 text-xs leading-5 text-slate-500">Cada bloque representa un archivo reciente. Pase el cursor para identificarlo.</p>
        </Panel>
      </div>
    </div>
  );
}

function BarsView({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  const resultMax = Math.max(transfers.completedToday, transfers.failedToday, transfers.completedLastHour, transfers.failedLastHour, 1);
  const queueMax = Math.max(transfers.activeTransfers, transfers.waitingTransfers, transfers.pausedResumableTransfers, 1);
  const custodyMax = Math.max(transfers.receiptsIssuedToday, transfers.resumedTransfersToday, transfers.verificationBypassedToday, 1);

  return (
    <div className="grid gap-5 xl:grid-cols-2">
      <ChartPanel eyebrow="Capacidad" title="Recursos del servidor" footer={buildServicesHelper(server)}>
        <MetricBar label="CPU" value={server.cpuUsagePercent} max={100} display={formatPercent(server.cpuUsagePercent)} tone={usageTone(server.cpuUsagePercent)} />
        <MetricBar label="Memoria" value={server.memoryUsagePercent} max={100} display={formatPercent(server.memoryUsagePercent)} tone={usageTone(server.memoryUsagePercent)} />
        <MetricBar label="Disco" value={server.diskUsagePercent} max={100} display={formatPercent(server.diskUsagePercent)} tone={usageTone(server.diskUsagePercent)} />
      </ChartPanel>

      <ChartPanel eyebrow="Resultados" title="Completadas contra fallidas" footer={`Éxito global del día: ${formatPercent(kpis.successRateTodayPercent)}`}>
        <MetricBar label="Completadas hoy" value={transfers.completedToday} max={resultMax} display={String(transfers.completedToday)} tone="green" />
        <MetricBar label="Fallidas hoy" value={transfers.failedToday} max={resultMax} display={String(transfers.failedToday)} tone="red" />
        <MetricBar label="Completadas · 1 h" value={transfers.completedLastHour} max={resultMax} display={String(transfers.completedLastHour)} tone="blue" />
        <MetricBar label="Fallidas · 1 h" value={transfers.failedLastHour} max={resultMax} display={String(transfers.failedLastHour)} tone="gold" />
      </ChartPanel>

      <ChartPanel eyebrow="Flujo actual" title="Carga de la tubería" footer={`${transfers.totalPendingTransfers} transferencias pendientes en memoria`}>
        <MetricBar label="Transmitiendo" value={transfers.activeTransfers} max={queueMax} display={String(transfers.activeTransfers)} tone="blue" />
        <MetricBar label="En espera" value={transfers.waitingTransfers} max={queueMax} display={String(transfers.waitingTransfers)} tone="gold" />
        <MetricBar label="Reanudables" value={transfers.pausedResumableTransfers} max={queueMax} display={String(transfers.pausedResumableTransfers)} tone="slate" />
      </ChartPanel>

      <ChartPanel eyebrow="Custodia" title="Verificación y evidencia" footer={`${formatBytes(transfers.resumedBytesToday)} recuperados mediante reanudación`}>
        <MetricBar label="Recibos firmados" value={transfers.receiptsIssuedToday} max={custodyMax} display={String(transfers.receiptsIssuedToday)} tone="green" />
        <MetricBar label="Cargas reanudadas" value={transfers.resumedTransfersToday} max={custodyMax} display={String(transfers.resumedTransfersToday)} tone="blue" />
        <MetricBar label="Verificación omitida" value={transfers.verificationBypassedToday} max={custodyMax} display={String(transfers.verificationBypassedToday)} tone={transfers.verificationBypassedToday > 0 ? "gold" : "slate"} />
      </ChartPanel>

      <div className="grid gap-3 sm:grid-cols-3 xl:col-span-2">
        <MetricCard label="Volumen última hora" value={formatBytes(transfers.bytesTransferredLastHour)} helper="Completado y confirmado" />
        <MetricCard label="Volumen del día" value={formatBytes(transfers.totalBytesTransferredToday)} helper={`Promedio ${formatBytes(kpis.averageFileSizeTodayBytes)} por archivo`} />
        <MetricCard label="Throughput promedio" value={`${transfers.averageThroughputMbps.toFixed(2)} Mbps`} helper="Ventana móvil de una hora" />
      </div>
    </div>
  );
}

function TextView({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  const observations = buildExecutiveObservations(summary);

  return (
    <div className="space-y-5">
      <Panel>
        <PanelHeading eyebrow="Lectura automática" title="Resumen ejecutivo" />
        <div className="mt-6 grid gap-4 lg:grid-cols-3">
          {observations.map((item, index) => (
            <article key={item.title} className="rounded-2xl border border-white/[0.07] bg-black/15 p-5">
              <div className="flex items-center justify-between gap-4">
                <span className="text-[10px] font-semibold uppercase tracking-[0.2em] text-amber-300/70">0{index + 1}</span>
                <span className={`h-2.5 w-2.5 rounded-full ${toneDot(item.tone)}`} />
              </div>
              <h3 className="mt-5 text-lg font-semibold text-white">{item.title}</h3>
              <p className="mt-2 text-sm leading-6 text-slate-400">{item.body}</p>
            </article>
          ))}
        </div>
      </Panel>

      <section>
        <SectionTitle title="Indicadores principales" subtitle="Cifras precisas para diagnóstico y seguimiento." />
        <div className="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-5">
          <MetricCard label="Índice operativo" value={formatPercent(kpis.operationalHealthScore)} helper={server.isHealthy ? "Servidor saludable" : "Servidor con alertas"} accent />
          <MetricCard label="Éxito hoy" value={formatPercent(kpis.successRateTodayPercent)} helper={`${kpis.totalTransfersToday} transferencias cerradas`} />
          <MetricCard label="Falla última hora" value={formatPercent(kpis.failureRateLastHourPercent)} helper="Completadas contra fallidas" danger={kpis.failureRateLastHourPercent > 5} />
          <MetricCard label="RFC activos" value={String(kpis.distinctRfcsToday)} helper="Con actividad durante el día" />
          <MetricCard label="Pendientes vencidos" value={String(kpis.stalePendingTransfers)} helper="Más de 30 minutos" danger={kpis.stalePendingTransfers > 0} />
          <MetricCard label="Duración promedio" value={formatSeconds(kpis.averageUploadDurationTodaySeconds)} helper="Cargas completadas hoy" />
          <MetricCard label="Duración P95" value={formatSeconds(kpis.p95UploadDurationTodaySeconds)} helper="95 % concluye antes de este tiempo" />
          <MetricCard label="Tamaño promedio" value={formatBytes(kpis.averageFileSizeTodayBytes)} helper="Archivos completados" />
          <MetricCard label="Última carga OK" value={formatDateTime(kpis.lastCompletedUtc)} />
          <MetricCard label="Última falla" value={formatDateTime(kpis.lastFailedUtc)} danger={Boolean(kpis.lastFailedUtc)} />
        </div>
      </section>

      <section>
        <SectionTitle title="Operación y custodia" subtitle="Transmisión, reanudación, verificación y recibos." />
        <div className="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <MetricCard label="Transmitiendo" value={String(transfers.activeTransfers)} helper="Streams gRPC en curso" />
          <MetricCard label="En espera" value={String(transfers.waitingTransfers)} helper="Pendientes sin bytes" />
          <MetricCard label="Reanudables" value={String(transfers.pausedResumableTransfers)} helper="Parciales vigentes" />
          <MetricCard label="Reanudadas hoy" value={String(transfers.resumedTransfersToday)} helper={`${formatBytes(transfers.resumedBytesToday)} recuperados`} />
          <MetricCard label="Recibos hoy" value={String(transfers.receiptsIssuedToday)} helper="Firmados y encadenados" />
          <MetricCard label="Verificación omitida" value={String(transfers.verificationBypassedToday)} helper="Sólo por política explícita" danger={transfers.verificationBypassedToday > 0} />
          <MetricCard label="Volumen hoy" value={formatBytes(transfers.totalBytesTransferredToday)} />
          <MetricCard label="Throughput" value={`${transfers.averageThroughputMbps.toFixed(2)} Mbps`} />
        </div>
      </section>
    </div>
  );
}

function RingsView({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  const capacity = clamp(100 - Math.max(server.cpuUsagePercent, server.memoryUsagePercent, server.diskUsagePercent), 0, 100);
  const receiptCoverage = transfers.completedToday === 0 ? 100 : clamp(transfers.receiptsIssuedToday / transfers.completedToday * 100, 0, 100);
  const verifiedRecent = summary.recentTransfers.filter(item => !item.verificationRequired || item.verificationPerformed).length;
  const verificationCoverage = summary.recentTransfers.length === 0 ? 100 : verifiedRecent / summary.recentTransfers.length * 100;
  const stability = clamp(100 - kpis.failureRateLastHourPercent, 0, 100);

  return (
    <Panel>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-end sm:justify-between">
        <PanelHeading eyebrow="Lectura proporcional" title="Anillos operativos" />
        <p className="max-w-lg text-xs leading-5 text-slate-500">El cierre de cada círculo representa 100 %. Permite detectar desequilibrios sin comparar escalas incompatibles.</p>
      </div>
      <div className="mt-8 grid grid-cols-2 gap-6 md:grid-cols-3 xl:grid-cols-6">
        <RingMetric label="Salud" value={kpis.operationalHealthScore} display={formatPercent(kpis.operationalHealthScore)} />
        <RingMetric label="Éxito" value={kpis.totalTransfersToday === 0 ? 0 : kpis.successRateTodayPercent} display={kpis.totalTransfersToday === 0 ? "Sin actividad" : formatPercent(kpis.successRateTodayPercent)} />
        <RingMetric label="Estabilidad" value={stability} display={formatPercent(stability)} />
        <RingMetric label="Holgura" value={capacity} display={formatPercent(capacity)} inverse />
        <RingMetric label="Recibos" value={receiptCoverage} display={formatPercent(receiptCoverage)} />
        <RingMetric label="Verificación" value={verificationCoverage} display={formatPercent(verificationCoverage)} />
      </div>
      <div className="mt-8 grid gap-3 border-t border-white/[0.06] pt-5 sm:grid-cols-3">
        <SignalCard label="Presión principal" value={largestResource(server)} detail="Recurso con mayor utilización" />
        <SignalCard label="Resultado del día" value={`${transfers.completedToday} / ${transfers.failedToday}`} detail="Completadas / fallidas" />
        <SignalCard label="Custodia activa" value={String(transfers.pausedResumableTransfers)} detail="Cargas parciales recuperables" />
      </div>
    </Panel>
  );
}

function HeatmapView({ summary }: { summary: SummaryResponse }) {
  const signals = buildHeatSignals(summary);
  return (
    <Panel>
      <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
        <PanelHeading eyebrow="Concentración de señales" title="Mapa térmico operativo" />
        <div className="flex flex-wrap gap-3 text-[10px] uppercase tracking-wide text-slate-500">
          <LegendDot tone="good" label="Normal" />
          <LegendDot tone="warning" label="Vigilar" />
          <LegendDot tone="danger" label="Atender" />
        </div>
      </div>
      <div className="mt-7 grid gap-3 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-5">
        {signals.map(signal => <HeatCell key={signal.label} {...signal} />)}
      </div>
      <p className="mt-6 border-t border-white/[0.06] pt-4 text-xs text-slate-500">El color expresa condición, no volumen: rojo significa intervención; amarillo, observación; verde, operación normal.</p>
    </Panel>
  );
}

function FlowView({ summary }: { summary: SummaryResponse }) {
  const { transfers, kpis } = summary;
  const stages = [
    { label: "Cerradas hoy", value: kpis.totalTransfersToday, detail: "Resultado acumulado", tone: "gold" as Tone },
    { label: "En espera", value: transfers.waitingTransfers, detail: "Aún sin bytes", tone: "slate" as Tone },
    { label: "Transmitiendo", value: transfers.activeTransfers, detail: "Streams activos", tone: "blue" as Tone },
    { label: "Reanudables", value: transfers.pausedResumableTransfers, detail: "Parciales en custodia", tone: "gold" as Tone },
    { label: "Completadas", value: transfers.completedToday, detail: formatBytes(transfers.totalBytesTransferredToday), tone: "green" as Tone },
    { label: "Fallidas", value: transfers.failedToday, detail: "Durante el día", tone: "red" as Tone }
  ];

  return (
    <Panel>
      <PanelHeading eyebrow="Secuencia operativa" title="Flujo de transferencias" />
      <p className="mt-2 text-sm text-slate-500">Una lectura de izquierda a derecha del estado actual y los resultados acumulados del día.</p>
      <div className="mt-8 overflow-x-auto pb-3">
        <div className="flex min-w-[1120px] items-stretch">
          {stages.map((stage, index) => (
            <div key={stage.label} className="flex flex-1 items-center">
              <ProcessStage index={index + 1} {...stage} />
              {index < stages.length - 1 && <div className="mx-2 flex w-8 shrink-0 items-center"><span className="h-px flex-1 bg-gradient-to-r from-amber-300/35 to-transparent" /><span className="text-amber-300/40">›</span></div>}
            </div>
          ))}
        </div>
      </div>
      <div className="mt-5 grid gap-3 sm:grid-cols-4">
        <MiniMetric label="Éxito" value={formatPercent(kpis.successRateTodayPercent)} />
        <MiniMetric label="Throughput" value={`${transfers.averageThroughputMbps.toFixed(2)} Mbps`} />
        <MiniMetric label="Reanudadas" value={String(transfers.resumedTransfersToday)} />
        <MiniMetric label="Recibos" value={String(transfers.receiptsIssuedToday)} />
      </div>
    </Panel>
  );
}

function MosaicView({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  return (
    <div className="grid auto-rows-[150px] gap-4 sm:grid-cols-2 lg:grid-cols-4">
      <MosaicTile className="sm:col-span-2 sm:row-span-2" eyebrow="Índice operativo" value={formatPercent(kpis.operationalHealthScore)} detail={healthExplanation(summary)} tone={kpis.operationalHealthScore >= 85 ? "green" : "gold"} hero />
      <MosaicTile eyebrow="Éxito hoy" value={formatPercent(kpis.successRateTodayPercent)} detail={`${kpis.totalTransfersToday} cerradas`} tone="green" />
      <MosaicTile eyebrow="Fallidas" value={String(transfers.failedToday)} detail="Resultado del día" tone={transfers.failedToday > 0 ? "red" : "slate"} />
      <MosaicTile className="sm:col-span-2" eyebrow="Volumen confirmado" value={formatBytes(transfers.totalBytesTransferredToday)} detail={`${transfers.completedToday} archivos completados`} tone="blue" />
      <MosaicTile eyebrow="Memoria" value={formatPercent(server.memoryUsagePercent)} detail="Uso del servidor" tone={usageTone(server.memoryUsagePercent)} />
      <MosaicTile eyebrow="Disco" value={formatPercent(server.diskUsagePercent)} detail="Uso del servidor" tone={usageTone(server.diskUsagePercent)} />
      <MosaicTile eyebrow="Reanudables" value={String(transfers.pausedResumableTransfers)} detail="Parciales vigentes" tone="gold" />
      <MosaicTile eyebrow="Recibos" value={String(transfers.receiptsIssuedToday)} detail="Firmados hoy" tone="green" />
      <MosaicTile className="sm:col-span-2" eyebrow="Actividad institucional" value={`${kpis.distinctRfcsToday} RFC`} detail={`${formatBytes(kpis.averageFileSizeTodayBytes)} por archivo en promedio`} tone="slate" />
      <MosaicTile className="sm:col-span-2" eyebrow="Continuidad recuperada" value={formatBytes(transfers.resumedBytesToday)} detail={`${transfers.resumedTransfersToday} transferencias reanudadas`} tone="blue" />
    </div>
  );
}

function TimelineView({ summary }: { summary: SummaryResponse }) {
  const events = summary.recentTransfers.slice(0, 12);
  return (
    <div className="grid gap-5 xl:grid-cols-[1.35fr_0.65fr]">
      <Panel>
        <PanelHeading eyebrow="Secuencia verificable" title="Cronología de transferencias" />
        <div className="relative mt-7 space-y-1 before:absolute before:bottom-4 before:left-[7px] before:top-4 before:w-px before:bg-gradient-to-b before:from-amber-300/60 before:via-white/10 before:to-transparent">
          {events.length === 0 && <p className="text-sm text-slate-500">No hay eventos recientes.</p>}
          {events.map((event, index) => (
            <article key={`${event.traceId ?? event.fileName}-${index}`} className="relative grid grid-cols-[16px_1fr] gap-4 py-3">
              <span className={`relative z-10 mt-1 h-4 w-4 rounded-full border-4 border-[#11141a] ${statusDotClass(event.status)}`} />
              <div className="min-w-0 rounded-2xl border border-white/[0.06] bg-black/10 p-4">
                <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                  <p className="truncate text-sm font-semibold text-white" title={event.fileName}>{event.fileName}</p>
                  <span className="shrink-0 text-[10px] tabular-nums text-slate-500">{formatDateTime(event.createdUtc)}</span>
                </div>
                <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-slate-500"><StatusBadge status={event.status} /><span>{formatBytes(event.bytesReceived)}</span><span>{event.verificationRequired ? (event.verificationPerformed ? "Verificado" : "Verificación pendiente") : "Verificación omitida"}</span></div>
              </div>
            </article>
          ))}
        </div>
      </Panel>
      <div className="space-y-4">
        <MetricCard label="Última carga OK" value={formatDateTime(summary.kpis.lastCompletedUtc)} accent />
        <MetricCard label="Última falla" value={formatDateTime(summary.kpis.lastFailedUtc)} danger={Boolean(summary.kpis.lastFailedUtc)} />
        <MetricCard label="Duración promedio" value={formatSeconds(summary.kpis.averageUploadDurationTodaySeconds)} helper={`P95: ${formatSeconds(summary.kpis.p95UploadDurationTodaySeconds)}`} />
        <MetricCard label="Trazas mostradas" value={String(events.length)} helper="Ordenadas desde la más reciente" />
      </div>
    </div>
  );
}

function RadarView({ summary }: { summary: SummaryResponse }) {
  const metrics = buildRadarMetrics(summary);
  return (
    <div className="grid gap-5 xl:grid-cols-[1.1fr_0.9fr]">
      <Panel className="overflow-hidden">
        <PanelHeading eyebrow="Equilibrio multidimensional" title="Radar operativo" />
        <div className="mt-4 flex justify-center overflow-hidden"><RadarChart metrics={metrics} /></div>
        <p className="text-center text-xs leading-5 text-slate-500">Puntuaciones visuales derivadas del corte actual; no sustituyen un SLA contractual.</p>
      </Panel>
      <Panel>
        <PanelHeading eyebrow="Interpretación" title="Fortalezas y brechas" />
        <div className="mt-6 space-y-4">
          {metrics.map(metric => (
            <div key={metric.label}>
              <div className="flex items-center justify-between text-xs"><span className="font-medium text-slate-300">{metric.label}</span><span className="font-semibold tabular-nums text-white">{metric.value.toFixed(0)}</span></div>
              <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-white/[0.06]"><div className={`h-full rounded-full ${toneBar(metric.value >= 80 ? "green" : metric.value >= 55 ? "gold" : "red")}`} style={{ width: `${metric.value}%` }} /></div>
              <p className="mt-1.5 text-[11px] text-slate-500">{metric.detail}</p>
            </div>
          ))}
        </div>
      </Panel>
    </div>
  );
}

function TrafficView({ summary }: { summary: SummaryResponse }) {
  const signals = buildTrafficSignals(summary);
  const good = signals.filter(item => item.severity === "good");
  const warning = signals.filter(item => item.severity === "warning");
  const danger = signals.filter(item => item.severity === "danger");
  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div><p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-amber-300/70">Decisión inmediata</p><h3 className="mt-1 text-2xl font-semibold text-white">Semáforo operativo</h3></div>
        <p className="max-w-xl text-sm text-slate-500">Agrupa señales por acción requerida: continuar, vigilar o intervenir.</p>
      </div>
      <div className="grid gap-5 lg:grid-cols-3">
        <TrafficColumn severity="good" title="Operando" subtitle="Sin intervención" items={good} />
        <TrafficColumn severity="warning" title="Vigilar" subtitle="Seguimiento recomendado" items={warning} />
        <TrafficColumn severity="danger" title="Atender" subtitle="Intervención prioritaria" items={danger} />
      </div>
    </div>
  );
}

function RingMetric({ label, value, display, inverse = false }: { label: string; value: number; display: string; inverse?: boolean }) {
  const safe = clamp(value, 0, 100);
  const visualTone: Tone = (inverse ? safe < 15 : safe < 55) ? "red" : safe < 80 ? "gold" : "green";
  const color = visualTone === "green" ? "#34d399" : visualTone === "gold" ? "#f2b96b" : "#fb7185";
  return (
    <div className="text-center">
      <div className="mx-auto grid h-32 w-32 place-items-center rounded-full p-2" style={{ background: `conic-gradient(${color} ${safe}%, rgba(255,255,255,.06) 0)` }}><div className="grid h-full w-full place-items-center rounded-full bg-[#11141a]"><span className="px-2 text-center text-sm font-semibold text-white">{display}</span></div></div>
      <p className="mt-3 text-xs font-semibold uppercase tracking-wider text-slate-400">{label}</p>
    </div>
  );
}

function HeatCell({ label, value, detail, severity }: { label: string; value: string; detail: string; severity: Severity }) {
  const styles = severity === "good" ? "border-emerald-400/20 bg-emerald-400/[0.08] text-emerald-200" : severity === "warning" ? "border-amber-400/25 bg-amber-400/[0.09] text-amber-100" : "border-rose-400/30 bg-rose-400/[0.11] text-rose-100";
  return <div className={`min-h-32 rounded-2xl border p-4 ${styles}`}><p className="text-[10px] font-semibold uppercase tracking-[0.16em] opacity-65">{label}</p><p className="mt-3 text-2xl font-semibold text-white">{value}</p><p className="mt-2 text-[11px] leading-4 opacity-65">{detail}</p></div>;
}

function LegendDot({ tone, label }: { tone: Severity; label: string }) {
  const color = tone === "good" ? "bg-emerald-400" : tone === "warning" ? "bg-amber-400" : "bg-rose-400";
  return <span className="flex items-center gap-1.5"><span className={`h-2 w-2 rounded-full ${color}`} />{label}</span>;
}

function ProcessStage({ index, label, value, detail, tone }: { index: number; label: string; value: number; detail: string; tone: Tone }) {
  return <div className={`min-h-44 min-w-36 flex-1 rounded-2xl border p-4 ${tonePanel(tone)}`}><span className="text-[10px] font-semibold tracking-widest opacity-50">{String(index).padStart(2, "0")}</span><p className="mt-5 text-4xl font-semibold text-white">{value}</p><p className="mt-2 text-sm font-semibold">{label}</p><p className="mt-1 text-[11px] opacity-55">{detail}</p></div>;
}

function MosaicTile({ eyebrow, value, detail, tone, className = "", hero = false }: { eyebrow: string; value: string; detail: string; tone: Tone; className?: string; hero?: boolean }) {
  return <article className={`relative overflow-hidden rounded-3xl border p-5 ${tonePanel(tone)} ${className}`}><div className="pointer-events-none absolute -right-12 -top-12 h-32 w-32 rounded-full bg-current opacity-[0.04] blur-2xl" /><p className="relative text-[10px] font-semibold uppercase tracking-[0.18em] opacity-60">{eyebrow}</p><p className={`relative mt-3 font-semibold tracking-tight text-white ${hero ? "text-5xl sm:text-6xl" : "text-3xl"}`}>{value}</p><p className={`relative mt-2 text-current opacity-60 ${hero ? "max-w-sm text-sm leading-6" : "text-xs"}`}>{detail}</p></article>;
}

function RadarChart({ metrics }: { metrics: Array<{ label: string; value: number; detail: string }> }) {
  const centerX = 210;
  const centerY = 180;
  const radius = 120;
  const points = (values: number[]) => values.map((value, index) => radarPoint(index, values.length, radius * clamp(value, 0, 100) / 100, centerX, centerY)).map(point => `${point.x},${point.y}`).join(" ");
  const fullValues = metrics.map(() => 100);
  return (
    <svg viewBox="0 0 420 380" className="h-auto w-full max-w-[520px]" role="img" aria-label="Radar de equilibrio operativo">
      {[25, 50, 75, 100].map(level => <polygon key={level} points={points(metrics.map(() => level))} fill="none" stroke="rgba(255,255,255,.08)" strokeWidth="1" />)}
      {metrics.map((metric, index) => {
        const outer = radarPoint(index, metrics.length, radius, centerX, centerY);
        const label = radarPoint(index, metrics.length, radius + 32, centerX, centerY);
        return <g key={metric.label}><line x1={centerX} y1={centerY} x2={outer.x} y2={outer.y} stroke="rgba(255,255,255,.08)" /><text x={label.x} y={label.y} fill="#94a3b8" fontSize="11" textAnchor="middle" dominantBaseline="middle">{metric.label}</text></g>;
      })}
      <polygon points={points(metrics.map(metric => metric.value))} fill="rgba(242,185,107,.18)" stroke="#f2b96b" strokeWidth="2" />
      {metrics.map((metric, index) => { const point = radarPoint(index, fullValues.length, radius * metric.value / 100, centerX, centerY); return <circle key={metric.label} cx={point.x} cy={point.y} r="4" fill="#f2b96b" stroke="#11141a" strokeWidth="2" />; })}
    </svg>
  );
}

function TrafficColumn({ severity, title, subtitle, items }: { severity: Severity; title: string; subtitle: string; items: Array<{ label: string; detail: string; severity: Severity }> }) {
  const styles = severity === "good" ? "border-emerald-400/18 bg-emerald-400/[0.035]" : severity === "warning" ? "border-amber-400/20 bg-amber-400/[0.04]" : "border-rose-400/25 bg-rose-400/[0.05]";
  const light = severity === "good" ? "bg-emerald-400 shadow-[0_0_24px_#34d399]" : severity === "warning" ? "bg-amber-400 shadow-[0_0_24px_#fbbf24]" : "bg-rose-400 shadow-[0_0_24px_#fb7185]";
  return <section className={`rounded-3xl border p-5 ${styles}`}><div className="flex items-center gap-3"><span className={`h-4 w-4 rounded-full ${light}`} /><div><h4 className="font-semibold text-white">{title}</h4><p className="text-[11px] text-slate-500">{subtitle}</p></div><span className="ml-auto text-2xl font-semibold text-white">{items.length}</span></div><div className="mt-5 space-y-2">{items.length === 0 && <p className="rounded-xl border border-white/[0.06] p-4 text-xs text-slate-500">No hay señales en esta categoría.</p>}{items.map(item => <div key={item.label} className="rounded-xl border border-white/[0.06] bg-black/10 p-3"><p className="text-xs font-semibold text-slate-200">{item.label}</p><p className="mt-1 text-[11px] leading-4 text-slate-500">{item.detail}</p></div>)}</div></section>;
}

function buildHeatSignals(summary: SummaryResponse): Array<{ label: string; value: string; detail: string; severity: Severity }> {
  const { server, transfers, kpis } = summary;
  return [
    { label: "Salud operativa", value: formatPercent(kpis.operationalHealthScore), detail: "Índice compuesto", severity: scoreSeverity(kpis.operationalHealthScore) },
    { label: "Éxito hoy", value: formatPercent(kpis.successRateTodayPercent), detail: `${kpis.totalTransfersToday} cerradas`, severity: kpis.totalTransfersToday === 0 ? "warning" : scoreSeverity(kpis.successRateTodayPercent) },
    { label: "Fallas · 1 h", value: formatPercent(kpis.failureRateLastHourPercent), detail: `${transfers.failedLastHour} transferencias`, severity: inverseSeverity(kpis.failureRateLastHourPercent, 5, 15) },
    { label: "Pendientes vencidos", value: String(kpis.stalePendingTransfers), detail: "Más de 30 minutos", severity: countSeverity(kpis.stalePendingTransfers, 1, 5) },
    { label: "CPU", value: formatPercent(server.cpuUsagePercent), detail: "Utilización actual", severity: inverseSeverity(server.cpuUsagePercent, 75, 90) },
    { label: "Memoria", value: formatPercent(server.memoryUsagePercent), detail: "Utilización actual", severity: inverseSeverity(server.memoryUsagePercent, 75, 90) },
    { label: "Disco", value: formatPercent(server.diskUsagePercent), detail: "Utilización actual", severity: inverseSeverity(server.diskUsagePercent, 75, 90) },
    { label: "Base de datos", value: server.databaseOk ? "Disponible" : "Error", detail: "Dependencia crítica", severity: server.databaseOk ? "good" : "danger" },
    { label: "Almacenamiento", value: server.storageOk ? "Disponible" : "Error", detail: "Destino de archivos", severity: server.storageOk ? "good" : "danger" },
    { label: "Bus interno", value: server.messageBusOk ? "Disponible" : "Error", detail: "Mensajería operativa", severity: server.messageBusOk ? "good" : "danger" },
    { label: "Alertas", value: server.alertingEnabled ? (server.alertingOk ? "Disponible" : "Error") : "Modo local", detail: `${server.alertDeadLetter} en dead-letter`, severity: server.alertingEnabled && !server.alertingOk ? "danger" : server.alertingEnabled ? "good" : "warning" },
    { label: "En transmisión", value: String(transfers.activeTransfers), detail: "Streams actuales", severity: "good" },
    { label: "Reanudables", value: String(transfers.pausedResumableTransfers), detail: "Parciales protegidos", severity: countSeverity(transfers.pausedResumableTransfers, 3, 10) },
    { label: "Recibos", value: String(transfers.receiptsIssuedToday), detail: "Emitidos hoy", severity: transfers.completedToday > transfers.receiptsIssuedToday ? "warning" : "good" },
    { label: "Verificación omitida", value: String(transfers.verificationBypassedToday), detail: "Según política", severity: transfers.verificationBypassedToday > 0 ? "warning" : "good" }
  ];
}

function buildRadarMetrics(summary: SummaryResponse): Array<{ label: string; value: number; detail: string }> {
  const { server, transfers, kpis } = summary;
  const continuityBase = transfers.pausedResumableTransfers + transfers.resumedTransfersToday;
  const continuity = continuityBase === 0 ? 100 : clamp(transfers.resumedTransfersToday / continuityBase * 100, 0, 100);
  const integrity = transfers.completedToday === 0 ? 100 : clamp((transfers.completedToday - transfers.verificationBypassedToday) / transfers.completedToday * 100, 0, 100);
  return [
    { label: "Salud", value: clamp(kpis.operationalHealthScore, 0, 100), detail: "Índice operativo compuesto" },
    { label: "Éxito", value: kpis.totalTransfersToday === 0 ? 100 : clamp(kpis.successRateTodayPercent, 0, 100), detail: "Transferencias cerradas correctamente" },
    { label: "Holgura", value: clamp(100 - Math.max(server.cpuUsagePercent, server.memoryUsagePercent, server.diskUsagePercent), 0, 100), detail: "Capacidad libre del recurso más presionado" },
    { label: "Estabilidad", value: clamp(100 - kpis.failureRateLastHourPercent, 0, 100), detail: "Ausencia de fallas en la última hora" },
    { label: "Continuidad", value: continuity, detail: "Relación entre reanudadas y parciales vigentes" },
    { label: "Integridad", value: integrity, detail: "Cargas sin omisión de verificación" }
  ];
}

function buildTrafficSignals(summary: SummaryResponse): Array<{ label: string; detail: string; severity: Severity }> {
  const { server, transfers, kpis } = summary;
  return [
    { label: "Base de datos", detail: server.databaseOk ? "Conectividad confirmada." : "La base de datos no responde.", severity: server.databaseOk ? "good" : "danger" },
    { label: "Almacenamiento", detail: server.storageOk ? "Destino disponible." : "El destino de archivos no está disponible.", severity: server.storageOk ? "good" : "danger" },
    { label: "Bus interno", detail: server.messageBusOk ? "Mensajería operativa." : "La mensajería interna reporta falla.", severity: server.messageBusOk ? "good" : "danger" },
    { label: "CPU", detail: `${formatPercent(server.cpuUsagePercent)} de utilización.`, severity: inverseSeverity(server.cpuUsagePercent, 75, 90) },
    { label: "Memoria", detail: `${formatPercent(server.memoryUsagePercent)} de utilización.`, severity: inverseSeverity(server.memoryUsagePercent, 75, 90) },
    { label: "Disco", detail: `${formatPercent(server.diskUsagePercent)} de utilización.`, severity: inverseSeverity(server.diskUsagePercent, 75, 90) },
    { label: "Fallas recientes", detail: `${formatPercent(kpis.failureRateLastHourPercent)} durante la última hora.`, severity: inverseSeverity(kpis.failureRateLastHourPercent, 5, 15) },
    { label: "Pendientes vencidos", detail: `${kpis.stalePendingTransfers} cargas superan 30 minutos.`, severity: countSeverity(kpis.stalePendingTransfers, 1, 5) },
    { label: "Alertas no entregadas", detail: `${server.alertDeadLetter} mensajes en dead-letter.`, severity: countSeverity(server.alertDeadLetter, 1, 1) },
    { label: "Verificación omitida", detail: `${transfers.verificationBypassedToday} archivos por política explícita.`, severity: transfers.verificationBypassedToday > 0 ? "warning" : "good" },
    { label: "Recibos", detail: `${transfers.receiptsIssuedToday} emitidos para ${transfers.completedToday} completadas.`, severity: transfers.receiptsIssuedToday < transfers.completedToday ? "warning" : "good" }
  ];
}

function radarPoint(index: number, total: number, radius: number, centerX: number, centerY: number): { x: number; y: number } {
  const angle = -Math.PI / 2 + index * Math.PI * 2 / total;
  return { x: centerX + Math.cos(angle) * radius, y: centerY + Math.sin(angle) * radius };
}

function scoreSeverity(value: number): Severity {
  return value >= 85 ? "good" : value >= 60 ? "warning" : "danger";
}

function inverseSeverity(value: number, warningAt: number, dangerAt: number): Severity {
  return value >= dangerAt ? "danger" : value >= warningAt ? "warning" : "good";
}

function countSeverity(value: number, warningAt: number, dangerAt: number): Severity {
  return value >= dangerAt ? "danger" : value >= warningAt ? "warning" : "good";
}

function largestResource(server: SummaryResponse["server"]): string {
  const values = [{ label: "CPU", value: server.cpuUsagePercent }, { label: "Memoria", value: server.memoryUsagePercent }, { label: "Disco", value: server.diskUsagePercent }];
  const largest = values.sort((left, right) => right.value - left.value)[0];
  return `${largest.label} · ${formatPercent(largest.value)}`;
}

function RecentTransfers({ transfers }: { transfers: RecentTransferDto[] }) {
  return (
    <section className="overflow-hidden rounded-3xl border border-white/[0.07] bg-white/[0.025]">
      <div className="flex flex-col gap-1 border-b border-white/[0.06] px-5 py-4 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-amber-300/70">Trazabilidad</p>
          <h3 className="mt-1 text-lg font-semibold">Transferencias recientes</h3>
        </div>
        <span className="text-xs text-slate-500">{transfers.length} registros visibles</span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full min-w-[980px] text-left text-xs">
          <thead className="bg-black/15 text-[10px] uppercase tracking-wider text-slate-500">
            <tr><th className="px-5 py-3 font-semibold">Archivo</th><th className="py-3 font-semibold">Estado</th><th className="py-3 font-semibold">Tamaño</th><th className="py-3 font-semibold">Política</th><th className="py-3 font-semibold">Verificación</th><th className="py-3 font-semibold">Traza</th><th className="py-3 pr-5 font-semibold">Recibo</th></tr>
          </thead>
          <tbody>
            {transfers.map((transfer, index) => (
              <tr key={`${transfer.traceId ?? transfer.fileName}-${index}`} className="border-t border-white/[0.05] text-slate-300 transition hover:bg-white/[0.025]">
                <td className="max-w-72 truncate px-5 py-3.5 font-medium text-slate-100" title={transfer.fileName}>{transfer.fileName}</td>
                <td><StatusBadge status={transfer.status} /></td>
                <td className="tabular-nums">{formatBytes(transfer.bytesReceived)}</td>
                <td>{transfer.policyExtension ?? "—"}{transfer.policyVersion ? ` · v${transfer.policyVersion}` : ""}</td>
                <td>{transfer.verificationRequired ? (transfer.verificationPerformed ? "Ejecutada" : "Pendiente") : "Omitida por política"}</td>
                <td className="max-w-40 truncate font-mono text-[10px] text-slate-500" title={transfer.traceId ?? undefined}>{transfer.traceId ?? "—"}</td>
                <td className="max-w-40 truncate pr-5 font-mono text-[10px] text-slate-500" title={transfer.receiptId ?? undefined}>{transfer.receiptId ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {transfers.length === 0 && <p className="px-5 py-10 text-center text-sm text-slate-500">No existen transferencias recientes.</p>}
      </div>
    </section>
  );
}

function HealthRing({ score, healthy }: { score: number; healthy: boolean }) {
  const safeScore = clamp(score, 0, 100);
  const color = !healthy || safeScore < 60 ? "#fb7185" : safeScore < 85 ? "#f2b96b" : "#4ade80";
  return (
    <div className="relative grid h-48 w-48 place-items-center rounded-full p-3" style={{ background: `conic-gradient(${color} 0% ${safeScore}%, rgba(255,255,255,0.07) ${safeScore}% 100%)` }}>
      <div className="absolute inset-2 rounded-full bg-[#12151b]" />
      <div className="relative text-center"><p className="text-5xl font-semibold tracking-tighter text-white">{safeScore.toFixed(0)}</p><p className="mt-1 text-[10px] font-semibold uppercase tracking-[0.24em] text-slate-500">de 100</p></div>
      <span className="ase-pulse absolute -right-1 top-1/2 h-3 w-3 rounded-full" style={{ backgroundColor: color, boxShadow: `0 0 24px ${color}` }} />
    </div>
  );
}

function FlowNode(props: { index: string; label: string; value: number; detail: string; tone: Tone; active?: boolean }) {
  return (
    <div className={`relative rounded-2xl border p-4 ${tonePanel(props.tone)}`}>
      <div className="flex items-center justify-between"><span className="text-[10px] font-semibold tracking-widest text-current opacity-55">{props.index}</span>{props.active && <span className="ase-pulse h-2 w-2 rounded-full bg-sky-400 shadow-[0_0_16px_#38bdf8]" />}</div>
      <p className="mt-5 text-3xl font-semibold tabular-nums text-white">{props.value}</p><p className="mt-1 text-sm font-semibold">{props.label}</p><p className="mt-1 text-[11px] text-current opacity-55">{props.detail}</p>
    </div>
  );
}

function ResourceDial({ label, value }: { label: string; value: number }) {
  const safe = clamp(value, 0, 100);
  return (
    <div className="rounded-2xl border border-white/[0.06] bg-black/10 p-4">
      <div className="flex items-center justify-between text-xs"><span className="font-semibold text-slate-300">{label}</span><span className="tabular-nums text-slate-400">{safe.toFixed(1)} %</span></div>
      <div className="mt-3 h-1.5 overflow-hidden rounded-full bg-white/[0.06]"><div className={`h-full rounded-full ${toneBar(usageTone(safe))}`} style={{ width: `${safe}%` }} /></div>
    </div>
  );
}

function ServiceTile({ label, ok, detail }: { label: string; ok: boolean; detail?: string }) {
  return (
    <div className={`rounded-2xl border p-4 ${ok ? "border-emerald-400/15 bg-emerald-400/[0.045]" : "border-rose-400/25 bg-rose-500/[0.07]"}`}>
      <div className="flex items-center gap-2"><span className={`h-2 w-2 rounded-full ${ok ? "bg-emerald-400 shadow-[0_0_12px_#4ade80]" : "bg-rose-400 shadow-[0_0_12px_#fb7185]"}`} /><span className="text-xs font-semibold text-slate-200">{label}</span></div>
      <p className={`mt-3 text-[11px] ${ok ? "text-emerald-300/75" : "text-rose-200"}`}>{detail ?? (ok ? "Operando" : "Requiere atención")}</p>
    </div>
  );
}

function ChartPanel(props: { eyebrow: string; title: string; footer: string; children: React.ReactNode }) {
  return <Panel><PanelHeading eyebrow={props.eyebrow} title={props.title} /><div className="mt-7 space-y-5">{props.children}</div><p className="mt-6 border-t border-white/[0.06] pt-4 text-xs text-slate-500">{props.footer}</p></Panel>;
}

function MetricBar(props: { label: string; value: number; max: number; display: string; tone: Tone }) {
  const width = props.value <= 0 ? 0 : Math.max(3, clamp(props.value / props.max * 100, 0, 100));
  return (
    <div><div className="mb-2 flex items-end justify-between gap-4 text-xs"><span className="font-medium text-slate-300">{props.label}</span><span className="font-semibold tabular-nums text-white">{props.display}</span></div><div className="h-3 overflow-hidden rounded-full bg-white/[0.055]"><div className={`h-full rounded-full transition-[width] duration-700 ${toneBar(props.tone)}`} style={{ width: `${width}%` }} /></div></div>
  );
}

function MetricCard(props: { label: string; value: string; helper?: string; accent?: boolean; danger?: boolean }) {
  const border = props.danger ? "border-rose-400/25 bg-rose-500/[0.055]" : props.accent ? "border-amber-300/20 bg-amber-400/[0.055]" : "border-white/[0.07] bg-white/[0.025]";
  return <div className={`rounded-2xl border p-4 ${border}`}><p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-500">{props.label}</p><p className={`mt-2 text-2xl font-semibold tracking-tight ${props.danger ? "text-rose-100" : "text-white"}`}>{props.value}</p>{props.helper && <p className="mt-2 text-xs leading-5 text-slate-500">{props.helper}</p>}</div>;
}

function SignalCard({ label, value, detail }: { label: string; value: string; detail: string }) {
  return <div className="rounded-2xl border border-white/[0.06] bg-black/10 p-4"><p className="text-[10px] uppercase tracking-wider text-slate-500">{label}</p><p className="mt-2 text-xl font-semibold text-white">{value}</p><p className="mt-1 text-[11px] text-slate-500">{detail}</p></div>;
}

function MiniMetric({ label, value, danger = false }: { label: string; value: string; danger?: boolean }) {
  return <div className="min-w-0 text-center"><p className={`truncate text-lg font-semibold ${danger ? "text-rose-300" : "text-white"}`}>{value}</p><p className="mt-1 truncate text-[10px] uppercase tracking-wide text-slate-500">{label}</p></div>;
}

function Panel({ children, className = "" }: { children: React.ReactNode; className?: string }) {
  return <section className={`rounded-3xl border border-white/[0.07] bg-white/[0.025] p-5 shadow-[0_20px_60px_rgba(0,0,0,0.18)] sm:p-6 ${className}`}>{children}</section>;
}

function PanelHeading({ eyebrow, title }: { eyebrow: string; title: string }) {
  return <div><p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-amber-300/70">{eyebrow}</p><h3 className="mt-1 text-xl font-semibold tracking-tight text-white">{title}</h3></div>;
}

function SectionTitle({ title, subtitle }: { title: string; subtitle: string }) {
  return <div><h3 className="text-lg font-semibold text-white">{title}</h3><p className="mt-1 text-sm text-slate-500">{subtitle}</p></div>;
}

function LiveBadge({ healthy }: { healthy: boolean }) {
  return <span className={`flex items-center gap-2 rounded-full border px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wide ${healthy ? "border-emerald-400/20 bg-emerald-400/[0.07] text-emerald-300" : "border-rose-400/25 bg-rose-400/[0.08] text-rose-200"}`}><span className={`h-1.5 w-1.5 rounded-full ${healthy ? "ase-pulse bg-emerald-400" : "bg-rose-400"}`} />{healthy ? "Sistema en línea" : "Atención requerida"}</span>;
}

function StatusBadge({ status }: { status: string }) {
  const kind = statusKind(status);
  const styles = kind === "completed" ? "border-emerald-400/20 bg-emerald-400/[0.08] text-emerald-300" : kind === "failed" ? "border-rose-400/25 bg-rose-400/[0.08] text-rose-200" : kind === "active" ? "border-sky-400/20 bg-sky-400/[0.08] text-sky-200" : "border-amber-400/20 bg-amber-400/[0.08] text-amber-200";
  return <span className={`inline-flex rounded-full border px-2 py-1 text-[10px] font-semibold uppercase tracking-wide ${styles}`}>{status}</span>;
}

function ViewGlyph({ mode }: { mode: ViewMode }) {
  if (mode === "bars") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M4 20V10m6 10V4m6 16v-7m4 7H2" /></svg>;
  if (mode === "text") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M5 6h14M5 12h14M5 18h9" /></svg>;
  if (mode === "rings") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><circle cx="12" cy="12" r="8" /><circle cx="12" cy="12" r="4" /></svg>;
  if (mode === "heatmap") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="currentColor"><rect x="3" y="3" width="5" height="5" rx="1" opacity=".35" /><rect x="10" y="3" width="5" height="5" rx="1" opacity=".7" /><rect x="17" y="3" width="4" height="5" rx="1" /><rect x="3" y="10" width="5" height="5" rx="1" /><rect x="10" y="10" width="5" height="5" rx="1" opacity=".3" /><rect x="17" y="10" width="4" height="5" rx="1" opacity=".65" /><rect x="3" y="17" width="5" height="4" rx="1" opacity=".6" /><rect x="10" y="17" width="5" height="4" rx="1" /><rect x="17" y="17" width="4" height="4" rx="1" opacity=".3" /></svg>;
  if (mode === "flow") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M4 6h7a3 3 0 0 1 3 3v6a3 3 0 0 0 3 3h3M17 15l3 3-3 3M4 18h5" /></svg>;
  if (mode === "mosaic") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><rect x="3" y="3" width="10" height="10" rx="2" /><rect x="15" y="3" width="6" height="6" rx="2" /><rect x="15" y="11" width="6" height="10" rx="2" /><rect x="3" y="15" width="10" height="6" rx="2" /></svg>;
  if (mode === "timeline") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="M7 3v18M7 6h10M7 12h7M7 18h11" /><circle cx="7" cy="6" r="1.5" fill="currentColor" /><circle cx="7" cy="12" r="1.5" fill="currentColor" /><circle cx="7" cy="18" r="1.5" fill="currentColor" /></svg>;
  if (mode === "radar") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><path d="m12 3 8 6-3 10H7L4 9l8-6Z" /><path d="m12 7 4 3-1.5 5H9.5L8 10l4-3Z" /></svg>;
  if (mode === "traffic") return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><rect x="7" y="2" width="10" height="20" rx="4" /><circle cx="12" cy="7" r="2" /><circle cx="12" cy="12" r="2" /><circle cx="12" cy="17" r="2" /></svg>;
  return <svg aria-hidden="true" viewBox="0 0 24 24" className="h-5 w-5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8"><circle cx="12" cy="12" r="3" /><circle cx="12" cy="12" r="8" strokeDasharray="3 3" /></svg>;
}

function DashboardSkeleton() {
  return <div className="space-y-6" role="status" aria-live="polite"><div className="h-24 animate-pulse rounded-3xl bg-white/[0.035]" /><div className="grid gap-5 xl:grid-cols-[0.82fr_1.6fr]"><div className="h-96 animate-pulse rounded-3xl bg-white/[0.035]" /><div className="h-96 animate-pulse rounded-3xl bg-white/[0.035]" /></div><p className="text-sm text-slate-500">Preparando el centro de control...</p></div>;
}

function EmptyState({ title, detail, tone = "neutral" }: { title: string; detail: string; tone?: "neutral" | "error" }) {
  return <div className={`rounded-3xl border p-8 ${tone === "error" ? "border-rose-400/25 bg-rose-500/[0.06]" : "border-white/[0.07] bg-white/[0.025]"}`}><p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-amber-300/70">Centro de control</p><h2 className="mt-3 text-2xl font-semibold">{title}</h2><p className="mt-2 text-sm text-slate-400">{detail}</p></div>;
}

function buildExecutiveObservations(summary: SummaryResponse): Array<{ title: string; body: string; tone: Tone }> {
  const { server, transfers, kpis } = summary;
  const activity = kpis.totalTransfersToday === 0 ? "Todavía no hay transferencias cerradas durante el día; el porcentaje de éxito aparecerá cuando exista actividad." : `Hoy cerraron ${kpis.totalTransfersToday} transferencias con ${formatPercent(kpis.successRateTodayPercent)} de éxito y ${formatBytes(transfers.totalBytesTransferredToday)} confirmados.`;
  const attentionCount = kpis.stalePendingTransfers + transfers.failedLastHour + server.alertDeadLetter;
  const attention = attentionCount === 0 ? "No se detectan pendientes vencidos, fallas recientes ni alertas en dead-letter. No hay intervención inmediata pendiente." : `Se detectan ${kpis.stalePendingTransfers} pendientes vencidos, ${transfers.failedLastHour} fallas en la última hora y ${server.alertDeadLetter} alertas que requieren revisión.`;
  const continuity = transfers.pausedResumableTransfers === 0 && transfers.resumedTransfersToday === 0
    ? "No hay cargas parciales por reanudar. El mecanismo de continuidad permanece disponible y sin trabajo acumulado."
    : `${countLabel(transfers.pausedResumableTransfers, "carga reanudable", "cargas reanudables")} en custodia; hoy se recuperaron ${formatBytes(transfers.resumedBytesToday)} en ${countLabel(transfers.resumedTransfersToday, "transferencia", "transferencias")}.`;
  return [
    { title: server.isHealthy ? "Operación estable" : "Operación degradada", body: `${healthExplanation(summary)} ${activity}`, tone: server.isHealthy ? "green" : "red" },
    { title: attentionCount === 0 ? "Sin focos rojos" : "Atención recomendada", body: attention, tone: attentionCount === 0 ? "green" : "gold" },
    { title: "Continuidad de carga", body: continuity, tone: "blue" }
  ];
}

function healthExplanation(summary: SummaryResponse): string {
  const score = summary.kpis.operationalHealthScore;
  if (!summary.server.isHealthy) return "Una o más dependencias críticas reportan degradación; revise la constelación de servicios.";
  if (score >= 90) return "El ecosistema opera dentro de parámetros saludables y sin presión significativa en la cola.";
  if (score >= 70) return "El servidor está disponible, aunque existen señales operativas que conviene vigilar.";
  return "El servicio responde, pero el índice compuesto exige atención operativa.";
}

function readPreferredView(): ViewMode {
  const value = window.localStorage.getItem(VIEW_PREFERENCE_KEY);
  const validViews: ViewMode[] = ["pulse", "bars", "text", "rings", "heatmap", "flow", "mosaic", "timeline", "radar", "traffic"];
  return validViews.includes(value as ViewMode) ? value as ViewMode : "pulse";
}

function statusKind(status: string): "completed" | "failed" | "active" | "pending" {
  const normalized = status.trim().toLowerCase();
  if (["completed", "complete", "completado", "completedwithwarnings"].some(value => normalized.includes(value))) return "completed";
  if (["failed", "error", "rejected", "fallido", "cancelled"].some(value => normalized.includes(value))) return "failed";
  if (["uploading", "active", "transmitting", "verifying"].some(value => normalized.includes(value))) return "active";
  return "pending";
}

function statusDotClass(status: string): string {
  const kind = statusKind(status);
  if (kind === "completed") return "border-emerald-300/25 bg-emerald-400/70 shadow-[0_0_12px_rgba(74,222,128,0.18)]";
  if (kind === "failed") return "border-rose-300/30 bg-rose-400/80 shadow-[0_0_12px_rgba(251,113,133,0.2)]";
  if (kind === "active") return "border-sky-300/25 bg-sky-400/75 shadow-[0_0_12px_rgba(56,189,248,0.2)]";
  return "border-amber-300/25 bg-amber-400/60";
}

function tonePanel(tone: Tone): string {
  return ({ gold: "border-amber-400/15 bg-amber-400/[0.045] text-amber-200", green: "border-emerald-400/15 bg-emerald-400/[0.045] text-emerald-200", red: "border-rose-400/20 bg-rose-400/[0.055] text-rose-200", blue: "border-sky-400/15 bg-sky-400/[0.045] text-sky-200", slate: "border-white/[0.07] bg-white/[0.025] text-slate-300" } satisfies Record<Tone, string>)[tone];
}

function toneBar(tone: Tone): string {
  return ({ gold: "bg-gradient-to-r from-amber-700 to-amber-400", green: "bg-gradient-to-r from-emerald-700 to-emerald-400", red: "bg-gradient-to-r from-rose-800 to-rose-400", blue: "bg-gradient-to-r from-sky-800 to-sky-400", slate: "bg-gradient-to-r from-slate-700 to-slate-400" } satisfies Record<Tone, string>)[tone];
}

function toneDot(tone: Tone): string {
  return ({ gold: "bg-amber-400 shadow-[0_0_14px_#fbbf24]", green: "bg-emerald-400 shadow-[0_0_14px_#4ade80]", red: "bg-rose-400 shadow-[0_0_14px_#fb7185]", blue: "bg-sky-400 shadow-[0_0_14px_#38bdf8]", slate: "bg-slate-400" } satisfies Record<Tone, string>)[tone];
}

function usageTone(value: number): Tone {
  if (value >= 90) return "red";
  if (value >= 75) return "gold";
  return "green";
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  if (bytes < 1024) return `${bytes.toFixed(0)} B`;
  const units = ["KB", "MB", "GB", "TB", "PB"];
  let index = -1;
  do { bytes /= 1024; index++; } while (bytes >= 1024 && index < units.length - 1);
  return `${bytes.toFixed(2)} ${units[index]}`;
}

function formatPercent(value: number): string {
  return `${Number.isFinite(value) ? value.toFixed(1) : "0.0"} %`;
}

function formatSeconds(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return "0 s";
  if (value < 60) return `${value.toFixed(1)} s`;
  return `${Math.floor(value / 60)} min ${Math.round(value % 60)} s`;
}

function formatDateTime(value?: string | null): string {
  if (!value) return "Sin dato";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "Sin dato" : date.toLocaleString();
}

function buildServicesHelper(server: SummaryResponse["server"]): string {
  return [`DB ${server.databaseOk ? "OK" : "Error"}`, `Storage ${server.storageOk ? "OK" : "Error"}`, `Bus ${server.messageBusOk ? "OK" : "Error"}`, `Alertas ${server.alertingEnabled ? (server.alertingOk ? "OK" : "Error") : "locales"}`].join(" · ");
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Number.isFinite(value) ? value : min));
}

function countLabel(value: number, singular: string, plural: string): string {
  return `${value} ${value === 1 ? singular : plural}`;
}
