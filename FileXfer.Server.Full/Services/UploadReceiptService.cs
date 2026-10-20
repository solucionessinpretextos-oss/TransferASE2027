using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileXfer.Server.Data;
using FileXfer.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Services;

public sealed record UploadReceiptDraft(
    Guid UploadId,
    string FileUuid,
    string OriginalFileName,
    long Bytes,
    string Sha256,
    string Rfc,
    string? DeviceId,
    int? Periodo,
    int? IdEnte,
    int? IdAuditoria,
    int? IdActo,
    int? IdOficio,
    int? IdRequerimiento,
    int? IdResultado,
    string PolicyExtension,
    int PolicyVersion,
    bool VerificationRequired,
    bool VerificationPerformed,
    string VerificationOutcome,
    string TraceId);

public interface IUploadReceiptService
{
    Task<UploadReceipt> CreateAsync(UploadReceiptDraft draft, CancellationToken ct = default);
    bool Verify(UploadReceipt receipt);
}

public sealed class UploadReceiptService : IUploadReceiptService, IDisposable
{
    private static readonly SemaphoreSlim ChainGate = new(1, 1);
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly RSA _rsa;
    private readonly byte[] _publicKeyDer;
    private readonly string _publicKeyPem;
    private readonly string _keyId;

    public UploadReceiptService(
        IDbContextFactory<FileXferDbContext> dbFactory,
        ReceiptSigningOptions options,
        IWebHostEnvironment environment,
        ILogger<UploadReceiptService> logger)
    {
        _dbFactory = dbFactory;
        _rsa = RSA.Create();

        var pem = options.PrivateKeyPem;
        if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(options.PrivateKeyPemPath))
            pem = File.ReadAllText(Path.GetFullPath(options.PrivateKeyPemPath));

        if (!string.IsNullOrWhiteSpace(pem))
        {
            if (string.IsNullOrWhiteSpace(options.PrivateKeyPassword))
                _rsa.ImportFromPem(pem);
            else
                _rsa.ImportFromEncryptedPem(pem, options.PrivateKeyPassword);
        }
        else if (environment.IsDevelopment() && options.AllowEphemeralDevelopmentKey)
        {
            _rsa.KeySize = 3072;
            logger.LogWarning(
                "Los recibos usan una llave RSA efímera de Development. Configure Security:Receipts:PrivateKeyPem o PrivateKeyPemPath antes de producción.");
        }
        else
        {
            throw new InvalidOperationException(
                "Security:Receipts requiere una llave privada RSA PEM persistente fuera de Development.");
        }

        if (_rsa.KeySize < 3072)
            throw new InvalidOperationException("La llave de firma de recibos debe ser RSA de al menos 3072 bits.");

