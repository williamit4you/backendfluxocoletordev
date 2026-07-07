using FlowTrack.Application;
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
}
