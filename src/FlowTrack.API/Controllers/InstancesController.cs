using System.Security.Claims;
using System.Text.Json;
using FlowTrack.Application;
using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize]
[Route("api/instances")]
public sealed class InstancesController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InstanceDto>>> GetAll(
        [FromQuery] Guid? flowId,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromServices] AppDbContext db)
    {
        var query = db.FlowInstances
            .AsNoTracking()
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
            .AsQueryable();

        if (flowId.HasValue)
        {
            query = query.Where(x => x.FlowDefinitionId == flowId.Value);
        }

        if (Enum.TryParse<InstanceStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(x => x.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Code.ToLower().Contains(normalized));
        }

        var rows = await query.OrderByDescending(x => x.UpdatedAt).Take(200).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InstanceDto>> GetById(Guid id, [FromServices] AppDbContext db)
    {
        var item = await db.FlowInstances
            .AsNoTracking()
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            return NotFound();
        }

        return Ok(ToDto(item));
    }

    [HttpPost]
    public async Task<ActionResult<object>> Create([FromBody] CreateInstanceRequest request, [FromServices] AppDbContext db)
    {
        var flow = await db.FlowDefinitions
            .Include(x => x.Steps)
                .ThenInclude(x => x.Fields)
            .SingleOrDefaultAsync(x => x.Id == request.FlowDefinitionId && x.Active && x.LifecycleStatus == FlowLifecycleStatus.Published);

        if (flow is null)
        {
            return NotFound();
        }

        var orderedSteps = flow.Steps.OrderBy(x => x.Order).ToList();
        var firstStep = orderedSteps.FirstOrDefault();

        if (firstStep is not null)
        {
            var missing = firstStep.Fields
                .Where(x => x.Required)
                .Where(x => !request.Data.TryGetValue(x.Key, out var value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || string.IsNullOrWhiteSpace(value.ToString()))
                .Select(x => x.Label)
                .ToArray();

            if (missing.Length > 0)
            {
                return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["required"] = missing }));
            }
        }

        var now = DateTime.UtcNow;
        var instance = new FlowInstance
        {
            FlowDefinitionId = flow.Id,
            Code = string.IsNullOrWhiteSpace(request.Code) ? $"FL-{now:yyyyMMddHHmmss}" : request.Code.Trim(),
            DataJson = JsonSerializer.Serialize(request.Data),
            CurrentStepOrder = firstStep?.Order ?? 0,
            StepExecutions = orderedSteps
                .Select((step, index) => new StepExecution
                {
                    FlowStepId = step.Id,
                    Status = index == 0 ? StepStatus.InProgress : StepStatus.Pending,
                    StartedAt = index == 0 ? now : null
                })
                .ToList()
        };

        if (orderedSteps.Count == 0)
        {
            instance.Status = InstanceStatus.Completed;
        }

        db.FlowInstances.Add(instance);
        await db.SaveChangesAsync();

        return Created($"/api/instances/{instance.Id}", new { instance.Id });
    }

    [HttpPost("{id:guid}/advance")]
    public async Task<IActionResult> Advance(
        Guid id,
        [FromBody] AdvanceStepRequest request,
        [FromServices] AppDbContext db)
    {
        var item = await db.FlowInstances
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
            .SingleOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            return NotFound();
        }

        if (item.Status != InstanceStatus.InProgress)
        {
            return Conflict(new { message = "Execução não está em andamento." });
        }

        var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress);
        if (current is null)
        {
            return Conflict(new { message = "Nenhuma etapa ativa encontrada." });
        }

        var now = DateTime.UtcNow;
        current.Status = StepStatus.Completed;
        current.CompletedAt = now;
        current.Notes = request.Notes;

        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId))
        {
            current.CompletedByUserId = userId;
        }

        var next = item.StepExecutions
            .Where(x => x.FlowStep.Order > current.FlowStep.Order)
            .OrderBy(x => x.FlowStep.Order)
            .FirstOrDefault();

        if (next is null)
        {
            item.Status = InstanceStatus.Completed;
        }
        else
        {
            next.Status = StepStatus.InProgress;
            next.StartedAt = now;
            item.CurrentStepOrder = next.FlowStep.Order;
        }

        item.UpdatedAt = now;
        await db.SaveChangesAsync();

        return NoContent();
    }

    private static InstanceDto ToDto(FlowInstance item)
    {
        return new InstanceDto(
            item.Id,
            item.FlowDefinitionId,
            item.FlowDefinition.Name,
            item.Code,
            item.Status,
            item.CurrentStepOrder,
            item.CreatedAt,
            item.UpdatedAt,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.DataJson) ?? [],
            item.StepExecutions
                .OrderBy(x => x.FlowStep.Order)
                .Select(x => new StepProgressDto(
                    x.Id,
                    x.FlowStep.Name,
                    x.FlowStep.Order,
                    x.FlowStep.Type,
                    x.Status,
                    x.StartedAt,
                    x.CompletedAt))
                .ToList());
    }
}
