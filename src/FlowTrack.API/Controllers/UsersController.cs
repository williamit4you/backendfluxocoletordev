using System.Security.Claims;
using FlowTrack.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize(Roles = "SuperAdmin,Admin")]
[Route("api/users")]
public sealed class UsersController(IUserManagementService users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetAll()
    {
        return Ok(await users.GetAllAsync(User.FindFirstValue(ClaimTypes.Role), HttpContext.RequestAborted));
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserRequest request)
    {
        try
        {
            var user = await users.CreateAsync(request, User.FindFirstValue(ClaimTypes.Role), TryGetCurrentUserId(), HttpContext.RequestAborted);
            return Created($"/api/users/{user.Id}", user);
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
        catch (AppForbiddenException)
        {
            return Forbid();
        }
        catch (AppConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(Guid id, [FromBody] UpdateUserRequest request)
    {
        try
        {
            return Ok(await users.UpdateAsync(id, request, User.FindFirstValue(ClaimTypes.Role), TryGetCurrentUserId(), HttpContext.RequestAborted));
        }
        catch (AppValidationException ex)
        {
            return BadRequest(new ValidationProblemDetails(ex.Errors));
        }
        catch (AppForbiddenException)
        {
            return Forbid();
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

    private Guid? TryGetCurrentUserId()
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"), out var userId)
            ? userId
            : null;
    }
}
