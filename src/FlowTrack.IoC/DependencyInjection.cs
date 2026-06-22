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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using UglyToad.PdfPig;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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
        services.AddHttpClient<IIntegrationExecutionService, IntegrationExecutionService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
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

internal sealed class IntegrationExecutionService(HttpClient httpClient, AppDbContext db) : IIntegrationExecutionService
{
    public async Task<IntegrationTestResponse> ExecuteAsync(
        FlowDefinition flow,
        FlowStep step,
        Dictionary<string, JsonElement> data,
        CancellationToken cancellationToken,
        FlowInstance? instance = null,
        StepExecution? stepExecution = null,
        IntegrationTriggerType triggerType = IntegrationTriggerType.Runtime)
    {
        var config = string.IsNullOrWhiteSpace(step.ConfigurationJson)
            ? null
            : JsonSerializer.Deserialize<StepApiConfigDto>(step.ConfigurationJson);

        if (config is null || string.IsNullOrWhiteSpace(config.Url))
        {
            return await SaveAttemptAsync(step, triggerType, "GET", "", false, null, 0, null, "Etapa sem configuracao de integracao.", instance, stepExecution, cancellationToken);
        }

        var method = ResolveMethod(step.Type, config.Method);
        var resolvedUrl = ResolveTemplate(config.Url, data);
        if (step.Type == StepType.ApiQuery && !string.IsNullOrWhiteSpace(config.QueryTemplate))
        {
            resolvedUrl = $"{resolvedUrl}{ResolveTemplate(config.QueryTemplate, data)}";
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), resolvedUrl);
        ApplyToken(flow, config, resolvedUrl, request);

        if (step.Type == StepType.ApiSend)
        {
            request.Content = JsonContent.Create(data);
        }

        var watch = Stopwatch.StartNew();

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            watch.Stop();

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var preview = Truncate(responseText);
            var success = response.IsSuccessStatusCode;

            return await SaveAttemptAsync(
                step,
                triggerType,
                method,
                resolvedUrl,
                success,
                (int)response.StatusCode,
                (int)watch.ElapsedMilliseconds,
                preview,
                success ? null : $"Resposta HTTP {(int)response.StatusCode}.",
                instance,
                stepExecution,
                cancellationToken);
        }
        catch (Exception ex)
        {
            watch.Stop();
            return await SaveAttemptAsync(
                step,
                triggerType,
                method,
                resolvedUrl,
                false,
                null,
                (int)watch.ElapsedMilliseconds,
                null,
                Truncate(ex.Message, 400),
                instance,
                stepExecution,
                cancellationToken);
        }
    }

    private static string ResolveMethod(StepType type, string? configuredMethod)
    {
        if (type == StepType.ApiQuery)
        {
            return "GET";
        }

        if (string.Equals(configuredMethod, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            return "PUT";
        }

        return "POST";
    }

    private static string ResolveTemplate(string template, Dictionary<string, JsonElement> data)
    {
        var result = template;
        foreach (var entry in data)
        {
            result = result.Replace($"{{{{{entry.Key}}}}}", entry.Value.ToString(), StringComparison.OrdinalIgnoreCase);
            result = result.Replace($"{{{{steps.current.fields.{entry.Key}}}}}", entry.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string Truncate(string? value, int max = 1200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    private void ApplyToken(FlowDefinition flow, StepApiConfigDto config, string resolvedUrl, HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(config.TokenName))
        {
            return;
        }

        var token = flow.Tokens.FirstOrDefault(x => x.Active && string.Equals(x.Name, config.TokenName, StringComparison.OrdinalIgnoreCase));
        if (token is null || string.IsNullOrWhiteSpace(token.Value))
        {
            return;
        }

        if (token.Type == TokenType.Bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            return;
        }

        var header = string.IsNullOrWhiteSpace(token.HeaderName) ? "X-API-Key" : token.HeaderName;
        request.Headers.TryAddWithoutValidation(header, token.Value);
    }

    private async Task<IntegrationTestResponse> SaveAttemptAsync(
        FlowStep step,
        IntegrationTriggerType triggerType,
        string method,
        string url,
        bool success,
        int? statusCode,
        int durationMs,
        string? preview,
        string? error,
        FlowInstance? instance,
        StepExecution? stepExecution,
        CancellationToken cancellationToken)
    {
        var attempt = new IntegrationAttempt
        {
            FlowInstanceId = instance?.Id,
            FlowStepId = step.Id,
            StepExecutionId = stepExecution?.Id,
            TriggerType = triggerType,
            Method = method,
            Url = Truncate(url, 1900),
            Success = success,
            ResponseStatusCode = statusCode,
            DurationMs = durationMs,
            ResponsePreview = Truncate(preview),
            ErrorMessage = string.IsNullOrWhiteSpace(error) ? null : Truncate(error, 900)
        };

        db.IntegrationAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        return new IntegrationTestResponse(success, statusCode, durationMs, attempt.Url, method, attempt.ResponsePreview, attempt.ErrorMessage);
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
