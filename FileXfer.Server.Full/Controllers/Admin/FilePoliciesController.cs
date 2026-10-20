using System.Security.Claims;
using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/file-policies")]
[Authorize(Policy = "PrivilegedEfirma")]
public sealed class FilePoliciesController : ControllerBase
{
    private readonly IFilePolicyService _policies;
    private readonly ILogger<FilePoliciesController> _logger;

    public FilePoliciesController(
        IFilePolicyService policies,
        ILogger<FilePoliciesController> logger)
    {
        _policies = policies;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FilePolicyDto>>> GetAll(CancellationToken ct)
    {
        var policies = await _policies.GetAllAsync(ct);
        return Ok(policies.Select(ToDto).ToArray());
    }

    [HttpGet("{extension}/history")]
    public async Task<ActionResult<IReadOnlyList<FilePolicyRevisionDto>>> GetHistory(
        string extension,
        CancellationToken ct)
    {
        try
        {
            var history = await _policies.GetHistoryAsync(extension, ct);
            return Ok(history.Select(revision => new FilePolicyRevisionDto
            {
                Id = revision.Id,
                Extension = revision.Extension,
                IsAllowed = revision.IsAllowed,
                RequiresVerification = revision.RequiresVerification,
                MaxFileBytes = revision.MaxFileBytes,
                Version = revision.Version,
                ChangedUtc = revision.ChangedUtc,
                ChangedBy = revision.ChangedBy,
                ChangeReason = revision.ChangeReason
            }).ToArray());
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPut("{extension}")]
    [Authorize(Policy = "AdminEfirma")]
    public async Task<ActionResult<FilePolicyDto>> Upsert(
        string extension,
        [FromBody] UpdateFilePolicyRequest request,
        CancellationToken ct)
    {
        try
        {
            var actor = GetActor();
            var result = await _policies.UpsertAsync(
                extension,
                new FilePolicyChange(
                    request.IsAllowed,
                    request.RequiresVerification,
                    request.MaxFileBytes,
                    request.ExpectedVersion,
                    request.Reason),
                actor,
                ct);

            _logger.LogWarning(
                "Política de archivo modificada. Actor={Actor}, Extension={Extension}, Allowed={Allowed}, Verify={Verify}, Version={Version}, TraceId={TraceId}",
                actor,
                result.Extension,
                result.IsAllowed,
                result.RequiresVerification,
                result.Version,
                HttpContext.TraceIdentifier);
            return Ok(ToDto(result));
        }
        catch (FilePolicyConflictException exception)
        {
            return Conflict(new { message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("rollback")]
    [Authorize(Policy = "AdminEfirma")]
    public async Task<ActionResult<FilePolicyDto>> Rollback(
        [FromBody] RollbackFilePolicyRequest request,
        CancellationToken ct)
    {
        try
        {
            var actor = GetActor();
            var result = await _policies.RollbackAsync(
                request.RevisionId,
                request.ExpectedVersion,
                request.Reason,
                actor,
                ct);
            _logger.LogWarning(
                "Rollback de política aplicado. Actor={Actor}, Extension={Extension}, Version={Version}, TraceId={TraceId}",
                actor,
                result.Extension,
                result.Version,
                HttpContext.TraceIdentifier);
            return Ok(ToDto(result));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (FilePolicyConflictException exception)
        {
            return Conflict(new { message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private string GetActor() =>
        User.FindFirst("rfc")?.Value ??
        User.FindFirst(ClaimTypes.Name)?.Value ??
        "unknown";

    private static FilePolicyDto ToDto(FilePolicySnapshot policy) => new()
    {
        Id = policy.Id,
        Extension = policy.Extension,
        IsAllowed = policy.IsAllowed,
        RequiresVerification = policy.RequiresVerification,
        MaxFileBytes = policy.MaxFileBytes,
        Version = policy.Version,
        UpdatedUtc = policy.UpdatedUtc,
        UpdatedBy = policy.UpdatedBy,
        ChangeReason = policy.ChangeReason
    };
}

[ApiController]
[Route("file-policies")]
[Authorize]
public sealed class EffectiveFilePoliciesController : ControllerBase
{
    private readonly IFilePolicyService _policies;

    public EffectiveFilePoliciesController(IFilePolicyService policies) => _policies = policies;

    [HttpGet("effective")]
    public async Task<ActionResult<IReadOnlyList<EffectiveFilePolicyDto>>> GetEffective(CancellationToken ct)
    {
        var policies = await _policies.GetAllAsync(ct);
        return Ok(policies
            .Where(policy => policy.IsAllowed)
            .Select(policy => new EffectiveFilePolicyDto
            {
                Extension = policy.Extension,
                IsAllowed = true,
                RequiresVerification = policy.RequiresVerification,
                MaxFileBytes = policy.MaxFileBytes,
                Version = policy.Version
            })
            .ToArray());
    }
}
