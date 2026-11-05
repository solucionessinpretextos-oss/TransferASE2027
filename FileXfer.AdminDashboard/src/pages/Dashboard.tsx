// DOCUMENTACIÓN DE MANTENIMIENTO — TRANSFER ASE
// Consola operativa con diez representaciones complementarias del mismo resumen.

import { useEffect, useState, type ComponentProps } from "react";
import { useAuth } from "../auth/AuthContext";
import { RecentTransferDto, SummaryResponse } from "../types/admin";
import { MaintenanceRequest, TransferMaintenance } from "../components/TransferMaintenance";
import { StorageDiagnostics } from "../components/StorageDiagnostics";
import { AlertDeliveryDiagnostics } from "../components/AlertDeliveryDiagnostics";
import { HelpSteps, HelpTrigger } from "../components/ContextualHelp";

const REFRESH_INTERVAL_MS = 2_000;
const VIEW_PREFERENCE_KEY = "filexfer.admin.dashboard-view.v1";

type ViewMode = typeof dashboardViews[number]["mode"];
type Tone = "gold" | "green" | "red" | "blue" | "slate";
type Severity = "good" | "warning" | "danger";
type StatusKind = "completed" | "transmitting" | "pending" | "paused" | "catalogFailed" | "failed" | "cancelled" | "unknown";
type KpiTone = "green" | "red" | "blue" | "gold" | "violet" | "slate";
type DashboardSection = "overview" | "health" | "maintenance" | "activity";

const dashboardSections = [
  { id: "overview", label: "Panorama" },
  { id: "health", label: "Diagnóstico" },
  { id: "maintenance", label: "Mantenimiento" },
  { id: "activity", label: "Actividad" }
] as const;

const dashboardViews = [
  { mode: "pulse", label: "Pulso", helper: "Estado general", description: "Salud, servicios, recursos y señales de actividad reciente.", Component: PulseView },
  { mode: "bars", label: "Barras", helper: "Comparación", description: "Compara capacidad, resultados, cola y evidencia en escalas comunes.", Component: BarsView },
  { mode: "text", label: "Ejecutiva", helper: "Lectura rápida", description: "Lee el corte operativo y las observaciones en texto.", Component: TextView },
  { mode: "rings", label: "Anillos", helper: "Proporciones", description: "Compara porcentajes de salud, resultados, capacidad y verificación.", Component: RingsView },
  { mode: "heatmap", label: "Térmico", helper: "Intensidad", description: "Localiza las señales que requieren seguimiento por nivel de atención.", Component: HeatmapView },
  { mode: "flow", label: "Flujo", helper: "Etapas", description: "Consulta la cola actual y los resultados del día por etapa.", Component: FlowView },
  { mode: "mosaic", label: "Mosaico", helper: "Prioridades", description: "Explora los indicadores principales en un tablero de tarjetas.", Component: MosaicView },
  { mode: "timeline", label: "Cronología", helper: "Eventos", description: "Recorre las transferencias recientes y consulta sus resultados.", Component: TimelineView },
  { mode: "radar", label: "Radar", helper: "Equilibrio", description: "Compara fortalezas y brechas en distintas dimensiones de operación.", Component: RadarView },
  { mode: "traffic", label: "Semáforo", helper: "Decisión", description: "Separa lo que está operando de lo que conviene vigilar o atender.", Component: TrafficView }
] as const;

// El mismo indicador conserva su definición al cambiar de presentación.
const metricGuidance = [
  { labels: ["Salud operativa", "Salud operativa actual", "Índice operativo", "Salud"], description: "La salud parte de 100 y descuenta las incidencias del corte. La actividad normal de transferencia puede continuar con una puntuación degradada.", nextStep: "Abre Diagnóstico para ver la causa de cada deducción y cómo recuperarla." },
  { labels: ["Éxito del día", "Éxito hoy", "Éxito"], description: "Porcentaje de transferencias completadas entre los cierres del período. Sin cierres, todavía no se puede interpretar la tasa.", nextStep: "Consulta Completadas y Fallidas antes de interpretar un porcentaje calculado con pocos archivos." },
  { labels: ["En transmisión", "Transmitiendo"], description: "Sesiones que están enviando datos en el corte actual. Todavía están abiertas y su resultado está por confirmarse.", nextStep: "Consulta Mantenimiento para dar seguimiento a los pendientes." },
  { labels: ["En espera"], description: "Sesiones admitidas que todavía esperan transmitir sus primeros bytes.", nextStep: "Si la cola crece de forma sostenida, consulta el uso de CPU, memoria y disco." },
  { labels: ["Completadas", "Completadas hoy", "Completados"], description: "Transferencias que el servidor cerró correctamente durante el período diario medido.", nextStep: "En Actividad puedes buscar el archivo y consultar su recibo y traza." },
  { labels: ["Completadas · 1 h"], description: "Transferencias cerradas correctamente durante la última hora medida.", nextStep: "Compara con Fallidas de la misma ventana para entender el resultado reciente." },
  { labels: ["Cerradas hoy", "Resultado del día"], description: "Cierres completados y fallidos del período diario. Las cargas en curso y los retiros administrativos se consultan aparte.", nextStep: "En Actividad puedes filtrar cada estado para investigar un archivo concreto." },
  { labels: ["Fallas hoy", "Fallas del día", "Fallidas", "Fallidas hoy"], description: "Cierres con falla técnica, falla de catálogo o rechazo en el período diario medido. Los retiros y las expiraciones se consultan por separado.", nextStep: "Filtra los fallidos en Actividad y abre el estado para leer el error y copiar la traza." },
  { labels: ["Fallidas · 1 h"], description: "Cantidad de transferencias fallidas durante la última hora medida.", nextStep: "Revisa los errores recientes en Actividad antes de volver a enviar archivos." },
  { labels: ["Falla hoy"], description: "Porcentaje de cierres fallidos entre los cierres del período diario.", nextStep: "Revisa cuántos archivos cerraron y usa Actividad para identificar errores repetidos." },
  { labels: ["Falla última hora", "Fallas · 1 h", "Fallas recientes"], description: "Porcentaje de cierres fallidos entre los cierres de la última hora medida.", nextStep: "Abre Actividad para localizar el error y la traza de la transferencia." },
  { labels: ["Fallas de catálogo"], description: "El servidor recibió los bytes y el registro final en catálogo falló.", nextStep: "Conserva la traza del archivo y solicita revisión del catálogo antes de duplicar el envío." },
  { labels: ["Pendientes críticos", "Pendientes sin actividad", "Sin actividad"], description: "Cargas que siguen abiertas y llevan más de 30 minutos sin avanzar. Cada una conserva su plazo de reanudación.", nextStep: "En Mantenimiento revisa la última actividad y el vencimiento antes de intervenir." },
  { labels: ["Pendientes persistidos"], description: "Sesiones pendientes o pausadas que siguen abiertas en base de datos. El tamaño indica cuántos bytes se conservan en copias parciales.", nextStep: "Consulta la cola de Mantenimiento para identificar cada archivo y su plazo." },
  { labels: ["Plazo vencido", "Pendientes vencidos"], description: "Cargas parciales abiertas cuya fecha individual de reanudación ya pasó. Las sesiones activas se protegen durante la limpieza.", nextStep: "Abre Mantenimiento para revisar el archivo antes de ejecutar una limpieza." },
  { labels: ["Vencen en 24 horas"], description: "Cargas parciales cuyo plazo de reanudación termina durante las próximas 24 horas.", nextStep: "Contacta a los emisores para que completen la carga mientras la copia sigue disponible." },
  { labels: ["Mayor espera pendiente"], description: "Tiempo transcurrido desde la última actividad de la carga abierta más antigua.", nextStep: "Busca la carga en Mantenimiento y revisa su plazo de reanudación." },
  { labels: ["Expiradas del día"], description: "Cargas que cerraron por vencimiento durante el período diario medido.", nextStep: "Si el archivo sigue siendo necesario, pide al emisor que inicie un nuevo envío." },
  { labels: ["Retiros manuales del día"], description: "Cargas pendientes retiradas por decisión administrativa en el período diario medido.", nextStep: "Consulta la bitácora para conocer el motivo del retiro." },
  { labels: ["Reanudables", "Custodia activa"], description: "Cargas interrumpidas con una copia parcial vigente. El cliente puede continuar desde el último byte confirmado.", nextStep: "Revisa el plazo de reanudación del archivo en Mantenimiento." },
  { labels: ["Reanudadas", "Reanudadas hoy", "Cargas reanudadas"], description: "Cargas que continuaron a partir de una copia parcial durante el período medido.", nextStep: "Revisa los bytes recuperados para conocer el volumen que el cliente evitó volver a enviar." },
  { labels: ["Recibos firmados", "Recibos emitidos", "Recibos hoy", "Recibos"], description: "Cantidad de recibos digitales del período. Aportan evidencia firmada y encadenada de las transferencias.", nextStep: "Consulta el recibo en Actividad o revisa la cobertura para identificar faltantes." },
  { labels: ["Cobertura de recibos"], description: "Porcentaje de transferencias completadas del día que tienen un recibo asociado.", nextStep: "Si hay completadas sin recibo, conserva su traza y solicita revisar la emisión de evidencia." },
  { labels: ["Verificación aplicada", "Cobertura de verificación", "Verificación"], description: "Porcentaje de archivos completados que fueron verificados entre los que debían pasar el control por política.", nextStep: "Revisa las transferencias con verificación requerida y consulta su resultado en Actividad." },
  { labels: ["Verificación omitida"], description: "Archivos que omitieron la verificación profunda por una política explícita de extensión.", nextStep: "En Políticas de archivos puedes consultar la regla de cada extensión y su historial." },
  { labels: ["Volumen confirmado", "Transferido hoy", "Volumen del día", "Volumen hoy"], description: "Suma de bytes pertenecientes a transferencias completadas durante el período diario medido.", nextStep: "Consulta el tamaño P95 para dimensionar la capacidad que necesitan las cargas más grandes." },
  { labels: ["Volumen última hora"], description: "Bytes de transferencias completadas y confirmadas en la última hora medida.", nextStep: "Compara este volumen con el promedio en Mbps para detectar variaciones de demanda." },
  { labels: ["Throughput", "Throughput promedio"], description: "Velocidad efectiva promedio de los bytes confirmados en la última hora, expresada en Mbps.", nextStep: "Si baja durante varios cortes con cargas en curso, revisa la cola y la presión de recursos." },
  { labels: ["Continuidad recuperada", "Ahorro por reanudación"], description: "Proporción del volumen completado hoy que el servidor conservó y reutilizó al reanudar cargas.", nextStep: "Consulta Bytes recuperados para ver el volumen que se evitó volver a enviar." },
  { labels: ["Recuperación", "Bytes recuperados"], description: "Volumen de bytes reutilizados al reanudar una carga parcial vigente durante el período diario medido.", nextStep: "Compara con el volumen confirmado para medir el efecto de la reanudación." },
  { labels: ["Actividad institucional", "RFC activos"], description: "Número de RFC distintos que iniciaron actividad de transferencia durante el período diario medido.", nextStep: "Busca un RFC en Actividad para consultar sus registros recientes." },
  { labels: ["Duración promedio"], description: "Tiempo medio de las cargas completadas del período diario. Las transferencias muy largas pueden elevar el promedio.", nextStep: "Compara con la mediana y P95 para entender la dispersión." },
  { labels: ["Duración mediana"], description: "La mitad de las cargas completadas del período terminó en este tiempo o menos.", nextStep: "Compárala con P95 para detectar cargas que tardaron mucho más de lo habitual." },
  { labels: ["Duración P95"], description: "El 95 % de las cargas completadas del período terminó en este tiempo o menos.", nextStep: "Una diferencia persistente respecto a la mediana indica que conviene revisar las cargas más lentas." },
  { labels: ["Tamaño promedio"], description: "Tamaño medio de los archivos completados en el período diario.", nextStep: "Consulta el tamaño P95 para evaluar el efecto de los archivos más grandes." },
  { labels: ["Tamaño P95"], description: "El 95 % de los archivos completados del período tiene este tamaño o uno menor.", nextStep: "Úsalo junto con el volumen confirmado para planear capacidad de transferencia y almacenamiento." },
  { labels: ["Última carga OK"], description: "Fecha y hora del último cierre correcto en los datos del corte.", nextStep: "En Actividad, filtra los completados para localizar la transferencia correspondiente." },
  { labels: ["Última falla"], description: "Fecha y hora del último cierre fallido que informa el servidor.", nextStep: "En Actividad, filtra los fallidos y abre su estado para leer el error." },
  { labels: ["Trazas mostradas"], description: "Cantidad de transferencias recientes que aparecen en la cronología.", nextStep: "Usa Actividad para buscar por archivo, RFC, traza o recibo." },
  { labels: ["CPU"], description: "Porcentaje aproximado de capacidad de procesamiento utilizada en el servidor.", nextStep: "Vigila niveles sostenidos a partir de 75 %. Una lectura persistente de 90 % o más requiere atención." },
  { labels: ["Memoria"], description: "Porcentaje de memoria física que utiliza el servidor. Una presión sostenida puede ralentizar transferencias.", nextStep: "Vigila niveles sostenidos a partir de 75 % y revisa procesos consumidores si se mantiene en 90 % o más." },
  { labels: ["Disco"], description: "Ocupación del volumen supervisado. Sin medición indica que el servidor todavía no pudo consultar la capacidad.", nextStep: "Abre Diagnóstico para revisar la capacidad y el acceso al almacenamiento por separado." },
  { labels: ["Presión principal"], description: "Recurso con la mayor utilización entre los que tienen medición disponible.", nextStep: "En Barras compara el uso de CPU, memoria y disco antes de intervenir." },
  { labels: ["Holgura", "Holgura CPU/RAM"], description: "Porcentaje disponible del recurso más utilizado entre los que tienen medición.", nextStep: "En Barras puedes identificar qué recurso limita la capacidad." },
  { labels: ["Estabilidad"], description: "100 menos el porcentaje de fallas de la última hora. Una puntuación alta indica menor proporción de fallas en esa ventana.", nextStep: "Revisa los cierres de la última hora para confirmar cuánta actividad respalda la lectura." },
  { labels: ["Continuidad"], description: "Proporción de cargas reanudadas del día frente al total de reanudadas y parciales vigentes. El radar muestra 100 cuando no hay actividad de continuidad.", nextStep: "Revisa parciales vigentes en Mantenimiento para identificar oportunidades de reanudar." },
  { labels: ["Integridad"], description: "Proporción de cargas completadas del día que no registraron omisión de verificación. El radar muestra 100 cuando todavía no hubo cierres completados.", nextStep: "Consulta Verificación aplicada para ver la cobertura de los archivos que sí debían pasar el control." },
  { labels: ["Base de datos"], description: "Comprueba la conexión a la base operativa que sostiene las sesiones, el catálogo, los recibos y la auditoría.", nextStep: "Si aparece degradada, consulta Diagnóstico y reporta la dependencia afectada." },
  { labels: ["Almacenamiento"], description: "Estado de acceso al destino de los archivos. Afecta la escritura, reanudación y descarga.", nextStep: "En Diagnóstico consulta el detalle de la ruta y la prueba de acceso." },
  { labels: ["Bus interno"], description: "Canal interno que coordina tareas y eventos del servidor. Una degradación puede retrasar pasos posteriores.", nextStep: "Consulta Diagnóstico para identificar si la falla afecta a otras dependencias." },
  { labels: ["Alertas", "Alertamiento", "Alertas no entregadas"], description: "Estado del canal de notificación. Los avisos con intentos agotados quedan disponibles para diagnóstico.", nextStep: "En Diagnóstico → Entrega de alertas revisa el canal, la causa y el identificador del evento." }
];

