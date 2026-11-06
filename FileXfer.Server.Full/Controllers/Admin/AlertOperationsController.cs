using System.ComponentModel.DataAnnotations;
using System.Globalization;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/alerts")]
[Authorize(Policy = "AdminEfirma")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AlertOperationsController(AlertOperationsService operations) : ControllerBase
{
    public sealed record TestRequest([Required] string Channel, Guid RequestId);
    public sealed record ReprocessRequest([Required, StringLength(500, MinimumLength = 5)] string Reason, Guid RequestId);

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestRequest request, CancellationToken ct)
    {
        var actor = User.FindFirst("rfc")?.Value;
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        return Respond(await operations.TestAsync(actor, HttpContext.Connection.RemoteIpAddress?.ToString(),
            request.Channel, request.RequestId, ct));
    }

    [HttpPost("{id:guid}/reprocess")]
    public async Task<IActionResult> Reprocess(Guid id, [FromBody] ReprocessRequest request, CancellationToken ct)
    {
        var actor = User.FindFirst("rfc")?.Value;
        if (string.IsNullOrWhiteSpace(actor)) return Forbid();
        return Respond(await operations.ReprocessAsync(actor, HttpContext.Connection.RemoteIpAddress?.ToString(),
            id, request.Reason, request.RequestId, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AlertOperationsService.DeliveryDetail>> Get(Guid id, CancellationToken ct)
    {
        var detail = await operations.GetAsync(id, ct);
        if (detail is null) return NotFound(new { message = "No se encontró la entrega." });
        return Ok(detail);
    }

    private IActionResult Respond(AlertOperationsService.Result result)
    {
        if (result.Delivery is { } delivery)
            return AcceptedAtAction(nameof(Get), new { id = delivery.DeliveryId }, delivery);
        if (result.RetryAfterSeconds is { } retryAfter)
            Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        return StatusCode(result.StatusCode, new { message = result.Message, deliveryId = result.ExistingDeliveryId });
    }
}
