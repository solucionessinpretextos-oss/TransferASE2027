using FileXfer.Server.Dtos.Admin;
using FileXfer.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileXfer.Server.Controllers.Admin;

[ApiController]
[Route("admin/monitoring/health")]
[Authorize(Policy = "AdminEfirma")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MonitoringHealthController(ISystemHealthService health) : ControllerBase
{
    [HttpPost("recheck")]
    public async Task<ActionResult<ServerHealthDto>> Recheck(CancellationToken ct) =>
        Ok(await health.RecheckAsync(ct));
}