type MetricHelpProps = Omit<ComponentProps<typeof HelpTrigger>, "title" | "description"> & {
  label: string;
  description?: ComponentProps<typeof HelpTrigger>["description"];
};

function MetricHelp({ label, description, nextStep, ...triggerProps }: MetricHelpProps) {
  const guide = metricGuidance.find(entry => entry.labels.includes(label));
  return <HelpTrigger {...triggerProps} title={label} description={description ?? guide?.description ?? "Valor informado en el último corte del Dashboard."} nextStep={nextStep ?? guide?.nextStep} />;
}

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
  const [section, setSection] = useState<DashboardSection>("overview");
  const [revision, setRevision] = useState(0);
  const [maintenanceRequest, setMaintenanceRequest] = useState<MaintenanceRequest | null>(null);

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
  }, [authorizedFetch, revision]);

  const chooseView = (next: ViewMode) => {
    setView(next);
    try { window.localStorage.setItem(VIEW_PREFERENCE_KEY, next); } catch { /* La vista sigue disponible para esta sesión. */ }
  };

  const openMaintenance = (kind: MaintenanceRequest["kind"]) => {
    setSection("maintenance");
    setMaintenanceRequest(previous => ({ kind, nonce: (previous?.nonce ?? 0) + 1 }));
  };

  if (loading && !summary && !error) return <DashboardSkeleton />;

  if (error && !summary) {
    return <EmptyState title="No fue posible abrir el centro de control" detail={error} tone="error" />;
  }

  if (!summary) {
    return <EmptyState title="Sin información disponible" detail="El servidor todavía no reporta actividad operativa." />;
  }

  const SelectedView = dashboardViews.find(option => option.mode === view)?.Component ?? PulseView;
  return (
    <div className="dashboard-console space-y-6">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <p className="dashboard-eyebrow">Operación de transferencias</p>
          <h2 className="mt-2 text-3xl font-semibold tracking-tight text-white sm:text-4xl">Dashboard general</h2>
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2 text-xs text-slate-400">
          {error ? <span className="font-semibold text-amber-200">Actualización interrumpida</span> : <LiveBadge healthy={summary.server.isHealthy} />}
          <time dateTime={summary.timestampUtc}>Corte {formatDateTime(summary.timestampUtc)}</time>
        </div>
      </header>

      {error && (
        <div role="status" className="rounded-xl border border-amber-500/30 bg-[#211b11] px-4 py-3 text-sm text-amber-100">
          Los últimos datos válidos siguen visibles. La actualización reportó: {error}
        </div>
      )}

      <nav className="dashboard-section-nav" aria-label="Secciones del Dashboard">
        {dashboardSections.map(item => (
          <button key={item.id} id={`dashboard-nav-${item.id}`} type="button" aria-pressed={section === item.id}
            aria-controls={`dashboard-${item.id}`} onClick={() => setSection(item.id)}>
            {item.label}
            {item.id === "maintenance" && (summary.kpis.expiredPendingTransfers ?? 0) > 0 && (
              <span className="dashboard-nav-count" aria-label="vencidas">{summary.kpis.expiredPendingTransfers}</span>
            )}
          </button>
        ))}
      </nav>

      <section id="dashboard-overview" aria-label="Panorama" hidden={section !== "overview"}>
        {section === "overview" && <div className="space-y-7">
          <OperationalOverview summary={summary} onNavigate={next => {
            setSection(next);
            document.getElementById(`dashboard-nav-${next}`)?.focus();
          }} />
          <section aria-labelledby="dashboard-views-title" className="space-y-4">
            <div>
              <p className="dashboard-eyebrow">Exploración visual</p>
              <h3 id="dashboard-views-title" className="mt-1 text-xl font-semibold text-white">Vistas del Dashboard</h3>
              <p className="mt-1 text-sm text-slate-400">Elige cómo leer el mismo corte de información.</p>
            </div>
            <ViewSwitcher selected={view} onChange={chooseView} />
            <SelectedView summary={summary} />
          </section>
          <KpiCommandDeck summary={summary} />
        </div>}
      </section>

      <section id="dashboard-health" aria-label="Diagnóstico" hidden={section !== "health"}>
        <OperationalHealthGuide summary={summary} onChanged={() => setRevision(value => value + 1)} onMaintenanceAction={openMaintenance} />
        <AlertDeliveryDiagnostics active={section === "health"} />
      </section>

      <section id="dashboard-maintenance" aria-label="Mantenimiento" hidden={section !== "maintenance"}>
        <TransferMaintenance statisticsStartedUtc={summary.statisticsStartedUtc} onChanged={() => setRevision(value => value + 1)} requestAction={maintenanceRequest} />
      </section>

      <section id="dashboard-activity" aria-label="Actividad" hidden={section !== "activity"}>
        <RecentTransfers transfers={summary.recentTransfers} />
      </section>

      <footer className="flex flex-col gap-2 border-t border-white/[0.06] pt-4 text-xs text-slate-500 sm:flex-row sm:items-center sm:justify-between">
        <span>Resumen cada 2 s · Cola de pendientes cada 15 s · Fechas en hora local</span>
        <span>Transfer ASE · Centro de control</span>
      </footer>
    </div>
  );
}

