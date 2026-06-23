using FlowTrack.Application;
using FlowTrack.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.API.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public async Task<IActionResult> Get([FromServices] AppDbContext db, [FromServices] IWorkerMonitor workerMonitor)
    {
        var databaseOk = await db.Database.CanConnectAsync();
        var workerHealthy = workerMonitor.LastRunAtUtc is null
            || workerMonitor.LastRunAtUtc >= DateTime.UtcNow.AddMinutes(-5);

        var status = databaseOk && workerHealthy ? "healthy" : "degraded";

        return Ok(new
        {
            status,
            api = "healthy",
            database = databaseOk ? "healthy" : "unhealthy",
            worker = workerHealthy ? "healthy" : "degraded",
            workerMonitor.LastRunAtUtc,
            workerMonitor.LastSuccessAtUtc,
            workerMonitor.LastError
        });
    }
}
