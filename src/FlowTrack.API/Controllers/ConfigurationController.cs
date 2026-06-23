using System.Security.Claims;
using FlowTrack.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize(Roles = "SuperAdmin")]
[Route("api/configuration")]
public sealed class ConfigurationController(IPlatformConfigurationService configuration) : ControllerBase
{
    [HttpGet("minio")]
    public async Task<ActionResult<MinioConfigurationDto>> GetMinio()
    {
        return Ok(await configuration.GetMinioAsync(HttpContext.RequestAborted));
    }

    [HttpPut("minio")]
    public async Task<ActionResult<MinioConfigurationDto>> SaveMinio([FromBody] SaveMinioConfigurationRequest request)
    {
        try
        {
            return Ok(await configuration.SaveMinioAsync(request, TryGetCurrentUserId(), HttpContext.RequestAborted));
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
    }

    private Guid? TryGetCurrentUserId()
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)
            ? userId
            : null;
    }
}