function OperationalOverview({ summary, onNavigate }: { summary: SummaryResponse; onNavigate: (section: DashboardSection) => void }) {
  const { server, transfers, kpis } = summary;
  const score = clamp(kpis.operationalHealthScore, 0, 100);
  const severity = operationalSeverity(score, server.isHealthy);
  const expired = kpis.expiredPendingTransfers ?? 0;
  const signals = [
    { label: "En transmisión", value: transfers.activeTransfers, detail: `${transfers.waitingTransfers} en espera`, tone: "sky", section: "maintenance" },
    { label: "Completadas hoy", value: transfers.completedToday, detail: formatBytes(transfers.totalBytesTransferredToday), tone: "emerald", section: "activity" },
    { label: "Fallas hoy", value: transfers.failedToday, detail: `${kpis.catalogFailuresToday} de catálogo`, tone: "rose", section: "activity" },
    { label: "Pendientes vencidos", value: expired, detail: `${kpis.pendingTransfers ?? 0} pendientes registrados`, tone: "amber", section: "maintenance" }
  ] as const;

  return (
    <section aria-label="Resumen de operación" className="dashboard-overview">
      <div className="dashboard-health-hero" data-severity={severity}>
        <div className="relative min-w-0">
          <div className="flex items-center gap-2"><p className="dashboard-eyebrow">Salud operativa</p><MetricHelp label="Salud operativa" /></div>
          <p className="mt-1 text-5xl font-semibold tracking-[-0.065em] tabular-nums text-white">{score.toFixed(1)} <span className="text-2xl font-medium text-slate-400">%</span></p>
          <p className="dashboard-health-caption">{severity === "good" ? "Operación estable" : severity === "warning" ? "Requiere seguimiento" : "Requiere atención"}</p>
          <button type="button" className="dashboard-hero-link" onClick={() => onNavigate("health")}>
            Ver diagnóstico <span aria-hidden="true">↗</span>
          </button>
        </div>
        <svg className="dashboard-hero-gauge" viewBox="0 0 120 120" aria-hidden="true">
          <circle cx="60" cy="60" r="55" fill="none" stroke="currentColor" opacity=".12" strokeDasharray="1 4" />
          <circle cx="60" cy="60" r="44" fill="none" stroke="currentColor" opacity=".14" strokeWidth="8" />
          <circle cx="60" cy="60" r="44" fill="none" stroke="currentColor" strokeWidth="8" strokeLinecap="round" pathLength="100" strokeDasharray={`${score} 100`} transform="rotate(-90 60 60)" visibility={score === 0 ? "hidden" : undefined} />
          <path d="M35 61h13l8-13 9 25 6-12h14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </div>
      <div className="dashboard-signal-grid">
        {signals.map(signal => (
          <div className="relative min-w-0" key={signal.label}>
            <button type="button" className="dashboard-signal h-full w-full" data-tone={signal.value ? signal.tone : "neutral"}
              onClick={() => onNavigate(signal.section)}>
              <span className="dashboard-signal-label">{signal.label}</span>
              <span className="dashboard-signal-value">{signal.value.toLocaleString("es-MX")}</span>
              <span className="dashboard-signal-detail pr-7">{signal.detail}</span>
              <span className="dashboard-signal-arrow" aria-hidden="true">↗</span>
            </button>
            <MetricHelp label={signal.label} className="absolute bottom-3 right-3" />
          </div>
        ))}
      </div>
    </section>
  );
}

function OperationalHealthGuide({ summary, onChanged, onMaintenanceAction }: { summary: SummaryResponse; onChanged: () => void; onMaintenanceAction: (kind: "expire" | "review") => void }) {
  const { session } = useAuth();
  const canManage = session?.roles.some(role => role.toLowerCase() === "admin") ?? false;
  const breakdown = summary.healthBreakdown ?? [];
  const totalPenalty = breakdown.reduce((total, item) => total + item.penalty, 0);
  return (
    <Panel>
      <PanelHeading eyebrow="Diagnóstico y recuperación" title="Cómo leer y recuperar la salud operativa" description="El desglose proviene del servidor y muestra la deducción real de cada componente, junto con la acción que puede recuperarlo." />
      <div className="mt-5 grid gap-5 lg:grid-cols-[17rem_1fr]">
        <div className="rounded-2xl border border-amber-300/20 bg-[#17150e] p-5">
          <div className="flex items-center gap-2"><p className="text-xs text-amber-100">Salud operativa actual</p><MetricHelp label="Salud operativa actual" /></div>
          <p className="mt-2 text-4xl font-semibold tabular-nums text-white">{formatPercent(summary.kpis.operationalHealthScore)}</p>
          {breakdown.length > 0 && <p className="mt-3 text-xs text-amber-200">100 − {totalPenalty.toFixed(1)} puntos = {summary.kpis.operationalHealthScore.toFixed(1)} puntos</p>}
          <p className="mt-4 text-xs leading-5 text-slate-300">Es un termómetro de operación, no disponibilidad histórica ni SLA. Las transferencias activas normales no reducen la puntuación. Una sesión sin actividad requiere revisión; su plazo real de reanudación se muestra aparte.</p>
          <p className="mt-3 text-xs leading-5 text-slate-400">Reiniciar estadísticas no repara dependencias ni borra pendientes. La salud utiliza el estado real y las fallas recientes para conservar los focos de atención.</p>
        </div>
        <div className="space-y-3">
          {breakdown.map(item => <div key={item.key} className={`rounded-xl border p-4 ${item.penalty > 0 ? "border-amber-300/25 bg-[#1b1710]" : "border-emerald-400/15 bg-[#101a17]"}`}>
            <div className="flex justify-between gap-4">
              <div className="flex items-center gap-2"><h4 className="text-sm font-semibold text-white">{item.label}</h4><HelpTrigger title={item.label} description={item.detail} nextStep={item.remediation} /></div>
              <span className={`whitespace-nowrap text-xs font-semibold ${item.penalty > 0 ? "text-amber-200" : "text-emerald-200"}`}>−{item.penalty.toFixed(1)} / {item.maxPenalty} puntos</span>
            </div>
            <p className="mt-2 text-xs leading-5 text-slate-300">{item.detail}</p>
            <p className="mt-2 text-xs leading-5 text-slate-400"><span className="font-semibold text-slate-200">Cómo sanearlo: </span>{item.remediation}</p>
            {(item.key === "expired" || item.key === "idle") && item.penalty > 0 && <div className="mt-3 flex flex-wrap gap-2">
              {item.key === "expired" && <button type="button" disabled={!canManage} onClick={() => onMaintenanceAction("expire")} className="rounded-lg border border-amber-300/30 bg-amber-300/10 px-3 py-2 text-xs font-semibold text-amber-100 disabled:opacity-40">Limpiar vencidos</button>}
              <button type="button" onClick={() => onMaintenanceAction("review")} className="rounded-lg border border-white/20 px-3 py-2 text-xs font-semibold text-slate-200">Revisar y conciliar pendientes protegidos</button>
            </div>}
          </div>)}
          {breakdown.length === 0 && <p className="rounded-xl border border-white/10 p-4 text-xs text-slate-400">El servidor todavía no proporciona el desglose de puntuación. Actualice el servicio para obtener deducciones y acciones precisas.</p>}
          <StorageDiagnostics server={summary.server} onChanged={onChanged} />
          <HelpTrigger title="Guía de intervención" clickOnly
            className="w-full rounded-xl border border-white/15 p-4 text-left text-xs font-semibold text-amber-200"
            description={<HelpSteps steps={[
              { title: "Revisa las dependencias degradadas", text: "Verifica qué dependencia aparece degradada en el desglose. En Almacenamiento revisa la prueba de acceso. En Entrega de alertas consulta el canal, el error y el evento." },
              { title: "Investiga las fallas recientes", text: "Abre Actividad, filtra los fallidos y selecciona el estado del archivo para consultar su error y su traza." },
              { title: "Confirma los plazos de los pendientes", text: "En Mantenimiento, revisa la última actividad y el vencimiento. Contacta al emisor para que reanude los envíos que siguen vigentes." },
              { title: "Comprueba si la presión de recursos persiste", text: "Compara varios cortes en Barras. Una presión sostenida de CPU, memoria o disco requiere revisar el recurso afectado con el equipo de soporte." }
            ]} />}>
            Guía de intervención · Qué revisar primero
          </HelpTrigger>
        </div>
      </div>
    </Panel>
  );
}

function ViewSwitcher({ selected, onChange }: { selected: ViewMode; onChange: (mode: ViewMode) => void }) {
  const current = dashboardViews.find(option => option.mode === selected);
  return (
    <div className="dashboard-view-picker">
      <div className="dashboard-view-grid" role="group" aria-label="Vista del Dashboard">
        {dashboardViews.map((option) => (
          <button
            key={option.mode}
            type="button"
            aria-pressed={selected === option.mode}
            onClick={() => onChange(option.mode)}
            className="dashboard-view-option"
          >
            <ViewGlyph mode={option.mode} />
            <span className="min-w-0">
              <span className="block text-xs font-semibold">{option.label}</span>
              <span className="dashboard-view-helper">{option.helper}</span>
            </span>
          </button>
        ))}
      </div>
      {current && <div className="dashboard-view-description">
        <strong>{current.label}</strong><span aria-live="polite">{current.description}</span>
        <HelpTrigger title={`Vista ${current.label}`} description={current.description}
          nextStep="Activa la ayuda de un indicador para entender su cifra. Abre Indicadores detallados para consultar los indicadores comunes a todas las vistas." />
      </div>}
    </div>
  );
}

