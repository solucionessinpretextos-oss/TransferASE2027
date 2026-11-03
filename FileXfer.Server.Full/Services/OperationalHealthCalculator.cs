using FileXfer.Server.Dtos.Admin;

namespace FileXfer.Server.Services;

public static class OperationalHealthCalculator
{
    public static IReadOnlyList<HealthScoreComponentDto> Explain(
        ServerHealthDto server, double failureRateLastHourPercent, int stalePending, int expiredPending) =>
    [
        new()
        {
            Key = "dependencies", Label = "Dependencias críticas", MaxPenalty = 25,
            Penalty = server.IsHealthy ? 0 : 25,
            Detail = server.IsHealthy ? "Las dependencias observadas están disponibles." : server.Message ?? "Hay dependencias degradadas.",
            Remediation = "Revise /readyz, conectividad y esquema de SQL, acceso de la cuenta de servicio al almacenamiento y entrega de alertas SMTP/outbox. Confirme recuperación antes de reiniciar procesos."
        },
        new()
        {
            Key = "failures", Label = "Fallas técnicas de la última hora", MaxPenalty = 30,
            Penalty = Math.Min(30, Math.Clamp(failureRateLastHourPercent, 0, 100) * 0.6),
            Detail = $"{failureRateLastHourPercent:F1}% de los cierres técnicos recientes fallaron; cada punto porcentual descuenta 0.6, hasta 30 puntos. Cancelaciones administrativas y vencimientos no cuentan como falla técnica.",
            Remediation = "Revise LastError y TraceId; resuelva red, validación, catálogo o firma de recibos según el error. La penalización baja al salir las fallas de la ventana móvil de 60 minutos o mejorar la proporción de cierres exitosos."
        },
        new()
        {
            Key = "idle", Label = "Pendientes sin actividad", MaxPenalty = 20,
            Penalty = Math.Min(20, Math.Max(0, stalePending) * 5.0),
            Detail = $"{stalePending} pendientes inactivos por más de 30 minutos y todavía dentro de su plazo; 5 puntos por archivo, hasta 20. Una carga activa normal no se penaliza.",
            Remediation = "Pida al usuario reconectar y reanudar. Si la carga se abandonó, selecciónela en Pendientes y elimínela indicando motivo; necesitará iniciar un nuevo envío si desea recuperarla."
        },
        new()
        {
            Key = "expired", Label = "Pendientes con plazo vencido", MaxPenalty = 15,
            Penalty = Math.Min(15, Math.Max(0, expiredPending) * 3.0),
            Detail = $"{expiredPending} pendientes inactivos alcanzaron su vencimiento individual; 3 puntos por archivo, hasta 15. Se usa ResumeExpiresUtc y, para registros antiguos, última actividad más retención configurada.",
            Remediation = "La limpieza automática revisa cada minuto. Use Limpiar vencidos para adelantarla; si persisten, revise errores de limpieza, disponibilidad del storage y permisos de borrado."
        }
    ];

    public static double Score(IReadOnlyList<HealthScoreComponentDto> components) =>
        Math.Round(Math.Clamp(100 - components.Sum(component => component.Penalty), 0, 100), 1);
}
