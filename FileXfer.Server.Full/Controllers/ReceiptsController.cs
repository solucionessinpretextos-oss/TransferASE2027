using System.Security.Claims;
using FileXfer.Server.Data;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileXfer.Server.Controllers;

[ApiController]
[Route("receipts")]
[Authorize]
public sealed class ReceiptsController : ControllerBase
{
    private readonly IDbContextFactory<FileXferDbContext> _dbFactory;
    private readonly IUploadReceiptService _receiptService;

    public ReceiptsController(
        IDbContextFactory<FileXferDbContext> dbFactory,
        IUploadReceiptService receiptService)
    {
        _dbFactory = dbFactory;
        _receiptService = receiptService;
    }

    [HttpGet("{receiptId:guid}")]
    public async Task<ActionResult<UploadReceiptResponse>> Get(Guid receiptId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var data = await (
            from receipt in db.UploadReceipts.AsNoTracking()
            join upload in db.Uploads.AsNoTracking() on receipt.UploadId equals upload.Id
            where receipt.Id == receiptId
            select new { Receipt = receipt, UploadRfc = upload.Rfc, upload.OriginalFileName })
            .SingleOrDefaultAsync(ct);
        if (data is null)
            return NotFound();

        var callerRfc = User.FindFirst("rfc")?.Value;
        var hasPrivilegedRole = User.IsInRole("admin") || User.IsInRole("ops");
        var privileged = hasPrivilegedRole && User.HasClaim("amr", "efirma+mfa");
        if (!privileged && !string.Equals(callerRfc, data.UploadRfc, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var chainLinkValid = string.IsNullOrWhiteSpace(data.Receipt.PreviousChainHash)
            ? !await db.UploadReceipts.AsNoTracking().AnyAsync(
                receipt => receipt.Id != data.Receipt.Id && receipt.PreviousChainHash == null,
                ct)
            : await db.UploadReceipts.AsNoTracking().AnyAsync(
                receipt => receipt.ChainHash == data.Receipt.PreviousChainHash,
                ct);

        return Ok(new UploadReceiptResponse
        {
            Id = data.Receipt.Id,
            UploadId = data.Receipt.UploadId,
            FileName = data.OriginalFileName ?? string.Empty,
            IssuedUtc = data.Receipt.IssuedUtc,
            CanonicalPayloadJson = data.Receipt.CanonicalPayloadJson,
            PayloadSha256 = data.Receipt.PayloadSha256,
            PreviousChainHash = data.Receipt.PreviousChainHash,
            ChainHash = data.Receipt.ChainHash,
            SignatureBase64 = data.Receipt.SignatureBase64,
            SignatureAlgorithm = data.Receipt.SignatureAlgorithm,
            KeyId = data.Receipt.KeyId,
            PublicKeyPem = data.Receipt.PublicKeyPem,
            SignatureValid = _receiptService.Verify(data.Receipt),
            ChainLinkValid = chainLinkValid
        });
    }
}

public sealed class UploadReceiptResponse
{
    public Guid Id { get; set; }
    public Guid UploadId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset IssuedUtc { get; set; }
    public string CanonicalPayloadJson { get; set; } = string.Empty;
    public string PayloadSha256 { get; set; } = string.Empty;
    public string? PreviousChainHash { get; set; }
    public string ChainHash { get; set; } = string.Empty;
    public string SignatureBase64 { get; set; } = string.Empty;
    public string SignatureAlgorithm { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string PublicKeyPem { get; set; } = string.Empty;
    public bool SignatureValid { get; set; }
    public bool ChainLinkValid { get; set; }
}