        _publicKeyDer = _rsa.ExportSubjectPublicKeyInfo();
        _publicKeyPem = _rsa.ExportSubjectPublicKeyInfoPem();
        _keyId = Convert.ToHexString(SHA256.HashData(_publicKeyDer))[..24].ToLowerInvariant();
    }

    public async Task<UploadReceipt> CreateAsync(UploadReceiptDraft draft, CancellationToken ct = default)
    {
        await ChainGate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    // SQL Server está configurado con reintentos transitorios. EF Core exige que
                    // toda transacción explícita se ejecute dentro de su estrategia; de otro modo
                    // la primera consulta dentro de BeginTransaction falla antes de emitir recibo.
                    await using var strategyContext = await _dbFactory.CreateDbContextAsync(ct);
                    var strategy = strategyContext.Database.CreateExecutionStrategy();
                    return await strategy.ExecuteAsync(() => CreateTransactionalAsync(draft, ct));
                }
                catch (Exception exception) when (attempt < 3 && IsRetryableChainConflict(exception))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
                }
            }

            throw new InvalidOperationException("No se pudo serializar la cadena de recibos después de tres intentos.");
        }
        finally
        {
            ChainGate.Release();
        }
    }

    private async Task<UploadReceipt> CreateTransactionalAsync(
        UploadReceiptDraft draft,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        var existing = await db.UploadReceipts
            .SingleOrDefaultAsync(receipt => receipt.UploadId == draft.UploadId, ct);
        if (existing is not null)
        {
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return existing;
        }

        var chainState = await db.ReceiptChainStates.SingleOrDefaultAsync(state => state.Id == 1, ct);
        if (chainState is null)
        {
            chainState = new ReceiptChainState
            {
                Id = 1,
                HeadHash = null,
                Version = 0,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            db.ReceiptChainStates.Add(chainState);
        }

        var receiptId = Guid.NewGuid();
        var issuedUtc = DateTimeOffset.UtcNow;
        var payload = BuildCanonicalPayload(receiptId, issuedUtc, draft);
        var payloadHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var payloadHash = Convert.ToHexString(payloadHashBytes).ToLowerInvariant();
        var previousHash = chainState.HeadHash;
        var previousBytes = string.IsNullOrWhiteSpace(previousHash)
            ? new byte[32]
            : Convert.FromHexString(previousHash);
        var chainMaterial = new byte[previousBytes.Length + payloadHashBytes.Length];
        previousBytes.CopyTo(chainMaterial, 0);
        payloadHashBytes.CopyTo(chainMaterial, previousBytes.Length);
        var chainHashBytes = SHA256.HashData(chainMaterial);
        var chainHash = Convert.ToHexString(chainHashBytes).ToLowerInvariant();
        var signature = _rsa.SignHash(
            chainHashBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);

        var receipt = new UploadReceipt
        {
            Id = receiptId,
            UploadId = draft.UploadId,
            IssuedUtc = issuedUtc,
            CanonicalPayloadJson = payload,
            PayloadSha256 = payloadHash,
            PreviousChainHash = previousHash,
            ChainHash = chainHash,
            SignatureBase64 = Convert.ToBase64String(signature),
            SignatureAlgorithm = "RSA-PSS-SHA256",
            KeyId = _keyId,
            PublicKeyPem = _publicKeyPem
        };
        db.UploadReceipts.Add(receipt);
        chainState.HeadHash = chainHash;
        chainState.Version++;
        chainState.UpdatedUtc = issuedUtc;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        ServerMetrics.UploadReceiptsIssuedTotal.Add(1);
        return receipt;
    }

    public bool Verify(UploadReceipt receipt)
    {
        try
        {
            if (!string.Equals(receipt.SignatureAlgorithm, "RSA-PSS-SHA256", StringComparison.Ordinal) ||
                !string.Equals(receipt.KeyId, _keyId, StringComparison.Ordinal))
            {
                return false;
            }

            var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(receipt.CanonicalPayloadJson));
            if (!CryptographicOperations.FixedTimeEquals(payloadHash, Convert.FromHexString(receipt.PayloadSha256)))
                return false;
            var previous = string.IsNullOrWhiteSpace(receipt.PreviousChainHash)
                ? new byte[32]
                : Convert.FromHexString(receipt.PreviousChainHash);
            var material = new byte[previous.Length + payloadHash.Length];
            previous.CopyTo(material, 0);
            payloadHash.CopyTo(material, previous.Length);
            var chainHash = SHA256.HashData(material);
            if (!CryptographicOperations.FixedTimeEquals(chainHash, Convert.FromHexString(receipt.ChainHash)))
                return false;

            using var publicRsa = RSA.Create();
            publicRsa.ImportFromPem(receipt.PublicKeyPem);
            var suppliedPublicKey = publicRsa.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(_publicKeyDer, suppliedPublicKey))
                return false;

            return publicRsa.VerifyHash(
                chainHash,
                Convert.FromBase64String(receipt.SignatureBase64),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose() => _rsa.Dispose();

    private static bool IsRetryableChainConflict(Exception exception) =>
        exception is DbUpdateException ||
        exception is Microsoft.Data.SqlClient.SqlException { Number: 1205 };

    private static string BuildCanonicalPayload(
        Guid receiptId,
        DateTimeOffset issuedUtc,
        UploadReceiptDraft draft)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("receiptId", receiptId);
            writer.WriteString("uploadId", draft.UploadId);
            writer.WriteString("fileUuid", draft.FileUuid);
            writer.WriteString("originalFileName", draft.OriginalFileName);
            writer.WriteNumber("bytes", draft.Bytes);
            writer.WriteString("sha256", draft.Sha256);
            writer.WriteString("rfc", draft.Rfc);
            WriteNullable(writer, "deviceId", draft.DeviceId);
            WriteNullable(writer, "periodo", draft.Periodo);
            WriteNullable(writer, "idEnte", draft.IdEnte);
            WriteNullable(writer, "idAuditoria", draft.IdAuditoria);
            WriteNullable(writer, "idActo", draft.IdActo);
            WriteNullable(writer, "idOficio", draft.IdOficio);
            WriteNullable(writer, "idRequerimiento", draft.IdRequerimiento);
            WriteNullable(writer, "idResultado", draft.IdResultado);
            writer.WriteString("policyExtension", draft.PolicyExtension);
            writer.WriteNumber("policyVersion", draft.PolicyVersion);
            writer.WriteBoolean("verificationRequired", draft.VerificationRequired);
            writer.WriteBoolean("verificationPerformed", draft.VerificationPerformed);
            writer.WriteString("verificationOutcome", draft.VerificationOutcome);
            writer.WriteString("traceId", draft.TraceId);
            writer.WriteString("issuedUtc", issuedUtc.ToString("O"));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value.HasValue) writer.WriteNumber(name, value.Value); else writer.WriteNull(name);
    }
}
