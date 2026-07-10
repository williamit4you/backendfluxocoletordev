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
    public sealed class UploadStepFileForm
    {
        public string FieldKey { get; set; } = string.Empty;
        public IFormFile File { get; set; } = default!;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InstanceDto>>> GetAll([FromQuery] Guid? flowId, [FromQuery] string? status, [FromQuery] string? search)
    {
        return Ok(await instances.GetAllAsync(flowId, status, search, TryGetCurrentUserId(), HttpContext.RequestAborted));
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardInstancesResultDto>> GetDashboard(
        [FromQuery] Guid flowId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? startDate = null)
    {
        return Ok(await instances.GetDashboardInstancesAsync(flowId, page, pageSize, status, search, startDate, TryGetCurrentUserId(), HttpContext.RequestAborted));
    }

    [HttpGet("pending-tasks")]
    public async Task<ActionResult<PagedResultDto<InstanceDto>>> GetPendingTasks([FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? search = null, [FromQuery] string? statusFilter = null)
    {
        return Ok(await instances.GetPendingTasksAsync(page, pageSize, search, statusFilter, TryGetCurrentUserId(), HttpContext.RequestAborted));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InstanceDto>> GetById(Guid id)
    {
        try
        {
            return Ok(await instances.GetByIdAsync(id, TryGetCurrentUserId(), HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
        catch (AppConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (AppForbiddenException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acesso negado." });
        }
    }

    [HttpGet("{id:guid}/steps/{stepExecutionId:guid}/integration-attempts")]
    public async Task<ActionResult<PagedIntegrationAttemptResultDto>> GetStepIntegrationAttempts(Guid id, Guid stepExecutionId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] string? statusCode = null)
    {
        try
        {
            return Ok(await instances.GetStepIntegrationAttemptsAsync(id, stepExecutionId, page, pageSize, statusCode, TryGetCurrentUserId(), HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<object>> Create([FromBody] CreateInstanceRequest request)
    {
        try
        {
            var id = await instances.CreateAsync(request, TryGetCurrentUserId(), HttpContext.RequestAborted);
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
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
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
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/save-step")]
    public async Task<ActionResult<InstanceDto>> SaveStep(Guid id, [FromBody] AdvanceStepRequest request)
    {
        try
        {
            return Ok(await instances.SaveCurrentStepDataAsync(id, request.Data ?? [], request.Notes, TryGetCurrentUserId(), HttpContext.RequestAborted));
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
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/upload")]
    [RequestSizeLimit(10_000_000)]
    public async Task<ActionResult<InstanceDto>> Upload(Guid id, [FromForm] UploadStepFileForm request)
    {
        try
        {
            await using var stream = request.File.OpenReadStream();
            return Ok(await instances.UploadCurrentStepFileAsync(id, request.FieldKey, request.File.FileName, request.File.ContentType, stream, TryGetCurrentUserId(), HttpContext.RequestAborted));
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
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/retry-integration")]
    public async Task<ActionResult<InstanceDto>> RetryIntegration(Guid id)
    {
        try
        {
            return Ok(await instances.RetryIntegrationAsync(id, TryGetCurrentUserId(), HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/steps/{stepExecutionId:guid}/reprocess")]
    public async Task<ActionResult<InstanceDto>> ReprocessStep(Guid id, Guid stepExecutionId)
    {
        try
        {
            return Ok(await instances.ReprocessStepAsync(id, stepExecutionId, TryGetCurrentUserId(), HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
        catch (AppConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/steps/{stepExecutionId:guid}/cancel-waiting")]
    public async Task<ActionResult<InstanceDto>> CancelWaitingStep(Guid id, Guid stepExecutionId)
    {
        try
        {
            return Ok(await instances.CancelWaitingStepAsync(id, stepExecutionId, TryGetCurrentUserId(), HttpContext.RequestAborted));
        }
        catch (AppNotFoundException)
        {
            return NotFound();
        }
        catch (AppConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (AppForbiddenException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
    }

    private Guid? TryGetCurrentUserId()
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)
            ? userId
            : null;
    }
}