function PulseView({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  const recentCounts = countRecentStatuses(summary.recentTransfers);
  const recentFailed = recentCounts.failed + recentCounts.catalogFailed;
  const recentIncomplete = recentCounts.transmitting + recentCounts.pending + recentCounts.paused;

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
            <MiniMetric label="Sin actividad" value={String(kpis.stalePendingTransfers)} danger={kpis.stalePendingTransfers > 0} />
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
            <ServiceTile label="Alertamiento" ok={!server.alertingEnabled || server.alertingOk} detail={server.alertingEnabled ? "Habilitado" : "Modo local"} description={server.alertingEnabled ? undefined : "El alertamiento externo está deshabilitado. Los eventos permanecen disponibles localmente para diagnóstico."} />
          </div>
          <div className="mt-5 grid gap-4 sm:grid-cols-3">
            <ResourceDial label="CPU" value={server.cpuUsagePercent} />
            <ResourceDial label="Memoria" value={server.memoryUsagePercent} />
            <ResourceDial label="Disco" value={server.diskUsagePercent} available={server.diskCapacityAvailable !== false} />
          </div>
        </Panel>

        <Panel>
          <PanelHeading eyebrow="Últimos 20 registros" title="Huella de actividad" />
          <div className="mt-5">
            <StatusLegend counts={recentCounts} />
          </div>
          <div className="mt-6 flex min-h-20 flex-wrap content-start gap-2" aria-label={`${recentCounts.completed} completados, ${recentIncomplete} incompletos y ${recentFailed} fallidos en la actividad reciente`}>
            {summary.recentTransfers.length === 0 && <p className="text-sm text-slate-500">Aún no hay transferencias recientes.</p>}
            {summary.recentTransfers.map((transfer) => (
              <ActivityStatusChip key={transfer.uploadId || `${transfer.traceId ?? transfer.fileName}-${transfer.createdUtc}`} transfer={transfer} />
            ))}
          </div>
          <div className="mt-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
            <MiniMetric label="Completados" value={String(recentCounts.completed)} description="Transferencias completadas dentro de los registros recientes de esta huella." />
            <MiniMetric label="Transmitiendo" value={String(recentCounts.transmitting)} description="Transferencias en curso dentro de los registros recientes de esta huella." />
            <MiniMetric label="Pendientes" value={String(recentCounts.pending + recentCounts.paused)} danger={recentIncomplete > 0} description="Suma de pendientes y pausadas dentro de los registros recientes de esta huella." nextStep="En Mantenimiento puedes consultar el estado de la cola completa." />
            <MiniMetric label="Con falla" value={String(recentFailed)} danger={recentFailed > 0} description="Suma de fallidas y fallas de catálogo dentro de los registros recientes de esta huella." nextStep="Abre el bloque de la transferencia para consultar su error y su traza." />
          </div>
          <p className="mt-4 text-xs leading-5 text-slate-500">Cada bloque representa una transferencia. Pase el cursor o use Tab para consultar archivo, RFC, tiempos, verificación y detalle del error.</p>
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
        <MetricBar label="Disco" value={server.diskCapacityAvailable === false ? 0 : server.diskUsagePercent} max={100} display={diskUsageDisplay(server)} tone={server.diskCapacityAvailable === false ? "slate" : usageTone(server.diskUsagePercent)} />
      </ChartPanel>

      <ChartPanel eyebrow="Resultados" title="Completadas contra fallidas" footer={`Éxito global del día: ${formatPercent(kpis.successRateTodayPercent)}`}>
        <MetricBar label="Completadas hoy" value={transfers.completedToday} max={resultMax} display={String(transfers.completedToday)} tone="green" />
        <MetricBar label="Fallidas hoy" value={transfers.failedToday} max={resultMax} display={String(transfers.failedToday)} tone="red" />
        <MetricBar label="Completadas · 1 h" value={transfers.completedLastHour} max={resultMax} display={String(transfers.completedLastHour)} tone="green" />
        <MetricBar label="Fallidas · 1 h" value={transfers.failedLastHour} max={resultMax} display={String(transfers.failedLastHour)} tone="red" />
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
          <MetricCard label="Falla hoy" value={formatPercent(kpis.failureRateTodayPercent)} helper={`${transfers.failedToday} cierres no exitosos`} danger={kpis.failureRateTodayPercent > 5} />
          <MetricCard label="Falla última hora" value={formatPercent(kpis.failureRateLastHourPercent)} helper="Completadas contra fallidas" danger={kpis.failureRateLastHourPercent > 5} />
          <MetricCard label="Fallas de catálogo" value={String(kpis.catalogFailuresToday)} helper="El archivo llegó, pero no pudo registrarse" danger={kpis.catalogFailuresToday > 0} />
          <MetricCard label="RFC activos" value={String(kpis.distinctRfcsToday)} helper="Con actividad durante el día" />
          <MetricCard label="Pendientes sin actividad" value={String(kpis.stalePendingTransfers)} helper="Más de 30 minutos sin avance" danger={kpis.stalePendingTransfers > 0} />
          <MetricCard label="Duración promedio" value={formatSeconds(kpis.averageUploadDurationTodaySeconds)} helper="Cargas completadas hoy" />
          <MetricCard label="Duración mediana" value={formatSeconds(kpis.medianUploadDurationTodaySeconds)} helper="La mitad terminó antes de este tiempo" />
          <MetricCard label="Duración P95" value={formatSeconds(kpis.p95UploadDurationTodaySeconds)} helper="95 % concluye antes de este tiempo" />
          <MetricCard label="Tamaño promedio" value={formatBytes(kpis.averageFileSizeTodayBytes)} helper="Archivos completados" />
          <MetricCard label="Tamaño P95" value={formatBytes(kpis.p95FileSizeTodayBytes)} helper="95 % de los archivos no supera este tamaño" />
          <MetricCard label="Cobertura de recibos" value={formatPercent(kpis.receiptCoverageTodayPercent)} helper={`${kpis.completedWithoutReceiptToday} completadas sin recibo asociado`} danger={kpis.completedWithoutReceiptToday > 0} />
          <MetricCard label="Cobertura de verificación" value={formatPercent(kpis.verificationCoverageTodayPercent)} helper="Sobre cargas que sí requieren verificación" danger={kpis.verificationCoverageTodayPercent < 100} />
          <MetricCard label="Ahorro por reanudación" value={formatPercent(kpis.resumeReuseTodayPercent)} helper="Bytes reutilizados frente al volumen completado" />
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

function KpiCommandDeck({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  const groups = {
    performance: "Rendimiento y capacidad",
    results: "Resultados y evidencia",
    continuity: "Continuidad y pendientes"
  } as const;
  const cards: Array<{ group: typeof groups[keyof typeof groups]; label: string; value: string; context: string; tone: KpiTone }> = [
    {
      group: groups.performance,
      label: "Salud operativa",
      value: formatPercent(kpis.operationalHealthScore),
      context: server.isHealthy ? "Dependencias disponibles" : "Hay servicios degradados",
      tone: !server.isHealthy || kpis.operationalHealthScore < 70 ? "red" : kpis.operationalHealthScore < 90 ? "gold" : "green"
    },
    {
      group: groups.results,
      label: "Éxito del día",
      value: formatPercent(kpis.successRateTodayPercent),
      context: `${transfers.completedToday} completadas de ${kpis.totalTransfersToday}`,
      tone: kpis.totalTransfersToday === 0 ? "slate" : kpis.successRateTodayPercent >= 95 ? "green" : kpis.successRateTodayPercent >= 85 ? "gold" : "red"
    },
    {
      group: groups.performance,
      label: "En transmisión",
      value: String(transfers.activeTransfers),
      context: `${transfers.waitingTransfers} esperando turno`,
      tone: transfers.activeTransfers > 0 ? "blue" : "slate"
    },
    {
      group: groups.results,
      label: "Fallas del día",
      value: String(transfers.failedToday),
      context: `${kpis.catalogFailuresToday} de catálogo`,
      tone: transfers.failedToday > 0 ? "red" : "green"
    },
    {
      group: groups.continuity,
      label: "Pendientes críticos",
      value: String(kpis.stalePendingTransfers),
      context: `${transfers.pausedResumableTransfers} reanudables`,
      tone: kpis.stalePendingTransfers > 0 ? "red" : transfers.pausedResumableTransfers > 0 ? "violet" : "green"
    },
    {
      group: groups.results,
      label: "Cobertura de recibos",
      value: formatPercent(kpis.receiptCoverageTodayPercent),
      context: `${kpis.completedWithoutReceiptToday} sin recibo`,
      tone: kpis.completedWithoutReceiptToday > 0 ? "red" : "green"
    },
    {
      group: groups.results,
      label: "Verificación aplicada",
      value: formatPercent(kpis.verificationCoverageTodayPercent),
      context: `${transfers.verificationBypassedToday} omitidas por política`,
      tone: kpis.verificationCoverageTodayPercent < 100 ? "red" : transfers.verificationBypassedToday > 0 ? "gold" : "green"
    },
    {
      group: groups.performance,
      label: "Duración P95",
      value: formatSeconds(kpis.p95UploadDurationTodaySeconds),
      context: `Mediana ${formatSeconds(kpis.medianUploadDurationTodaySeconds)}`,
      tone: "blue"
    },
    {
      group: groups.performance,
      label: "Volumen confirmado",
      value: formatBytes(transfers.totalBytesTransferredToday),
      context: `P95 por archivo ${formatBytes(kpis.p95FileSizeTodayBytes)}`,
      tone: "blue"
    },
    {
      group: groups.performance,
      label: "Throughput",
      value: `${transfers.averageThroughputMbps.toFixed(2)} Mbps`,
      context: `${formatBytes(transfers.bytesTransferredLastHour)} en 1 h`,
      tone: "blue"
    },
    {
      group: groups.performance,
      label: "Continuidad recuperada",
      value: formatPercent(kpis.resumeReuseTodayPercent),
      context: `${formatBytes(transfers.resumedBytesToday)} reutilizados`,
      tone: transfers.resumedBytesToday > 0 ? "violet" : "slate"
    },
    {
      group: groups.results,
      label: "Actividad institucional",
      value: `${kpis.distinctRfcsToday} RFC`,
      context: `Promedio ${formatBytes(kpis.averageFileSizeTodayBytes)}`,
      tone: "gold"
    },
    {
      group: groups.continuity,
      label: "Pendientes persistidos",
      value: String(kpis.pendingTransfers ?? 0),
      context: `${formatBytes(kpis.pendingBytes ?? 0)} en copias parciales`,
      tone: (kpis.pendingTransfers ?? 0) > 0 ? "violet" : "green"
    },
    {
      group: groups.continuity,
      label: "Plazo vencido",
      value: String(kpis.expiredPendingTransfers ?? 0),
      context: "Pendientes por limpiar",
      tone: (kpis.expiredPendingTransfers ?? 0) > 0 ? "red" : "green"
    },
    {
      group: groups.continuity,
      label: "Vencen en 24 horas",
      value: String(kpis.expiringNext24Hours ?? 0),
      context: `${formatBytes(kpis.nearExpiryBytes ?? 0)} por reanudar`,
      tone: (kpis.expiringNext24Hours ?? 0) > 0 ? "gold" : "green"
    },
    {
      group: groups.continuity,
      label: "Mayor espera pendiente",
      value: `${(kpis.oldestPendingAgeHours ?? 0).toFixed(1)} h`,
      context: "Desde la última actividad",
      tone: (kpis.oldestPendingAgeHours ?? 0) >= 24 ? "red" : (kpis.oldestPendingAgeHours ?? 0) > 0.5 ? "gold" : "green"
    },
    {
      group: groups.continuity,
      label: "Expiradas del día",
      value: String(kpis.expiredToday ?? 0),
      context: "Cerradas por vencimiento",
      tone: (kpis.expiredToday ?? 0) > 0 ? "gold" : "green"
    },
    {
      group: groups.continuity,
      label: "Retiros manuales del día",
      value: String(kpis.administrativelyCancelledToday ?? 0),
      context: "Decisiones administrativas",
      tone: "slate"
    }
  ];

  return (
    <details className="dashboard-kpi-details">
      <summary>
        <span className="min-w-0">
          <span className="block text-lg font-semibold text-white">Indicadores detallados</span>
          <span className="mt-1 block text-xs text-slate-400">{cards.length} indicadores de rendimiento, evidencia y continuidad</span>
        </span>
        <span className="dashboard-disclosure-icon" aria-hidden="true">⌄</span>
      </summary>
      <div className="space-y-6 border-t border-white/10 p-4 sm:p-5">
        <p className="text-xs leading-5 text-slate-400">Contadores del día desde medianoche en {summary.statisticsTimeZone ?? "America/Mazatlan"}{summary.statisticsStartedUtc ? `, o desde el último reinicio si fue posterior (${formatDateTime(summary.statisticsStartedUtc)})` : ""}. Las ventanas de última hora se recortan al reinicio. Cada tarjeta explica cómo interpretar su cifra. Pasa el cursor, usa Tab o selecciona la tarjeta para fijar la ayuda.</p>
        {Object.values(groups).map(group => (
          <section key={group} aria-label={group}>
            <h4 className="mb-3 text-sm font-semibold text-slate-200">{group}</h4>
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-6">
              {cards.filter(card => card.group === group).map(card => <KpiInsightCard key={card.label} {...card} />)}
            </div>
          </section>
        ))}
      </div>
    </details>
  );
}

function KpiInsightCard(props: { label: string; value: string; context: string; tone: KpiTone }) {
  const styles = kpiToneClasses(props.tone);
  return (
    <MetricHelp label={props.label}
      ariaLabel={`Ayuda de ${props.label}, ${props.value}. ${props.context}`}
      className={`flex h-full w-full flex-col items-stretch rounded-2xl border p-4 text-left transition hover:-translate-y-0.5 ${styles.panel}`}
    >
      <span className="flex items-start justify-between gap-3">
        <span className="text-[10px] font-semibold uppercase tracking-[0.15em] text-slate-400">{props.label}</span>
        <span className={`grid h-5 w-5 shrink-0 place-items-center rounded-full border text-[10px] font-bold ${styles.icon}`} aria-hidden="true">i</span>
      </span>
      <span className={`mt-3 text-2xl font-semibold tracking-tight tabular-nums ${styles.value}`}>{props.value}</span>
      <span className="mt-1 min-h-8 text-[11px] leading-4 text-slate-400">{props.context}</span>
    </MetricHelp>
  );
}

function RingsView({ summary }: { summary: SummaryResponse }) {
  const { server, transfers, kpis } = summary;
  const capacity = resourceHeadroom(server);
  const receiptCoverage = kpis.receiptCoverageTodayPercent;
  const verificationCoverage = kpis.verificationCoverageTodayPercent;
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
        <RingMetric label={server.diskCapacityAvailable === false ? "Holgura CPU/RAM" : "Holgura"} value={capacity} display={formatPercent(capacity)} inverse />
        <RingMetric label="Recibos" helpLabel="Cobertura de recibos" value={receiptCoverage} display={formatPercent(receiptCoverage)} />
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
      <p className="mt-2 text-sm text-slate-400">Estado actual de la cola y resultados acumulados del día.</p>
      <ol className="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-6">
        {stages.map((stage, index) => (
          <li key={stage.label}><ProcessStage index={index + 1} {...stage} /></li>
        ))}
      </ol>
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
      <MosaicTile eyebrow="Disco" value={diskUsageDisplay(server)} detail={server.diskCapacityAvailable === false ? "Capacidad aún no disponible" : "Uso del servidor"} tone={server.diskCapacityAvailable === false ? "slate" : usageTone(server.diskUsagePercent)} />
      <MosaicTile eyebrow="Reanudables" value={String(transfers.pausedResumableTransfers)} detail="Parciales vigentes" tone="gold" />
      <MosaicTile eyebrow="Recibos" value={String(transfers.receiptsIssuedToday)} detail="Firmados hoy" tone="green" />
      <MosaicTile className="sm:col-span-2" eyebrow="Actividad institucional" value={`${kpis.distinctRfcsToday} RFC`} detail={`${formatBytes(kpis.averageFileSizeTodayBytes)} por archivo en promedio`} tone="slate" />
      <MosaicTile className="sm:col-span-2" eyebrow="Bytes recuperados" value={formatBytes(transfers.resumedBytesToday)} detail={`${transfers.resumedTransfersToday} transferencias reanudadas`} tone="blue" />
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
          {events.map((event) => (
            <article key={event.uploadId || `${event.traceId ?? event.fileName}-${event.createdUtc}`} className="relative grid grid-cols-[16px_1fr] gap-4 py-3">
              <span className={`relative z-10 mt-1 h-4 w-4 rounded-full border-4 border-[#11141a] ${statusDotClass(event.status)}`} />
              <div className="min-w-0 rounded-2xl border border-white/[0.06] bg-black/10 p-4">
                <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                  <p className="min-w-0 break-all text-sm font-semibold text-white">{event.fileName}</p>
                  <span className="shrink-0 text-[10px] tabular-nums text-slate-500">{formatDateTime(event.createdUtc)}</span>
                </div>
                <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-slate-500"><StatusBadge transfer={event} /><span>{formatBytes(event.bytesReceived)}</span><span>{event.verificationRequired ? (event.verificationPerformed ? "Verificado" : "Verificación pendiente") : "Verificación omitida"}</span></div>
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
              <div className="flex items-center justify-between text-xs">
                <span className="flex items-center gap-2 font-medium text-slate-300">{metric.label}
                  <MetricHelp label={metric.label} description={metric.label === "Éxito" && summary.kpis.totalTransfersToday === 0
                    ? "El radar usa 100 como valor de referencia cuando todavía no hay cierres. Con actividad, muestra el porcentaje de cierres completados."
                    : undefined} />
                </span>
                <span className="font-semibold tabular-nums text-white">{metric.value.toFixed(0)}</span>
              </div>
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
        <PanelHeading eyebrow="Decisión inmediata" title="Semáforo operativo" />
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

function RingMetric({ label, helpLabel = label, value, display, inverse = false }: { label: string; helpLabel?: string; value: number; display: string; inverse?: boolean }) {
  const safe = clamp(value, 0, 100);
  const visualTone: Tone = (inverse ? safe < 15 : safe < 55) ? "red" : safe < 80 ? "gold" : "green";
  const color = visualTone === "green" ? "#34d399" : visualTone === "gold" ? "#f2b96b" : "#fb7185";
  return (
    <div className="text-center">
      <div className="mx-auto grid aspect-square w-full max-w-32 place-items-center rounded-full p-2" style={{ background: `conic-gradient(${color} ${safe}%, rgba(255,255,255,.06) 0)` }}><div className="grid h-full w-full place-items-center rounded-full bg-[#11141a]"><span className="px-2 text-center text-xs font-semibold text-white sm:text-sm">{display}</span></div></div>
      <div className="mt-3 flex items-center justify-center gap-2">
        <p className="text-xs font-semibold uppercase tracking-wider text-slate-400">{label}</p>
        <MetricHelp label={helpLabel} />
      </div>
    </div>
  );
}

function HeatCell({ label, value, detail, severity }: { label: string; value: string; detail: string; severity: Severity }) {
  const styles = severity === "good" ? "border-emerald-400/20 bg-emerald-400/[0.08] text-emerald-200" : severity === "warning" ? "border-amber-400/25 bg-amber-400/[0.09] text-amber-100" : "border-rose-400/30 bg-rose-400/[0.11] text-rose-100";
  return <div className={`min-h-32 rounded-2xl border p-4 ${styles}`}>
    <div className="flex items-start justify-between gap-2"><p className="text-[10px] font-semibold uppercase tracking-[0.16em] opacity-65">{label}</p><MetricHelp label={label} /></div>
    <p className="mt-3 text-2xl font-semibold text-white">{value}</p>
    <p className="mt-2 text-[11px] leading-4 opacity-65">{detail}</p>
  </div>;
}

function LegendDot({ tone, label }: { tone: Severity; label: string }) {
  const color = tone === "good" ? "bg-emerald-400" : tone === "warning" ? "bg-amber-400" : "bg-rose-400";
  return <span className="flex items-center gap-1.5"><span className={`h-2 w-2 rounded-full ${color}`} />{label}</span>;
}

function ProcessStage({ index, label, value, detail, tone }: { index: number; label: string; value: number; detail: string; tone: Tone }) {
  return <div className={`min-h-44 min-w-36 flex-1 rounded-2xl border p-4 ${tonePanel(tone)}`}>
    <div className="flex items-start justify-between gap-2"><span className="text-[10px] font-semibold tracking-widest opacity-50">{String(index).padStart(2, "0")}</span><MetricHelp label={label} /></div>
    <p className="mt-5 text-4xl font-semibold text-white">{value}</p>
    <p className="mt-2 text-sm font-semibold">{label}</p>
    <p className="mt-1 text-[11px] opacity-55">{detail}</p>
  </div>;
}

function MosaicTile({ eyebrow, value, detail, tone, className = "", hero = false }: { eyebrow: string; value: string; detail: string; tone: Tone; className?: string; hero?: boolean }) {
  return <article className={`relative overflow-hidden rounded-3xl border p-5 ${tonePanel(tone)} ${className}`}>
    <div className="pointer-events-none absolute -right-12 -top-12 h-32 w-32 rounded-full bg-current opacity-[0.04] blur-2xl" />
    <div className="relative flex items-start justify-between gap-2"><p className="text-[10px] font-semibold uppercase tracking-[0.18em] opacity-60">{eyebrow}</p><MetricHelp label={eyebrow} /></div>
    <p className={`relative mt-3 font-semibold tracking-tight text-white ${hero ? "text-5xl sm:text-6xl" : "text-3xl"}`}>{value}</p>
    <p className={`relative mt-2 text-current opacity-60 ${hero ? "max-w-sm text-sm leading-6" : "text-xs"}`}>{detail}</p>
  </article>;
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
  return <section className={`rounded-3xl border p-5 ${styles}`}>
    <div className="flex items-center gap-3">
      <span aria-hidden="true" className={`h-4 w-4 rounded-full ${light}`} />
      <div><h4 className="font-semibold text-white">{title}</h4><p className="text-[11px] text-slate-500">{subtitle}</p></div>
      <span className="ml-auto text-2xl font-semibold text-white">{items.length}</span>
    </div>
    <div className="mt-5 space-y-2">
      {items.length === 0 && <p className="rounded-xl border border-white/[0.06] p-4 text-xs text-slate-500">No hay señales en esta categoría.</p>}
      {items.map(item => <div key={item.label} className="rounded-xl border border-white/[0.06] bg-black/10 p-3">
        <div className="flex items-start justify-between gap-2"><p className="text-xs font-semibold text-slate-200">{item.label}</p><MetricHelp label={item.label} /></div>
        <p className="mt-1 text-[11px] leading-4 text-slate-500">{item.detail}</p>
      </div>)}
    </div>
  </section>;
}

function buildHeatSignals(summary: SummaryResponse): Array<{ label: string; value: string; detail: string; severity: Severity }> {
  const { server, transfers, kpis } = summary;
  return [
    { label: "Salud operativa", value: formatPercent(kpis.operationalHealthScore), detail: "Índice compuesto", severity: operationalSeverity(kpis.operationalHealthScore, server.isHealthy) },
    { label: "Éxito hoy", value: formatPercent(kpis.successRateTodayPercent), detail: `${kpis.totalTransfersToday} cerradas`, severity: kpis.totalTransfersToday === 0 ? "warning" : scoreSeverity(kpis.successRateTodayPercent) },
    { label: "Fallas · 1 h", value: formatPercent(kpis.failureRateLastHourPercent), detail: `${transfers.failedLastHour} transferencias`, severity: inverseSeverity(kpis.failureRateLastHourPercent, 5, 15) },
    { label: "Pendientes sin actividad", value: String(kpis.stalePendingTransfers), detail: "Más de 30 minutos sin avance", severity: countSeverity(kpis.stalePendingTransfers, 1, 5) },
    { label: "CPU", value: formatPercent(server.cpuUsagePercent), detail: "Utilización actual", severity: inverseSeverity(server.cpuUsagePercent, 75, 90) },
    { label: "Memoria", value: formatPercent(server.memoryUsagePercent), detail: "Utilización actual", severity: inverseSeverity(server.memoryUsagePercent, 75, 90) },
    { label: "Disco", value: diskUsageDisplay(server), detail: server.diskCapacityAvailable === false ? "Capacidad no medida; consulte diagnóstico" : "Utilización actual", severity: server.diskCapacityAvailable === false ? "warning" : inverseSeverity(server.diskUsagePercent, 75, 90) },
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
    { label: "Holgura", value: resourceHeadroom(server), detail: server.diskCapacityAvailable === false ? "Holgura de CPU/RAM; disco sin medición" : "Capacidad libre del recurso más presionado" },
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
    { label: "Disco", detail: server.diskCapacityAvailable === false ? "Sin medición de capacidad. Compruebe el diagnóstico de almacenamiento." : `${formatPercent(server.diskUsagePercent)} de utilización.`, severity: server.diskCapacityAvailable === false ? "warning" : inverseSeverity(server.diskUsagePercent, 75, 90) },
    { label: "Fallas recientes", detail: `${formatPercent(kpis.failureRateLastHourPercent)} durante la última hora.`, severity: inverseSeverity(kpis.failureRateLastHourPercent, 5, 15) },
    { label: "Pendientes sin actividad", detail: `${kpis.stalePendingTransfers} cargas llevan más de 30 minutos sin actividad.`, severity: countSeverity(kpis.stalePendingTransfers, 1, 5) },
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

function operationalSeverity(value: number, healthy: boolean): Severity {
  if (!healthy || value < 70) return "danger";
  return value < 90 ? "warning" : "good";
}

function inverseSeverity(value: number, warningAt: number, dangerAt: number): Severity {
  return value >= dangerAt ? "danger" : value >= warningAt ? "warning" : "good";
}

function countSeverity(value: number, warningAt: number, dangerAt: number): Severity {
  return value >= dangerAt ? "danger" : value >= warningAt ? "warning" : "good";
}

function largestResource(server: SummaryResponse["server"]): string {
  const values = [{ label: "CPU", value: server.cpuUsagePercent }, { label: "Memoria", value: server.memoryUsagePercent }, ...(server.diskCapacityAvailable === false ? [] : [{ label: "Disco", value: server.diskUsagePercent }])];
  const largest = values.sort((left, right) => right.value - left.value)[0];
  return `${largest.label} · ${formatPercent(largest.value)}`;
}

export function diskUsageDisplay(server: SummaryResponse["server"]): string {
  return server.diskCapacityAvailable === false ? "Sin medición" : formatPercent(server.diskUsagePercent);
}

function resourceHeadroom(server: SummaryResponse["server"]): number {
  return clamp(100 - Math.max(server.cpuUsagePercent, server.memoryUsagePercent, server.diskCapacityAvailable === false ? 0 : server.diskUsagePercent), 0, 100);
}

function ActivityStatusChip({ transfer }: { transfer: RecentTransferDto }) {
  const presentation = statusPresentation(statusKind(transfer.status));
  return <TransferHelp transfer={transfer}
    className={`h-5 w-5 cursor-help rounded-md border transition hover:scale-125 focus-visible:scale-125 ${presentation.dotClass}`}>
    <span className="sr-only">{presentation.label}</span>
  </TransferHelp>;
}

function TransferHelp({ transfer, className, children }: { transfer: RecentTransferDto; className: string; children: React.ReactNode }) {
  const kind = statusKind(transfer.status);
  const presentation = statusPresentation(kind);
  const verification = transfer.verificationRequired
    ? transfer.verificationPerformed ? "Verificación ejecutada" : "Verificación requerida pendiente"
    : "Verificación omitida por política";
  const nextSteps: Partial<Record<StatusKind, string>> = {
    completed: "Consulta el recibo para verificar la evidencia del cierre.",
    pending: "Revisa la última actividad y el plazo del archivo en Mantenimiento.",
    paused: "Consulta el plazo en Mantenimiento y pide al emisor que continúe el envío mientras siga vigente.",
    catalogFailed: "Copia la traza y solicita revisar el catálogo antes de reenviar el archivo.",
    failed: "Lee el error y conserva la traza para solicitar soporte.",
    unknown: "Conserva el estado técnico y la traza para solicitar soporte."
  };
  return (
    <HelpTrigger title={transfer.fileName} ariaLabel={`${presentation.label}, ${transfer.fileName}. Consultar detalle`}
      className={className} nextStep={nextSteps[kind]}
      description={<>
        <span className={`inline-flex rounded-full border px-2 py-1 text-[10px] font-semibold uppercase tracking-wide ${presentation.badgeClass}`}>{presentation.label}</span>
        <p className="mt-2">{presentation.description}</p>
        <dl className="mt-2 grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-xs text-slate-400">
          <dt>Estado técnico</dt><dd className="break-words text-right text-slate-200">{transfer.status}</dd>
          <dt>RFC</dt><dd className="break-words text-right text-slate-200">{transfer.rfc || "—"}</dd>
          <dt>Tamaño</dt><dd className="text-right text-slate-200">{formatBytes(transfer.bytesReceived)}</dd>
          <dt>Inicio</dt><dd className="text-right text-slate-200">{formatDateTime(transfer.createdUtc)}</dd>
          <dt>Fin</dt><dd className="text-right text-slate-200">{formatDateTime(transfer.completedUtc)}</dd>
          <dt>Control</dt><dd className="text-right text-slate-200">{verification}</dd>
          {transfer.traceId && <><dt>Traza</dt><dd className="break-all text-right text-slate-200">{transfer.traceId}</dd></>}
          {transfer.receiptId && <><dt>Recibo</dt><dd className="break-all text-right text-slate-200">{transfer.receiptId}</dd></>}
        </dl>
        {transfer.lastError && <p className="mt-2 break-words rounded-lg border border-rose-400/20 bg-rose-400/[0.07] p-2 text-rose-100">{transfer.lastError}</p>}
      </>}>
      {children}
    </HelpTrigger>
  );
}

function StatusLegend({ counts }: { counts: Record<StatusKind, number> }) {
  const visibleKinds: StatusKind[] = ["transmitting", "pending", "paused", "completed", "catalogFailed", "failed", "cancelled", "unknown"];
  return (
    <div className="flex flex-wrap gap-x-4 gap-y-2" aria-label="Leyenda de estados">
      {visibleKinds.map(kind => {
        const presentation = statusPresentation(kind);
        return (
          <HelpTrigger key={kind} title={presentation.label} description={presentation.description}
            ariaLabel={`${presentation.label}, ${counts[kind]} en la actividad reciente. Consultar significado`}
            className="inline-flex cursor-help items-center gap-1.5 text-[10px] text-slate-400">
            <span aria-hidden="true" className={`h-2.5 w-2.5 rounded-sm border ${presentation.dotClass}`} />
            <span>{presentation.label}</span>
            <span className="font-semibold tabular-nums text-slate-200">{counts[kind]}</span>
          </HelpTrigger>
        );
      })}
    </div>
  );
}

function RecentTransfers({ transfers }: { transfers: RecentTransferDto[] }) {
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<StatusKind | "all">("all");
  const term = query.trim().toLowerCase();
  const filtered = transfers.filter(transfer =>
    (status === "all" || statusKind(transfer.status) === status) &&
    (!term || [transfer.fileName, transfer.rfc, transfer.traceId, transfer.receiptId]
      .some(value => value?.toLowerCase().includes(term))));
  const statuses = Object.keys(countRecentStatuses(transfers)) as StatusKind[];

  return (
    <section className="dashboard-panel overflow-hidden rounded-2xl border" aria-labelledby="recent-transfers-title">
      <div className="flex flex-col gap-1 border-b border-white/[0.06] px-5 py-4 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <p className="dashboard-eyebrow">Trazabilidad</p>
          <div className="mt-1 flex items-center gap-2">
            <h3 id="recent-transfers-title" className="text-xl font-semibold">Transferencias recientes</h3>
            <HelpTrigger title="Cómo localizar una transferencia" description="Escribe parte del nombre, RFC, traza o recibo para buscar entre los registros recientes. Puedes combinar la búsqueda con un filtro de estado."
              nextStep="Selecciona el estado del archivo para fijar su detalle y copiar la traza. El recibo identifica la evidencia firmada de una transferencia completada." />
          </div>
        </div>
        <span className="text-xs text-slate-400" aria-live="polite">{filtered.length} de {transfers.length} registros recientes</span>
      </div>
      <div className="grid gap-3 border-b border-white/10 px-5 py-4 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <label className="grid min-w-0 gap-1.5 text-xs font-medium text-slate-300">
          Buscar transferencia
          <input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Archivo, RFC, traza o recibo"
            className="min-h-11 min-w-0 rounded-lg border border-white/15 bg-[#0e131b] px-3 text-sm text-white outline-none focus-visible:border-amber-300" />
        </label>
        <label className="grid min-w-0 gap-1.5 text-xs font-medium text-slate-300">
          Estado
          <select value={status} onChange={event => setStatus(event.target.value as StatusKind | "all")}
            className="min-h-11 min-w-0 rounded-lg border border-white/15 bg-[#0e131b] px-3 text-sm text-white outline-none focus-visible:border-amber-300">
            <option value="all">Todos los estados</option>
            {statuses.map(kind => <option key={kind} value={kind}>{statusPresentation(kind).label}</option>)}
          </select>
        </label>
      </div>
      <div className="overflow-x-auto focus-visible:outline-amber-300" tabIndex={0} role="region" aria-label="Tabla de transferencias recientes">
        <table className="w-full min-w-[1080px] text-left text-xs">
          <thead className="bg-black/15 text-[10px] uppercase tracking-wider text-slate-500">
            <tr><th className="px-5 py-3 font-semibold">Archivo</th><th className="py-3 font-semibold">RFC</th><th className="py-3 font-semibold">Estado</th><th className="py-3 font-semibold">Tamaño</th><th className="py-3 font-semibold">Política</th><th className="py-3 font-semibold">Verificación</th><th className="py-3 font-semibold">Traza</th><th className="py-3 pr-5 font-semibold">Recibo</th></tr>
          </thead>
          <tbody>
            {filtered.map((transfer) => (
              <tr key={transfer.uploadId || `${transfer.traceId ?? transfer.fileName}-${transfer.createdUtc}`} className="border-t border-white/[0.05] text-slate-300 transition hover:bg-white/[0.025]">
                <td className="max-w-72 break-words px-5 py-3.5 font-medium text-slate-100">{transfer.fileName}</td>
                <td className="max-w-36 break-words font-mono text-[10px] text-slate-400">{transfer.rfc || "—"}</td>
                <td><StatusBadge transfer={transfer} /></td>
                <td className="tabular-nums">{formatBytes(transfer.bytesReceived)}</td>
                <td>{transfer.policyExtension ?? "—"}{transfer.policyVersion ? ` · v${transfer.policyVersion}` : ""}</td>
                <td>{transfer.verificationRequired ? (transfer.verificationPerformed ? "Ejecutada" : "Pendiente") : "Omitida por política"}</td>
                <td className="max-w-40 break-all font-mono text-[10px] text-slate-500">{transfer.traceId ?? "—"}</td>
                <td className="max-w-40 break-all pr-5 font-mono text-[10px] text-slate-500">{transfer.receiptId ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {filtered.length === 0 && <p className="px-5 py-10 text-center text-sm text-slate-400">{transfers.length === 0 ? "No existen transferencias recientes." : "Ninguna transferencia reciente coincide con estos filtros."}</p>}
      </div>
    </section>
  );
}

function HealthRing({ score, healthy }: { score: number; healthy: boolean }) {
  const safeScore = clamp(score, 0, 100);
  const severity = operationalSeverity(safeScore, healthy);
  const color = severity === "danger" ? "#fb7185" : severity === "warning" ? "#f2b96b" : "#4ade80";
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
      <div className="flex items-center justify-between"><span className="text-[10px] font-semibold tracking-widest text-current opacity-55">{props.index}</span><span className="flex items-center gap-2">{props.active && <span className="ase-pulse h-2 w-2 rounded-full bg-sky-400 shadow-[0_0_16px_#38bdf8]" />}<MetricHelp label={props.label} /></span></div>
      <p className="mt-5 text-3xl font-semibold tabular-nums text-white">{props.value}</p><p className="mt-1 text-sm font-semibold">{props.label}</p><p className="mt-1 text-[11px] text-current opacity-55">{props.detail}</p>
    </div>
  );
}

function ResourceDial({ label, value, available = true }: { label: string; value: number; available?: boolean }) {
  const safe = clamp(value, 0, 100);
  return (
    <div className="relative rounded-2xl border border-white/[0.06] bg-black/10 p-4">
      <div className="flex items-center justify-between text-xs"><span className="flex items-center gap-2 font-semibold text-slate-300">{label}<MetricHelp label={label} /></span><span className="tabular-nums text-slate-400">{available ? `${safe.toFixed(1)} %` : "Sin medición"}</span></div>
      <div className="mt-3 h-1.5 overflow-hidden rounded-full bg-white/[0.06]"><div className={`h-full rounded-full ${toneBar(available ? usageTone(safe) : "slate")}`} style={{ width: available ? `${safe}%` : "0%" }} /></div>
    </div>
  );
}

function ServiceTile({ label, ok, detail, description }: { label: string; ok: boolean; detail?: string; description?: string }) {
  return (
    <div className={`rounded-2xl border p-4 ${ok ? "border-emerald-400/15 bg-emerald-400/[0.045]" : "border-rose-400/25 bg-rose-500/[0.07]"}`}>
      <div className="flex items-center justify-between gap-2"><span className="flex items-center gap-2"><span className={`h-2 w-2 rounded-full ${ok ? "bg-emerald-400 shadow-[0_0_12px_#4ade80]" : "bg-rose-400 shadow-[0_0_12px_#fb7185]"}`} /><span className="text-xs font-semibold text-slate-200">{label}</span></span><MetricHelp label={label} description={description} /></div>
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
    <div>
      <div className="mb-2 flex items-center justify-between gap-4 text-xs">
        <span className="flex items-center gap-2 font-medium text-slate-300">{props.label}<MetricHelp label={props.label} /></span>
        <span className="font-semibold tabular-nums text-white">{props.display}</span>
      </div>
      <div className="h-3 overflow-hidden rounded-full bg-white/[0.055]"><div className={`h-full rounded-full transition-[width] duration-700 ${toneBar(props.tone)}`} style={{ width: `${width}%` }} /></div>
    </div>
  );
}

function MetricCard(props: { label: string; value: string; helper?: string; accent?: boolean; danger?: boolean }) {
  const border = props.danger ? "border-rose-400/25 bg-rose-500/[0.055]" : props.accent ? "border-amber-300/20 bg-amber-400/[0.055]" : "border-white/[0.07] bg-white/[0.025]";
  return <MetricHelp label={props.label} ariaLabel={`Ayuda de ${props.label}, ${props.value}${props.helper ? `. ${props.helper}` : ""}`}
    className={`flex h-full w-full flex-col items-stretch rounded-2xl border p-4 text-left ${border}`}>
    <span className="flex items-start justify-between gap-2">
      <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-400">{props.label}</span>
      <span className="grid h-4 w-4 shrink-0 place-items-center rounded-full border border-amber-300/20 text-[9px] font-bold text-amber-200" aria-hidden="true">i</span>
    </span>
    <span className={`mt-2 text-2xl font-semibold tracking-tight ${props.danger ? "text-rose-100" : "text-white"}`}>{props.value}</span>
    {props.helper && <span className="mt-2 text-xs leading-5 text-slate-400">{props.helper}</span>}
  </MetricHelp>;
}

function SignalCard({ label, value, detail }: { label: string; value: string; detail: string }) {
  return <div className="relative rounded-2xl border border-white/[0.06] bg-black/10 p-4"><div className="flex items-start justify-between gap-2"><p className="text-[10px] uppercase tracking-wider text-slate-500">{label}</p><MetricHelp label={label} /></div><p className="mt-2 text-xl font-semibold text-white">{value}</p><p className="mt-1 text-[11px] text-slate-500">{detail}</p></div>;
}

function MiniMetric({ label, value, danger = false, description, nextStep }: { label: string; value: string; danger?: boolean; description?: string; nextStep?: string }) {
  return <MetricHelp label={label} description={description} nextStep={nextStep}
    ariaLabel={`Ayuda de ${label}, ${value}`} className="flex min-w-0 flex-col items-center rounded-lg p-1 text-center">
    <span className={`text-lg font-semibold ${danger ? "text-rose-300" : "text-white"}`}>{value}</span>
    <span className="mt-1 text-[10px] uppercase tracking-wide text-slate-400">{label}</span>
  </MetricHelp>;
}

function Panel({ children, className = "" }: { children: React.ReactNode; className?: string }) {
  return <section className={`dashboard-panel rounded-2xl border p-5 sm:p-6 ${className}`}>{children}</section>;
}

function PanelHeading({ eyebrow, title, description }: { eyebrow: string; title: string; description?: string }) {
  const explanation = description ?? panelExplanation(title);
  return <div className="relative"><p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-amber-300/70">{eyebrow}</p><div className="mt-1 flex items-center gap-2"><h3 className="text-xl font-semibold tracking-tight text-white">{title}</h3>{explanation && <HelpTrigger title={title} description={explanation} />}</div></div>;
}

export function panelExplanation(title: string): string | undefined {
  const explanations: Record<string, string> = {
    "Pulso operativo": "Índice de 0 a 100. Parte de 100 y descuenta dependencias degradadas, tasa de fallas técnicas de la última hora, pendientes sin actividad por más de 30 minutos y plazos de reanudación vencidos. Las sesiones activas normales no penalizan. Consulte el diagnóstico para ver cada deducción y cómo recuperarla. No es un SLA contractual.",
    "Ruta de los archivos": "Combina el flujo actual en memoria con resultados persistidos del día: sesiones esperando, transmisiones activas, cargas parciales reanudables y transferencias completadas. Cada etapa tiene su propia explicación contextual.",
    "Constelación de servicios": "Resume las dependencias que sostienen el ecosistema: base de datos, almacenamiento, bus interno y alertamiento. También muestra presión de CPU, memoria y disco. Una dependencia roja requiere diagnóstico aunque la página continúe respondiendo.",
    "Huella de actividad": "Muestra las 20 transferencias más recientes ordenadas por fecha de creación. Cada bloque conserva identidad estable y usa un color exclusivo por estado; al enfocarlo muestra RFC, tiempos, tamaño, control aplicado y causa del error.",
    "Recursos del servidor": "Compara CPU, memoria y disco contra una escala común de 0 a 100 %. Verde indica menos de 75 %, ámbar de 75 a 89.9 % y rojo 90 % o más. Una lectura aislada debe confirmarse con tendencia y duración.",
    "Completadas contra fallidas": "Compara completadas con fallas técnicas, fallas de catálogo y rechazos en el día medido y la última hora. Las cancelaciones administrativas y expiraciones cuentan por separado. Después de un reinicio estadístico, estas ventanas solo incluyen resultados posteriores al nuevo corte.",
    "Carga de la tubería": "Representa trabajo que todavía no finaliza: transmisiones activas, sesiones en espera y cargas parciales reanudables. Permite detectar acumulación antes de que se convierta en incumplimiento.",
    "Verificación y evidencia": "Relaciona recibos digitales, cargas reanudadas y verificaciones omitidas por una política explícita. Sirve para vigilar custodia e integridad, no solamente volumen transferido.",
    "Resumen ejecutivo": "Convierte las métricas del corte actual en observaciones accionables sobre estabilidad, focos de atención y continuidad. La lectura se recalcula automáticamente cada dos segundos.",
    "Anillos operativos": "Normaliza métricas diferentes a una escala de 0 a 100 para comparar salud, éxito, estabilidad, holgura, recibos y verificación. Un anillo cerrado representa mejor cobertura o condición.",
    "Mapa térmico operativo": "Agrupa señales por severidad. Verde significa condición normal, ámbar requiere vigilancia y rojo intervención. El color expresa condición, no cantidad absoluta.",
    "Flujo de transferencias": "Presenta de izquierda a derecha el trabajo acumulado y el estado del proceso. Mezcla resultados diarios con actividad actual, por lo que las etapas no deben sumarse como si fueran una sola población.",
    "Cronología de transferencias": "Ordena los eventos recientes desde el más nuevo y conserva su estado, tamaño y verificación. Es una vista rápida de trazabilidad; el recibo y la traza aparecen en la tabla detallada.",
    "Radar operativo": "Compara varias dimensiones convertidas a puntuaciones de 0 a 100. Facilita descubrir desequilibrios, pero no sustituye umbrales técnicos ni compromisos de servicio.",
    "Fortalezas y brechas": "Desglosa las dimensiones del radar para explicar qué componente eleva o reduce el equilibrio operativo. Valores bajos orientan la prioridad de investigación.",
    "Semáforo operativo": "Ordena las señales según la acción requerida. Empieza por Atender, revisa si persisten las señales de Vigilar y consulta la ayuda de cada señal para decidir el siguiente paso.",
    "Indicadores principales": "Muestra las cifras del corte junto con su contexto. Selecciona una tarjeta para entender qué datos incluye el indicador y cómo interpretarlos.",
    "Operación y custodia": "Relaciona las cargas abiertas con las completadas, los recibos y los controles de verificación. Consulta la ayuda de cada tarjeta para identificar su período y alcance."
  };
  return explanations[title];
}

function SectionTitle({ title, subtitle }: { title: string; subtitle: string }) {
  const explanation = panelExplanation(title);
  return <div><div className="flex items-center gap-2"><h3 className="text-lg font-semibold text-white">{title}</h3>{explanation && <HelpTrigger title={title} description={explanation} />}</div><p className="mt-1 text-sm text-slate-400">{subtitle}</p></div>;
}

function LiveBadge({ healthy }: { healthy: boolean }) {
  return <span className={`flex items-center gap-2 rounded-full border px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wide ${healthy ? "border-emerald-400/20 bg-emerald-400/[0.07] text-emerald-300" : "border-rose-400/25 bg-rose-400/[0.08] text-rose-200"}`}><span className={`h-1.5 w-1.5 rounded-full ${healthy ? "ase-pulse bg-emerald-400" : "bg-rose-400"}`} />{healthy ? "Sistema en línea" : "Atención requerida"}</span>;
}

function StatusBadge({ transfer }: { transfer: RecentTransferDto }) {
  const presentation = statusPresentation(statusKind(transfer.status));
  return <TransferHelp transfer={transfer} className={`inline-flex cursor-help rounded-full border px-2 py-1 text-[10px] font-semibold uppercase tracking-wide ${presentation.badgeClass}`}>
    {presentation.label}
  </TransferHelp>;
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

export function buildExecutiveObservations(summary: SummaryResponse): Array<{ title: string; body: string; tone: Tone }> {
  const { server, transfers, kpis } = summary;
  const activity = kpis.totalTransfersToday === 0 ? "Todavía no hay transferencias cerradas durante el día; el porcentaje de éxito aparecerá cuando exista actividad." : `Hoy cerraron ${kpis.totalTransfersToday} transferencias con ${formatPercent(kpis.successRateTodayPercent)} de éxito y ${formatBytes(transfers.totalBytesTransferredToday)} confirmados.`;
  const penalizedHealth = summary.healthBreakdown?.filter(item => item.penalty > 0) ?? [];
  const expiredPending = kpis.expiredPendingTransfers ?? 0;
  const attentionCount = kpis.stalePendingTransfers + expiredPending + transfers.failedLastHour + server.alertDeadLetter + penalizedHealth.length + (server.isHealthy ? 0 : 1);
  const attention = attentionCount === 0
    ? "No se detectan pendientes sin actividad, plazos vencidos ni alertas operativas en este corte."
    : `Revise ${kpis.stalePendingTransfers} pendientes sin actividad, ${expiredPending} pendientes con plazo vencido y ${server.alertDeadLetter} alertas en dead-letter. El período medido registra ${transfers.failedLastHour} fallas en la última hora. ${penalizedHealth.length ? `La salud conserva deducciones por: ${penalizedHealth.map(item => item.label).join(", ")}; consulte el diagnóstico para sanearlas.` : server.isHealthy ? "" : "Hay dependencias degradadas; consulte el diagnóstico."}`;
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
  try {
    const stored = window.localStorage.getItem(VIEW_PREFERENCE_KEY);
    return dashboardViews.find(option => option.mode === stored)?.mode ?? "pulse";
  } catch {
    return "pulse";
  }
}

export function statusKind(status: string): StatusKind {
  const normalized = status.trim().toLowerCase().replace(/[\s_-]/g, "");
  if (!normalized) return "unknown";
  if (normalized.includes("catalogfailed") || normalized.includes("catalogerror")) return "catalogFailed";
  if (["completed", "complete", "completado", "completedwithwarnings", "success", "succeeded"].some(value => normalized.includes(value))) return "completed";
  if (["cancelled", "canceled", "cancelado", "aborted"].some(value => normalized.includes(value))) return "cancelled";
  if (["failed", "error", "rejected", "fallido", "invalid", "expired"].some(value => normalized.includes(value))) return "failed";
  if (["paused", "resumable", "interrupted", "suspended"].some(value => normalized.includes(value))) return "paused";
  if (["uploading", "active", "transmitting", "verifying", "processing", "streaming"].some(value => normalized.includes(value))) return "transmitting";
  if (["pending", "queued", "waiting", "created", "initialized"].some(value => normalized.includes(value))) return "pending";
  return "unknown";
}

function statusDotClass(status: string): string {
  return statusPresentation(statusKind(status)).dotClass;
}

function statusPresentation(kind: StatusKind): { label: string; description: string; dotClass: string; badgeClass: string } {
  const presentations: Record<StatusKind, { label: string; description: string; dotClass: string; badgeClass: string }> = {
    completed: {
      label: "Completado",
      description: "El servidor recibió, cerró y confirmó correctamente la transferencia.",
      dotClass: "border-emerald-300/35 bg-emerald-400 shadow-[0_0_14px_rgba(52,211,153,0.38)]",
      badgeClass: "border-emerald-400/25 bg-emerald-400/[0.10] text-emerald-200"
    },
    transmitting: {
      label: "Transmitiendo",
      description: "La transferencia sigue en curso; todavía no debe interpretarse como éxito ni como falla.",
      dotClass: "ase-pulse border-sky-200/45 bg-sky-400 shadow-[0_0_16px_rgba(56,189,248,0.48)]",
      badgeClass: "border-sky-400/25 bg-sky-400/[0.10] text-sky-200"
    },
    pending: {
      label: "Pendiente",
      description: "La transferencia fue registrada o está en cola, pero aún no termina de enviar datos.",
      dotClass: "border-amber-200/45 bg-amber-400 shadow-[0_0_12px_rgba(251,191,36,0.30)]",
      badgeClass: "border-amber-400/25 bg-amber-400/[0.10] text-amber-200"
    },
    paused: {
      label: "Reanudable",
      description: "La carga se interrumpió y conserva bytes parciales para continuar desde el último punto seguro.",
      dotClass: "border-violet-200/45 bg-violet-400 shadow-[0_0_13px_rgba(167,139,250,0.35)]",
      badgeClass: "border-violet-400/25 bg-violet-400/[0.10] text-violet-200"
    },
    catalogFailed: {
      label: "Falla de catálogo",
      description: "Los bytes llegaron al servidor, pero falló el registro final en el catálogo o sistema de destino.",
      dotClass: "border-orange-200/45 bg-orange-500 shadow-[0_0_15px_rgba(249,115,22,0.42)]",
      badgeClass: "border-orange-400/30 bg-orange-400/[0.11] text-orange-200"
    },
    failed: {
      label: "Fallido",
      description: "La transferencia terminó con un error técnico, de validación, rechazo o expiración.",
      dotClass: "border-rose-200/45 bg-rose-500 shadow-[0_0_15px_rgba(244,63,94,0.42)]",
      badgeClass: "border-rose-400/30 bg-rose-400/[0.11] text-rose-200"
    },
    cancelled: {
      label: "Cancelado",
      description: "La operación fue cancelada o abortada; no está en curso y tampoco cuenta como completada.",
      dotClass: "border-slate-300/35 bg-slate-500 shadow-[0_0_10px_rgba(100,116,139,0.25)]",
      badgeClass: "border-slate-400/25 bg-slate-400/[0.10] text-slate-200"
    },
    unknown: {
      label: "Desconocido",
      description: "El servidor reportó un estado que el Dashboard todavía no reconoce. Debe revisarse el contrato de estados.",
      dotClass: "border-fuchsia-200/40 bg-fuchsia-500 shadow-[0_0_13px_rgba(217,70,239,0.35)]",
      badgeClass: "border-fuchsia-400/25 bg-fuchsia-400/[0.10] text-fuchsia-200"
    }
  };
  return presentations[kind];
}

function countRecentStatuses(transfers: RecentTransferDto[]): Record<StatusKind, number> {
  const counts: Record<StatusKind, number> = { completed: 0, transmitting: 0, pending: 0, paused: 0, catalogFailed: 0, failed: 0, cancelled: 0, unknown: 0 };
  transfers.forEach(transfer => { counts[statusKind(transfer.status)] += 1; });
  return counts;
}

function kpiToneClasses(tone: KpiTone): { panel: string; icon: string; value: string } {
  return ({
    green: { panel: "border-emerald-400/15 bg-emerald-400/[0.035]", icon: "border-emerald-400/25 text-emerald-300", value: "text-emerald-100" },
    red: { panel: "border-rose-400/25 bg-rose-400/[0.055]", icon: "border-rose-400/30 text-rose-300", value: "text-rose-100" },
    blue: { panel: "border-sky-400/15 bg-sky-400/[0.035]", icon: "border-sky-400/25 text-sky-300", value: "text-sky-100" },
    gold: { panel: "border-amber-400/18 bg-amber-400/[0.04]", icon: "border-amber-400/25 text-amber-300", value: "text-amber-100" },
    violet: { panel: "border-violet-400/20 bg-violet-400/[0.045]", icon: "border-violet-400/30 text-violet-300", value: "text-violet-100" },
    slate: { panel: "border-white/[0.07] bg-white/[0.025]", icon: "border-white/10 text-slate-400", value: "text-white" }
  } satisfies Record<KpiTone, { panel: string; icon: string; value: string }>)[tone];
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
