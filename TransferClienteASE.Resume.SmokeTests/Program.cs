using Microsoft.Data.Sqlite;
using TransferClienteASE.Modelos;
using TransferClienteASE.Servicios;

var root = Path.Combine(Path.GetTempPath(), "TransferASE-ResumeSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    SesionAplicacion.Rfc = "TST010101AAA";
    SesionAplicacion.DeviceId = "TRANSFER-ASE-" + Guid.NewGuid().ToString("N");

    var context = new ContextoRequerimiento
    {
        Periodo = 2027,
        IdColabora = 10,
        IdAuditoria = "20",
        IdActo = 30,
        IdOficio = 40,
        IdRequerimiento = 50
    };
    var original = Path.Combine(root, "informe.pdf");
    await File.WriteAllBytesAsync(original, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());

    var journal = new ServicioDiarioTransferencias(root);
    await journal.RegistrarEnColaAsync(context, original);
    Require((await journal.ObtenerPendientesAsync(context)).Count == 1, "No se persistió la cola inicial.");

    var uuid = Guid.NewGuid().ToString("N");
    var prepared = await journal.ConservarCopiaPreparadaAsync(uuid, "informe.pdf", original, CancellationToken.None);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(prepared)))
        .ToLowerInvariant();
    await journal.MarcarPreparadoAsync(
        context, original, prepared, uuid, hash, new FileInfo(prepared).Length,
        wasRepaired: false, verificationRequired: true, policyVersion: 1);
    await journal.MarcarProgresoAsync(context, original, 2048, "Paused", "Reinicio simulado");

    var reopened = new ServicioDiarioTransferencias(root);
    var restored = await reopened.ObtenerPendientesAsync(context);
    Require(restored.Count == 1, "La cola no sobrevivió a la reapertura del diario.");
    Require(restored[0].FileUuid == uuid, "Cambió el FileUuid persistido.");
    Require(restored[0].ConfirmedBytes == 2048, "No se restauró el offset confirmado.");
    Require(File.Exists(restored[0].PreparedPath), "No se conservó la copia segura.");

    await reopened.CompletarAsync(context, original, 4096, Guid.NewGuid().ToString("D"), "trace-smoke");
    Require((await reopened.ObtenerPendientesAsync(context)).Count == 0, "Una carga completada volvió a aparecer como pendiente.");
    Require(!File.Exists(prepared), "La copia segura no se eliminó después del recibo.");

    var changedOriginal = Path.Combine(root, "cambiado.txt");
    await File.WriteAllTextAsync(changedOriginal, "versión inicial");
    await reopened.RegistrarEnColaAsync(context, changedOriginal);
    var changedUuid = Guid.NewGuid().ToString("N");
    var changedPrepared = await reopened.ConservarCopiaPreparadaAsync(
        changedUuid, "cambiado.txt", changedOriginal, CancellationToken.None);
    var changedHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(changedPrepared)))
        .ToLowerInvariant();
    await reopened.MarcarPreparadoAsync(
        context, changedOriginal, changedPrepared, changedUuid, changedHash,
        new FileInfo(changedPrepared).Length, false, true, 1);
    await File.AppendAllTextAsync(changedOriginal, " modificada");
    await reopened.RegistrarEnColaAsync(context, changedOriginal);
    var invalidated = await reopened.ObtenerAsync(context, changedOriginal);
    Require(invalidated?.PreparedPath is null, "Se reutilizó una copia después de cambiar el original.");
    Require(invalidated?.ConfirmedBytes == 0, "Se conservó un offset perteneciente a otro contenido.");
    Require(!File.Exists(changedPrepared), "No se limpió la copia obsoleta del archivo modificado.");

    Console.WriteLine("PASS: journal survives reopen, restores offset, cleans completion and invalidates changed sources.");
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); } catch { }
    SesionAplicacion.Limpiar();
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
