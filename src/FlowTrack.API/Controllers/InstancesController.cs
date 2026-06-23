using System.Security.Claims;
using FlowTrack.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize]
[Route("api/instances")]
public sealed class InstancesController(IInstanceManagementService instances) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InstanceDto>>> GetAll([FromQuery] Guid? flowId, [FromQuery] string? status, [FromQuery] string? search)
    {
        return Ok(await instances.GetAllAsync(flowId, status, search, HttpContext.RequestAborted));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InstanceDto>> GetById(Guid id)
    {
        try
        {
            return Ok(await instances.GetByIdAsync(id, HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<object>> Create([FromBody] CreateInstanceRequest request)
    {
        try
        {
            var id = await instances.CreateAsync(request, HttpContext.RequestAborted);
            return Created($"/api/instances/{id}", new { Id = id });
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

    [HttpPost("{id:guid}/advance")]
    public async Task<IActionResult> Advance(Guid id, [FromBody] AdvanceStepRequest request)
    {
        try
        {
            await instances.AdvanceAsync(id, request, TryGetCurrentUserId(), HttpContext.RequestAborted);
            return NoContent();
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

    [HttpPost("{id:guid}/retry-integration")]
    public async Task<ActionResult<InstanceDto>> RetryIntegration(Guid id)
    {
        try
        {
            return Ok(await instances.RetryIntegrationAsync(id, HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
    }

    private Guid? TryGetCurrentUserId()
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)
            ? userId
            : null;
    }
}
