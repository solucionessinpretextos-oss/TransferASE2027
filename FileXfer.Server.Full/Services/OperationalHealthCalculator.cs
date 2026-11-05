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
            Detail = DescribeDependencies(server),
            Remediation = DescribeRecovery(server)
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

    private static string DescribeDependencies(ServerHealthDto server)
    {
        var details = new List<string>();
        if (!server.DatabaseOk) details.Add("La conexión a FileXferDb no respondió correctamente.");
        if (!server.StorageOk)
            details.Add(server.StorageDiagnostic?.Message ?? "El almacenamiento no superó su comprobación funcional.");
        if (!server.MessageBusOk) details.Add("El canal interno de trabajo reporta una incidencia.");
        if (!server.AlertingOk)
            details.Add($"Alertamiento degradado: {server.AlertQueuePending} entregas pendientes, {server.AlertDeadLetter} agotaron sus reintentos.");
        if (server.IsHealthy)
        {
            details.Clear();
            details.Add("Las dependencias observadas están operativas.");
            if (server.StorageDiagnostic is not null) details.Add(server.StorageDiagnostic.Message);
        }
        else if (details.Count == 0) details.Add(server.Message ?? "Una dependencia reportó degradación.");
        return string.Join(" ", details);
    }

    private static string DescribeRecovery(ServerHealthDto server)
    {
        if (server.IsHealthy)
            return "No requiere reparación de dependencias. Puede repetir la comprobación del almacenamiento para confirmar lectura, escritura y eliminación con la identidad del servicio.";
        var steps = new List<string>();
        if (!server.DatabaseOk) steps.Add("Compruebe la instancia SQL configurada y la cuenta del servicio; consulte /readyz para distinguir FileXfer de BuDi.");
        if (!server.StorageOk)
            steps.Add(server.StorageDiagnostic?.Remediation ?? "Ejecute Comprobar almacenamiento ahora para obtener la operación que falló, la ruta y la cuenta que debe tener permisos.");
        if (!server.MessageBusOk) steps.Add("Revise la disponibilidad del canal de trabajo configurado.");
        if (!server.AlertingOk) steps.Add("Consulte Entrega de alertas en este diagnóstico: muestra canal, evento o archivo, intentos fallidos, causa y corrección. Corrija la configuración del canal antes de reintentar; reiniciar estadísticas no entrega las notificaciones pendientes.");
        return string.Join(" ", steps);
    }
}
