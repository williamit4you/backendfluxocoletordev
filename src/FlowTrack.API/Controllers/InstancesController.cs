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
        var item = await LoadInstance(db).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (item is null)
        {
            return NotFound();
        }

        return Ok(ToDto(item));
    }

    [HttpPost]
    public async Task<ActionResult<object>> Create(
        [FromBody] CreateInstanceRequest request,
        [FromServices] AppDbContext db,
        [FromServices] IIntegrationExecutionService integrations)
    {
        var flow = await db.FlowDefinitions
            .Include(x => x.Tokens)
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
        await ProcessAutomaticStepsAsync(instance.Id, db, integrations, HttpContext.RequestAborted);

        return Created($"/api/instances/{instance.Id}", new { instance.Id });
    }

    [HttpPost("{id:guid}/advance")]
    public async Task<IActionResult> Advance(
        Guid id,
        [FromBody] AdvanceStepRequest request,
        [FromServices] AppDbContext db,
        [FromServices] IIntegrationExecutionService integrations)
    {
        var item = await LoadInstance(db).SingleOrDefaultAsync(x => x.Id == id);
        if (item is null)
        {
            return NotFound();
        }

        if (item.Status != InstanceStatus.InProgress)
        {
            return Conflict(new { message = "Execucao nao esta em andamento." });
        }

        var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress);
        if (current is null)
        {
            return Conflict(new { message = "Nenhuma etapa ativa encontrada." });
        }

        if (current.FlowStep.Type == StepType.ApiSend || current.FlowStep.Type == StepType.ApiQuery || current.FlowStep.Type == StepType.Automatic)
        {
            return Conflict(new { message = "A etapa atual eh automatica. Use o retry de integracao se necessario." });
        }

        CompleteCurrentStep(item, current, request.Notes, TryGetCurrentUserId());
        await db.SaveChangesAsync();
        await ProcessAutomaticStepsAsync(item.Id, db, integrations, HttpContext.RequestAborted);

        return NoContent();
    }

    [HttpPost("{id:guid}/retry-integration")]
    public async Task<ActionResult<InstanceDto>> RetryIntegration(
        Guid id,
        [FromServices] AppDbContext db,
        [FromServices] IIntegrationExecutionService integrations)
    {
        var item = await LoadInstance(db).SingleOrDefaultAsync(x => x.Id == id);
        if (item is null)
        {
            return NotFound();
        }

        await ProcessAutomaticStepsAsync(item.Id, db, integrations, HttpContext.RequestAborted, forceFailedCurrent: true);

        var reloaded = await LoadInstance(db).AsNoTracking().SingleAsync(x => x.Id == id);
        return Ok(ToDto(reloaded));
    }

    private Guid? TryGetCurrentUserId()
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)
            ? userId
            : null;
    }

    private static void CompleteCurrentStep(FlowInstance item, StepExecution current, string? notes, Guid? completedByUserId)
    {
        var now = DateTime.UtcNow;
        current.Status = StepStatus.Completed;
        current.CompletedAt = now;
        current.Notes = notes;
        current.CompletedByUserId = completedByUserId;

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
            next.Status = next.Status == StepStatus.Failed ? StepStatus.InProgress : StepStatus.InProgress;
            next.StartedAt ??= now;
            item.CurrentStepOrder = next.FlowStep.Order;
        }

        item.UpdatedAt = now;
    }

    private static IQueryable<FlowInstance> LoadInstance(AppDbContext db)
    {
        return db.FlowInstances
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Tokens)
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep);
    }

    private static async Task ProcessAutomaticStepsAsync(
        Guid instanceId,
        AppDbContext db,
        IIntegrationExecutionService integrations,
        CancellationToken cancellationToken,
        bool forceFailedCurrent = false)
    {
        while (true)
        {
            var item = await LoadInstance(db).SingleAsync(x => x.Id == instanceId, cancellationToken);
            if (item.Status != InstanceStatus.InProgress)
            {
                return;
            }

            var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress || (forceFailedCurrent && x.Status == StepStatus.Failed));
            if (current is null)
            {
                return;
            }

            if (current.Status == StepStatus.Failed && forceFailedCurrent)
            {
                current.Status = StepStatus.InProgress;
            }

            var currentData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.DataJson) ?? [];
            var stepType = current.FlowStep.Type;

            if (stepType == StepType.Automatic)
            {
                CompleteCurrentStep(item, current, "Etapa automatica concluida pelo sistema.", null);
                await db.SaveChangesAsync(cancellationToken);
                forceFailedCurrent = false;
                continue;
            }

            if (stepType != StepType.ApiSend && stepType != StepType.ApiQuery)
            {
                return;
            }

            var result = await integrations.ExecuteAsync(item.FlowDefinition, current.FlowStep, currentData, cancellationToken, item, current, IntegrationTriggerType.Runtime);
            if (!result.Success)
            {
                current.Status = StepStatus.Failed;
                current.Notes = result.ErrorMessage ?? "Falha na integracao.";
                item.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            CompleteCurrentStep(item, current, "Etapa de integracao concluida automaticamente.", null);
            await db.SaveChangesAsync(cancellationToken);
            forceFailedCurrent = false;
        }
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
