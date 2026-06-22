using FlowTrack.Application;
using FlowTrack.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.API.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        [FromServices] AppDbContext db,
        [FromServices] IPasswordService passwords,
        [FromServices] ITokenService tokens)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.AppUsers.SingleOrDefaultAsync(x => x.Email == email && x.Active);

        if (user is null || !passwords.Verify(user, user.PasswordHash, request.Password))
        {
            return Unauthorized();
        }

        return Ok(new LoginResponse(
            tokens.Create(user),
            new UserDto(user.Id, user.Name, user.Email, user.Role.ToString(), user.Active)));
    }
}
