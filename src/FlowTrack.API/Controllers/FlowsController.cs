using AutoMapper;
using FlowTrack.API.Infrastructure;
using FlowTrack.Application;
using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize]
[Route("api/flows")]
public sealed class FlowsController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FlowDto>>> GetAll(
        [FromQuery] string? scope,
        [FromServices] AppDbContext db,
        [FromServices] IMapper mapper)
    {
        var normalizedScope = string.Equals(scope, "builder", StringComparison.OrdinalIgnoreCase) ? "builder" : "runtime";
        var rows = await LoadFlows(db)
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenByDescending(x => x.VersionNumber)
            .ToListAsync();

        if (normalizedScope == "runtime")
        {
            var runtimeFlows = rows
                .Where(x => x.Active && x.LifecycleStatus == FlowLifecycleStatus.Published)
                .GroupBy(x => x.FlowKey)
                .Select(group => group.OrderByDescending(x => x.VersionNumber).First())
                .Select(flow => FlowDefinitionMapper.ToDto(flow, mapper))
                .ToList();

            return Ok(runtimeFlows);
        }

        var builderFlows = rows
            .GroupBy(x => x.FlowKey)
            .Select(group =>
            {
                var draft = group.FirstOrDefault(x => x.LifecycleStatus == FlowLifecycleStatus.Draft);
                var published = group.Where(x => x.LifecycleStatus == FlowLifecycleStatus.Published).OrderByDescending(x => x.VersionNumber).FirstOrDefault();
                var selected = draft ?? published ?? group.OrderByDescending(x => x.VersionNumber).First();
                return FlowDefinitionMapper.ToDto(selected, mapper, hasDraft: draft is not null && selected.Id != draft.Id ? true : draft is not null);
            })
            .OrderBy(x => x.Name)
            .ToList();

        return Ok(builderFlows);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FlowDto>> GetById(
        Guid id,
        [FromServices] AppDbContext db,
        [FromServices] IMapper mapper)
    {
        var flow = await LoadFlows(db).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (flow is null)
        {
            return NotFound();
        }

        var hasDraft = await db.FlowDefinitions.AnyAsync(x => x.FlowKey == flow.FlowKey && x.LifecycleStatus == FlowLifecycleStatus.Draft && x.Id != flow.Id);
        return Ok(FlowDefinitionMapper.ToDto(flow, mapper, includeTokenValues: true, hasDraft: hasDraft || flow.LifecycleStatus == FlowLifecycleStatus.Draft));
    }

    [HttpPost]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> Create([FromBody] SaveFlowRequest request, [FromServices] AppDbContext db)
    {
        var validation = Validate(request);
        if (validation is not null)
        {
            return validation;
        }

        var flow = new FlowDefinition
        {
            FlowKey = Guid.NewGuid(),
            VersionNumber = 1,
            LifecycleStatus = FlowLifecycleStatus.Draft
        };

        FlowDefinitionMapper.Apply(flow, request);

        db.FlowDefinitions.Add(flow);
        await db.SaveChangesAsync();

        return Created($"/api/flows/{flow.Id}", new { flow.Id });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> Update(Guid id, [FromBody] SaveFlowRequest request, [FromServices] AppDbContext db)
    {
        var validation = Validate(request);
        if (validation is not null)
        {
            return validation;
        }

        var flow = await LoadFlows(db).SingleOrDefaultAsync(x => x.Id == id);
        if (flow is null)
        {
            return NotFound();
        }

        if (flow.LifecycleStatus != FlowLifecycleStatus.Draft)
        {
            return Conflict(new { message = "Somente versões em rascunho podem ser alteradas. Gere um rascunho antes de editar." });
        }

        db.StepFieldOptions.RemoveRange(flow.Steps.SelectMany(x => x.Fields).SelectMany(x => x.Options));
        db.StepFields.RemoveRange(flow.Steps.SelectMany(x => x.Fields));
        db.FlowSteps.RemoveRange(flow.Steps);
        db.FlowTokens.RemoveRange(flow.Tokens);

        FlowDefinitionMapper.Apply(flow, request);
        await db.SaveChangesAsync();

        return Ok(new { flow.Id });
    }

    [HttpPost("{id:guid}/draft")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> CreateDraft(Guid id, [FromServices] AppDbContext db)
    {
        var source = await LoadFlows(db).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
        if (source is null)
        {
            return NotFound();
        }

        var existingDraft = await LoadFlows(db).SingleOrDefaultAsync(x => x.FlowKey == source.FlowKey && x.LifecycleStatus == FlowLifecycleStatus.Draft);
        if (existingDraft is not null)
        {
            return Ok(new { id = existingDraft.Id });
        }

        var nextVersion = await db.FlowDefinitions
            .Where(x => x.FlowKey == source.FlowKey)
            .MaxAsync(x => x.VersionNumber) + 1;

        var draft = CloneVersion(source, FlowLifecycleStatus.Draft, nextVersion);
        db.FlowDefinitions.Add(draft);
        await db.SaveChangesAsync();

        return Ok(new { id = draft.Id });
    }

    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> Publish(Guid id, [FromServices] AppDbContext db)
    {
        var draft = await LoadFlows(db).SingleOrDefaultAsync(x => x.Id == id);
        if (draft is null)
        {
            return NotFound();
        }

        if (draft.LifecycleStatus != FlowLifecycleStatus.Draft)
        {
            return Conflict(new { message = "Apenas rascunhos podem ser publicados." });
        }

        var validation = ValidateDraftForPublish(draft);
        if (validation is not null)
        {
            return validation;
        }

        var publishedVersions = await db.FlowDefinitions
            .Where(x => x.FlowKey == draft.FlowKey && x.LifecycleStatus == FlowLifecycleStatus.Published)
            .ToListAsync();

        foreach (var published in publishedVersions)
        {
            published.LifecycleStatus = FlowLifecycleStatus.Archived;
        }

        draft.LifecycleStatus = FlowLifecycleStatus.Published;
        draft.PublishedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return Ok(new { id = draft.Id });
    }

    [HttpPost("{id:guid}/steps/{stepId:guid}/test-integration")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<IntegrationTestResponse>> TestIntegration(
        Guid id,
        Guid stepId,
        [FromBody] IntegrationTestRequest request,
        [FromServices] AppDbContext db,
        [FromServices] IIntegrationExecutionService integrations)
    {
        var flow = await LoadFlows(db).SingleOrDefaultAsync(x => x.Id == id);
        if (flow is null)
        {
            return NotFound();
        }

        var step = flow.Steps.SingleOrDefault(x => x.Id == stepId);
        if (step is null)
        {
            return NotFound();
        }

        if (step.Type != StepType.ApiSend && step.Type != StepType.ApiQuery)
        {
            return BadRequest(new { message = "Esta etapa nao possui integracao de API para teste." });
        }

        var result = await integrations.ExecuteAsync(flow, step, request.Data, HttpContext.RequestAborted, triggerType: IntegrationTriggerType.Test);
        return Ok(result);
    }

    private static FlowDefinition CloneVersion(FlowDefinition source, FlowLifecycleStatus status, int versionNumber)
    {
        return new FlowDefinition
        {
            FlowKey = source.FlowKey,
            Name = source.Name,
            Description = source.Description,
            Active = source.Active,
            VersionNumber = versionNumber,
            LifecycleStatus = status,
            PublishedAt = status == FlowLifecycleStatus.Published ? DateTime.UtcNow : null,
            Tokens = source.Tokens.Select(token => new FlowToken
            {
                Name = token.Name,
                Value = token.Value,
                Type = token.Type,
                HeaderName = token.HeaderName,
                Active = token.Active
            }).ToList(),
            Steps = source.Steps
                .OrderBy(x => x.Order)
                .Select(step => new FlowStep
                {
                    Name = step.Name,
                    Description = step.Description,
                    Type = step.Type,
                    Order = step.Order,
                    AssignedUserId = step.AssignedUserId,
                    ConfigurationJson = step.ConfigurationJson,
                    Fields = step.Fields
                        .OrderBy(x => x.Order)
                        .Select(field => new StepField
                        {
                            Key = field.Key,
                            Label = field.Label,
                            Type = field.Type,
                            Required = field.Required,
                            Order = field.Order,
                            Options = field.Options
                                .OrderBy(x => x.Order)
                                .Select(option => new StepFieldOption
                                {
                                    Label = option.Label,
                                    Value = option.Value,
                                    Order = option.Order
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static IQueryable<FlowDefinition> LoadFlows(AppDbContext db)
    {
        return db.FlowDefinitions
            .Include(x => x.Tokens)
            .Include(x => x.Steps)
                .ThenInclude(x => x.Fields)
                    .ThenInclude(x => x.Options);
    }

    private ActionResult? Validate(SaveFlowRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ValidationError(new Dictionary<string, string[]> { ["name"] = ["Nome do fluxo e obrigatorio."] });
        }

        if (request.Steps.Count == 0)
        {
            return ValidationError(new Dictionary<string, string[]> { ["steps"] = ["Ao menos uma etapa e obrigatoria."] });
        }

        var fieldKeys = request.Steps
            .SelectMany(x => x.Fields)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => x.Key.Trim().ToLowerInvariant())
            .ToList();

        if (fieldKeys.Count != fieldKeys.Distinct().Count())
        {
            return ValidationError(new Dictionary<string, string[]> { ["fields"] = ["As chaves dos campos devem ser unicas no fluxo inteiro."] });
        }

        foreach (var step in request.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
            {
                return ValidationError(new Dictionary<string, string[]> { ["steps"] = ["Todas as etapas precisam de nome."] });
            }

            if ((step.Type == StepType.ApiSend || step.Type == StepType.ApiQuery) && string.IsNullOrWhiteSpace(step.ApiConfig?.Url))
            {
                return ValidationError(new Dictionary<string, string[]> { ["api"] = [$"A etapa '{step.Name}' precisa ter URL configurada."] });
            }
        }

        return null;
    }

    private ActionResult? ValidateDraftForPublish(FlowDefinition draft)
    {
        if (!draft.Steps.Any())
        {
            return ValidationError(new Dictionary<string, string[]> { ["steps"] = ["Nao e possivel publicar um fluxo sem etapas."] });
        }

        if (draft.Steps.Any(step => string.IsNullOrWhiteSpace(step.Name)))
        {
            return ValidationError(new Dictionary<string, string[]> { ["steps"] = ["Todas as etapas precisam estar nomeadas antes da publicacao."] });
        }

        return null;
    }

    private ActionResult ValidationError(Dictionary<string, string[]> errors)
    {
        return BadRequest(new ValidationProblemDetails(errors));
    }
}
