using FlowTrack.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize]
[Route("api/flows")]
public sealed class FlowsController(IFlowManagementService flows) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FlowDto>>> GetAll([FromQuery] string? scope)
    {
        var result = await flows.GetAllAsync(scope, HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FlowDto>> GetById(Guid id)
    {
        try
        {
            return Ok(await flows.GetByIdAsync(id, HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> Create([FromBody] SaveFlowRequest request)
    {
        try
        {
            var id = await flows.CreateAsync(request, TryGetCurrentUserId(), HttpContext.RequestAborted);
            return Created($"/api/flows/{id}", new { Id = id });
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> Update(Guid id, [FromBody] SaveFlowRequest request)
    {
        try
        {
            var updatedId = await flows.UpdateAsync(id, request, TryGetCurrentUserId(), HttpContext.RequestAborted);
            return Ok(new { Id = updatedId });
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
        catch (AppConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/draft")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> CreateDraft(Guid id)
    {
        try
        {
            var draftId = await flows.CreateDraftAsync(id, TryGetCurrentUserId(), HttpContext.RequestAborted);
            return Ok(new { Id = draftId });
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<object>> Publish(Guid id)
    {
        try
        {
            var publishedId = await flows.PublishAsync(id, TryGetCurrentUserId(), HttpContext.RequestAborted);
            return Ok(new { Id = publishedId });
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
        catch (AppConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/steps/{stepId:guid}/test-integration")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<IntegrationTestResponse>> TestIntegration(Guid id, Guid stepId, [FromBody] IntegrationTestRequest request)
    {
        try
        {
            return Ok(await flows.TestIntegrationAsync(id, stepId, request, HttpContext.RequestAborted));
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
    }

    private Guid? TryGetCurrentUserId()
    {
        return Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var userId)
            ? userId
            : null;
    }
}
