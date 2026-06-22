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
        [FromServices] AppDbContext db,
        [FromServices] IMapper mapper)
    {
        var flows = await LoadFlows(db)
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();

        return Ok(flows.Select(x => FlowDefinitionMapper.ToDto(x, mapper)).ToList());
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

        return Ok(FlowDefinitionMapper.ToDto(flow, mapper, includeTokenValues: true));
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

        var flow = new FlowDefinition();
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

        db.StepFieldOptions.RemoveRange(flow.Steps.SelectMany(x => x.Fields).SelectMany(x => x.Options));
        db.StepFields.RemoveRange(flow.Steps.SelectMany(x => x.Fields));
        db.FlowSteps.RemoveRange(flow.Steps);
        db.FlowTokens.RemoveRange(flow.Tokens);

        FlowDefinitionMapper.Apply(flow, request);
        await db.SaveChangesAsync();

        return Ok(new { flow.Id });
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
            return ValidationError(new Dictionary<string, string[]> { ["name"] = ["Nome do fluxo é obrigatório."] });
        }

        if (request.Steps.Count == 0)
        {
            return ValidationError(new Dictionary<string, string[]> { ["steps"] = ["Ao menos uma etapa é obrigatória."] });
        }

        var fieldKeys = request.Steps
            .SelectMany(x => x.Fields)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => x.Key.Trim().ToLowerInvariant())
            .ToList();

        if (fieldKeys.Count != fieldKeys.Distinct().Count())
        {
            return ValidationError(new Dictionary<string, string[]> { ["fields"] = ["As chaves dos campos devem ser únicas no fluxo inteiro."] });
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

    private ActionResult ValidationError(Dictionary<string, string[]> errors)
    {
        return BadRequest(new ValidationProblemDetails(errors));
    }
}
