using System.Security.Claims;
using FlowTrack.Application;
using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize(Roles = "SuperAdmin,Admin")]
[Route("api/users")]
public sealed class UsersController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> GetAll([FromServices] AppDbContext db)
    {
        var users = await db.AppUsers
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new UserDto(x.Id, x.Name, x.Email, x.Role.ToString(), x.Active))
            .ToListAsync();

        return Ok(users);
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(
        [FromBody] CreateUserRequest request,
        [FromServices] AppDbContext db,
        [FromServices] IPasswordService passwords)
    {
        var validation = await ValidateAsync(request.Name, request.Email, request.Password, request.Role, db);
        if (validation is not null)
        {
            return validation;
        }

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
        {
            return ValidationError(new Dictionary<string, string[]> { ["role"] = ["Perfil inválido."] });
        }

        if (IsAdminTryingToGrantSuperAdmin(role))
        {
            return Forbid();
        }

        var user = new AppUser
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim().ToLowerInvariant(),
            Role = role,
            Active = true
        };

        user.PasswordHash = passwords.Hash(user, request.Password);
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();

        return Created($"/api/users/{user.Id}", new UserDto(user.Id, user.Name, user.Email, user.Role.ToString(), user.Active));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> Update(
        Guid id,
        [FromBody] UpdateUserRequest request,
        [FromServices] AppDbContext db,
        [FromServices] IPasswordService passwords)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Role))
        {
            return ValidationError(new Dictionary<string, string[]> { ["user"] = ["Nome e perfil são obrigatórios."] });
        }

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
        {
            return ValidationError(new Dictionary<string, string[]> { ["role"] = ["Perfil inválido."] });
        }

        if (IsAdminTryingToGrantSuperAdmin(role))
        {
            return Forbid();
        }

        var user = await db.AppUsers.SingleOrDefaultAsync(x => x.Id == id);
        if (user is null)
        {
            return NotFound();
        }

        user.Name = request.Name.Trim();
        user.Role = role;
        user.Active = request.Active;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length < 8)
            {
                return ValidationError(new Dictionary<string, string[]> { ["password"] = ["A senha deve ter ao menos 8 caracteres."] });
            }

            user.PasswordHash = passwords.Hash(user, request.Password);
        }

        await db.SaveChangesAsync();

        return Ok(new UserDto(user.Id, user.Name, user.Email, user.Role.ToString(), user.Active));
    }

    private async Task<ActionResult?> ValidateAsync(string name, string email, string password, string role, AppDbContext db)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(role))
        {
            return ValidationError(new Dictionary<string, string[]> { ["user"] = ["Nome, e-mail, senha e perfil são obrigatórios."] });
        }

        if (password.Length < 8)
        {
            return ValidationError(new Dictionary<string, string[]> { ["password"] = ["A senha deve ter ao menos 8 caracteres."] });
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (await db.AppUsers.AnyAsync(x => x.Email == normalizedEmail))
        {
            return Conflict(new { message = "Já existe um usuário com este e-mail." });
        }

        return null;
    }

    private bool IsAdminTryingToGrantSuperAdmin(UserRole role)
    {
        var currentRole = User.FindFirstValue(ClaimTypes.Role);
        return string.Equals(currentRole, UserRole.Admin.ToString(), StringComparison.OrdinalIgnoreCase)
            && role == UserRole.SuperAdmin;
    }

    private ActionResult ValidationError(Dictionary<string, string[]> errors)
    {
        return BadRequest(new ValidationProblemDetails(errors));
    }
}
