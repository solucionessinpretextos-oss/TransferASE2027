using System.Diagnostics;

namespace FileXfer.Server.Telemetry;

public static class TransferTelemetry
{
    public const string ActivitySourceName = "FileXfer.Server.Transfer";
    public static readonly ActivitySource Activities = new(ActivitySourceName, "1.0.0");

    public static Activity? Start(string name, ActivityKind kind = ActivityKind.Internal) =>
        Activities.StartActivity(name, kind);
}
