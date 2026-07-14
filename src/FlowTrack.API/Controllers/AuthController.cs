using System.Security.Claims;
using FlowTrack.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowTrack.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
    {
        try
        {
            return Ok(await auth.LoginAsync(request, HttpContext.RequestAborted));
        }
        catch (AppForbiddenException)
        {
            return Unauthorized(new
            {
                message = "E-mail ou senha incorretos. Verifique seus dados e tente novamente."
            });
        }
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangeOwnPassword([FromBody] ChangeOwnPasswordRequest request)
    {
        try
        {
            var currentUserId = TryGetCurrentUserId();
            if (currentUserId is null)
            {
                return Unauthorized(new { message = "Sessão inválida." });
            }

            await auth.ChangeOwnPasswordAsync(currentUserId.Value, request, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
        catch (AppForbiddenException)
        {
            return Unauthorized(new { message = "Sessão inválida." });
        }
    }

    private Guid? TryGetCurrentUserId()
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)
            ? userId
            : null;
    }
}
