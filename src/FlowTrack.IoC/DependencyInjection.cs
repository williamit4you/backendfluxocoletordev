using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using FlowTrack.Application;
using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using UglyToad.PdfPig;

namespace FlowTrack.IoC;

public static class DependencyInjection
{
    public static IServiceCollection AddFlowTrack(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection não configurada.");
        var secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret não configurado.");
        if (secret.Length < 32) throw new InvalidOperationException("Jwt:Secret deve ter no mínimo 32 caracteres.");
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddAutoMapper(cfg => cfg.AddProfile<MappingProfile>());
        services.AddScoped<IPasswordService, PasswordService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IPdfExtractionService, PdfExtractionService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ValidIssuer = "FlowTrack", ValidAudience = "FlowTrack.Web",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ClockSkew = TimeSpan.FromMinutes(1), RoleClaimType = ClaimTypes.Role
            };
        });
        services.AddAuthorization();
        return services;
    }
}

internal sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<AppUser> _hasher = new();
    public string Hash(AppUser user, string password) => _hasher.HashPassword(user, password);
    public bool Verify(AppUser user, string hash, string password) => _hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed;
}

internal sealed class TokenService(IConfiguration config) : ITokenService
{
    public string Create(AppUser user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Secret"]!));
        var token = new JwtSecurityToken("FlowTrack", "FlowTrack.Web", claims, expires: DateTime.UtcNow.AddHours(8), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

internal sealed class PdfExtractionService : IPdfExtractionService
{
    public Task<PdfExtractionDto> ExtractAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(); stream.CopyTo(memory); memory.Position = 0;
        using var pdf = PdfDocument.Open(memory);
        var text = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        var fields = new Dictionary<string, string>();
        Match(fields, "chaveAcesso", text, @"(?:CHAVE DE ACESSO|CHAVE)\D*((?:\d[ .-]*){44})", v => Regex.Replace(v, @"\D", ""));
        Match(fields, "numeroNfe", text, @"(?:N[ÚU]MERO|N[º°])\s*[:.]?\s*(\d{1,9})");
        Match(fields, "serie", text, @"S[ÉE]RIE\s*[:.]?\s*(\d{1,3})");
        Match(fields, "cnpjEmitente", text, @"CNPJ\s*[:.]?\s*(\d{2}\.?\d{3}\.?\d{3}/?\d{4}-?\d{2})");
        Match(fields, "valorTotal", text, @"VALOR TOTAL DA NOTA\s*([\d.,]+)");
        Match(fields, "dataEmissao", text, @"DATA (?:DE )?EMISS[ÃA]O\s*(\d{2}/\d{2}/\d{4})");
        var warnings = fields.Count == 0 ? new[] { "Nenhum campo reconhecido. Confirme se o PDF contém texto; arquivos escaneados exigem OCR." } : Array.Empty<string>();
        return Task.FromResult(new PdfExtractionDto(fields, warnings));
    }
    private static void Match(Dictionary<string, string> fields, string key, string text, string pattern, Func<string, string>? normalize = null)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (match.Success) fields[key] = normalize?.Invoke(match.Groups[1].Value.Trim()) ?? match.Groups[1].Value.Trim();
    }
}
