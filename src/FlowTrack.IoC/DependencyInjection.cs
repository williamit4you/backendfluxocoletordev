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
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITokenProtectionService, TokenProtectionService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IPlatformConfigurationService, PlatformConfigurationService>();
        services.AddScoped<IFileStorageService, MinioFileStorageService>();
        services.AddScoped<IFlowManagementService, FlowManagementService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IInstanceManagementService, InstanceManagementService>();
        services.AddScoped<IInstanceAutomationService, InstanceAutomationService>();
        services.AddSingleton<IWorkerMonitor, WorkerMonitor>();
        services.AddHostedService<ApiQueryWorker>();
        services.AddHostedService<AutomaticStartWorker>();
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
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                    var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Principal?.FindFirstValue("sub");
                    if (!Guid.TryParse(userId, out var parsedUserId))
                    {
                        context.Fail("Token invalido.");
                        return;
                    }

                    var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == parsedUserId);
                    if (user is null || !user.Active)
                    {
                        context.Fail("Usuario inativo.");
                    }
                }
            };
        });
        services.AddAuthorization();
        return services;
    }
}

internal sealed class IntegrationExecutionService(
    HttpClient httpClient,
    AppDbContext db,
    ITokenProtectionService tokenProtection,
    IConfiguration appConfig,
    ILogger<IntegrationExecutionService> logger) : IIntegrationExecutionService
{
    public async Task<IntegrationExecutionResult> ExecuteAsync(
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
            return await SaveAttemptAsync(step, triggerType, "GET", "", false, null, 0, null, "Etapa sem configuracao de integracao.", null, instance, stepExecution, cancellationToken);
        }

        var method = ResolveMethod(step.Type, config.Method);
        var resolvedUrl = ResolveTemplate(config.Url, data);
        if (step.Type == StepType.ApiQuery && !string.IsNullOrWhiteSpace(config.QueryTemplate))
        {
            resolvedUrl = $"{resolvedUrl}{ResolveTemplate(config.QueryTemplate, data)}";
        }

        var validationError = await ValidateDestinationAsync(resolvedUrl, appConfig, cancellationToken);
        if (validationError is not null)
        {
            logger.LogWarning("Destino de integracao bloqueado para a etapa {StepId}: {Error}", step.Id, validationError);
            return await SaveAttemptAsync(step, triggerType, method, SanitizeUrl(resolvedUrl), false, null, 0, null, validationError, null, instance, stepExecution, cancellationToken);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), resolvedUrl);
        ApplyToken(flow, config, request);

        if (step.Type == StepType.ApiSend)
        {
            request.Content = JsonContent.Create(BuildApiSendPayload(config, data));
        }

        var watch = Stopwatch.StartNew();

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            watch.Stop();

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var preview = Truncate(responseText);
            var success = response.IsSuccessStatusCode;
            var mappedData = success && step.Type == StepType.ApiQuery
                ? MapResponseData(config, responseText)
                : null;

            return await SaveAttemptAsync(
                step,
                triggerType,
                method,
                SanitizeUrl(resolvedUrl),
                success,
                (int)response.StatusCode,
                (int)watch.ElapsedMilliseconds,
                preview,
                success ? null : $"Resposta HTTP {(int)response.StatusCode}.",
                mappedData,
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
                SanitizeUrl(resolvedUrl),
                false,
                null,
                (int)watch.ElapsedMilliseconds,
                null,
                Truncate(ex.Message, 400),
                null,
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

    private static Dictionary<string, JsonElement> BuildApiSendPayload(StepApiConfigDto config, Dictionary<string, JsonElement> data)
    {
        var selectedKeys = config.SendFieldKeys?
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (config.BodyMappings is not null && config.BodyMappings.Count > 0)
        {
            return BuildCustomApiSendPayload(config.BodyMappings, data);
        }

        if (selectedKeys is null || selectedKeys.Count == 0)
        {
            return data;
        }

        return data
            .Where(entry => selectedKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JsonElement> BuildCustomApiSendPayload(IReadOnlyList<BodyFieldMappingDto> mappings, Dictionary<string, JsonElement> data)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.TargetKey) || string.IsNullOrWhiteSpace(mapping.SourceReference))
            {
                continue;
            }

            SetNestedValue(payload, mapping.TargetKey.Trim(), ConvertJsonElementToObject(ResolveSourceReference(mapping.SourceReference.Trim(), data)));
        }

        return payload.ToDictionary(entry => entry.Key, entry => JsonSerializer.SerializeToElement(entry.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static void SetNestedValue(Dictionary<string, object?> target, string path, object? value)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return;
        }

        Dictionary<string, object?> current = target;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (!current.TryGetValue(segment, out var existing) || existing is not Dictionary<string, object?> nested)
            {
                nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                current[segment] = nested;
            }

            current = nested;
        }

        current[segments[^1]] = value;
    }

    private static object? ConvertJsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => JsonSerializer.Deserialize<object>(element.GetRawText())
        };
    }

    private static JsonElement ResolveSourceReference(string sourceReference, Dictionary<string, JsonElement> data)
    {
        var exactMatch = Regex.Match(sourceReference, @"^\{\{(?<key>[^}]+)\}\}$");
        if (exactMatch.Success)
        {
            var key = exactMatch.Groups["key"].Value.Trim();
            if (data.TryGetValue(key, out var jsonValue))
            {
                return jsonValue;
            }

            return JsonSerializer.SerializeToElement(string.Empty);
        }

        var resolvedText = sourceReference;
        foreach (var entry in data)
        {
            resolvedText = resolvedText.Replace($"{{{{{entry.Key}}}}}", entry.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return JsonSerializer.SerializeToElement(resolvedText);
    }

    private static Dictionary<string, JsonElement>? MapResponseData(StepApiConfigDto config, string responseText)
    {
        if (config.ResponseMappings is null || config.ResponseMappings.Count == 0 || string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        using var document = JsonDocument.Parse(responseText);
        var mapped = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in config.ResponseMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.FieldKey) || string.IsNullOrWhiteSpace(mapping.ResponsePath))
            {
                continue;
            }

            if (TryResolveJsonPath(document.RootElement, mapping.ResponsePath.Trim(), out var resolved))
            {
                mapped[mapping.FieldKey.Trim()] = resolved.Clone();
            }
        }

        return mapped.Count == 0 ? null : mapped;
    }

    private static bool TryResolveJsonPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var property))
            {
                value = property;
                continue;
            }

            if (value.ValueKind == JsonValueKind.Array
                && int.TryParse(segment.Trim('[', ']'), out var index)
                && index >= 0
                && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }

            value = default;
            return false;
        }

        return true;
    }

    private void ApplyToken(FlowDefinition flow, StepApiConfigDto config, HttpRequestMessage request)
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

        var plainValue = tokenProtection.Unprotect(token.Value);

        if (token.Type == TokenType.Bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plainValue);
            return;
        }

        var header = string.IsNullOrWhiteSpace(token.HeaderName) ? "X-API-Key" : token.HeaderName;
        request.Headers.TryAddWithoutValidation(header, plainValue);
    }

    private async Task<string?> ValidateDestinationAsync(string url, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "URL de integracao invalida.";
        }

        var allowedHosts = (configuration["Integrations:AllowedHosts"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isAllowListed = allowedHosts.Contains(uri.Host);
        if (!isAllowListed && uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Somente destinos HTTPS sao permitidos fora da allowlist.";
        }

        if (IsLocalHost(uri.Host))
        {
            return isAllowListed ? null : "Destino local ou loopback nao permitido.";
        }

        try
        {
            IPAddress[] addresses;
            if (IPAddress.TryParse(uri.Host, out var directIp))
            {
                addresses = [directIp];
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            }

            foreach (var address in addresses)
            {
                if (!isAllowListed && IsPrivateAddress(address))
                {
                    return "Destino privado/interno bloqueado por protecao SSRF.";
                }
            }
        }
        catch
        {
            if (!isAllowListed)
            {
                return "Nao foi possivel validar o destino da integracao.";
            }
        }

        return null;
    }

    private static bool IsLocalHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return Truncate(value, 1900);
        }

        return uri.GetLeftPart(UriPartial.Path);
    }

    private async Task<IntegrationExecutionResult> SaveAttemptAsync(
        FlowStep step,
        IntegrationTriggerType triggerType,
        string method,
        string url,
        bool success,
        int? statusCode,
        int durationMs,
        string? preview,
        string? error,
        Dictionary<string, JsonElement>? mappedData,
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

        return new IntegrationExecutionResult(success, statusCode, durationMs, attempt.Url, method, attempt.ResponsePreview, attempt.ErrorMessage, mappedData);
    }
}

internal sealed class TokenProtectionService(IConfiguration configuration) : ITokenProtectionService
{
    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(configuration["TokenEncryption:Key"] ?? configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Chave de criptografia nao configurada.")));

    public string Protect(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return string.Empty;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintextBytes, cipherBytes, tag);

        return Convert.ToBase64String([.. nonce, .. tag, .. cipherBytes]);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return string.Empty;
        }

        var payload = Convert.FromBase64String(protectedText);
        var nonce = payload[..12];
        var tag = payload[12..28];
        var cipher = payload[28..];
        var plaintext = new byte[cipher.Length];

        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}

internal sealed class AuditService(AppDbContext db) : IAuditService
{
    public async Task WriteAsync(string category, string action, string entityType, Guid entityId, string summary, Guid? actorUserId, CancellationToken cancellationToken)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            ActorUserId = actorUserId,
            Category = category,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class WorkerMonitor : IWorkerMonitor
{
    public DateTime? LastRunAtUtc { get; private set; }
    public DateTime? LastSuccessAtUtc { get; private set; }
    public string? LastError { get; private set; }

    public void MarkRun() => LastRunAtUtc = DateTime.UtcNow;
    public void MarkSuccess()
    {
        LastSuccessAtUtc = DateTime.UtcNow;
        LastError = null;
    }
    public void MarkFailure(string error)
    {
        LastError = error;
        LastRunAtUtc = DateTime.UtcNow;
    }
}

internal sealed class InstanceAutomationService(
    AppDbContext db,
    IIntegrationExecutionService integrations,
    IConfiguration appConfig,
    ILogger<InstanceAutomationService> logger) : IInstanceAutomationService
{
    public async Task ProcessAsync(Guid instanceId, CancellationToken cancellationToken, bool forceFailedCurrent = false)
    {
        while (true)
        {
            var item = await LoadInstance().SingleAsync(x => x.Id == instanceId, cancellationToken);
            if (item.Status != InstanceStatus.InProgress)
            {
                return;
            }

            var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress || (forceFailedCurrent && x.Status == StepStatus.Failed));
            if (current is null)
            {
                return;
            }

            if (current.Status == StepStatus.Failed && forceFailedCurrent)
            {
                current.Status = StepStatus.InProgress;
            }

            var currentData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.DataJson) ?? [];
            var stepType = current.FlowStep.Type;

            if (stepType == StepType.Automatic)
            {
                CompleteCurrentStep(item, current, "Etapa automatica concluida pelo sistema.", null);
                await db.SaveChangesAsync(cancellationToken);
                forceFailedCurrent = false;
                continue;
            }

            if (stepType != StepType.ApiSend && stepType != StepType.ApiQuery)
            {
                return;
            }

            if (stepType == StepType.ApiQuery)
            {
                var config = string.IsNullOrWhiteSpace(current.FlowStep.ConfigurationJson)
                    ? null
                    : JsonSerializer.Deserialize<StepApiConfigDto>(current.FlowStep.ConfigurationJson);
                var scheduleTimeZone = ScheduleRuntimeHelper.ResolveTimeZone(appConfig["Scheduling:TimeZoneId"]);

                if (config is not null
                    && !string.Equals(config.ScheduleMode, "manual", StringComparison.OrdinalIgnoreCase)
                    && !ScheduleRuntimeHelper.IsDue(config, null, current.StartedAt ?? DateTime.UtcNow, DateTime.UtcNow, timeZone: scheduleTimeZone))
                {
                    return;
                }
            }

            var result = await integrations.ExecuteAsync(item.FlowDefinition, current.FlowStep, currentData, cancellationToken, item, current, IntegrationTriggerType.Runtime);
            MergeIntegrationResultIntoExecutionData(currentData, result);

            if (stepType == StepType.ApiQuery && result.MappedData is not null)
            {
                foreach (var mapped in result.MappedData)
                {
                    currentData[mapped.Key] = mapped.Value;
                }
            }

            current.DataJson = JsonSerializer.Serialize(currentData);
            item.DataJson = JsonSerializer.Serialize(currentData);

            if (!result.Success)
            {
                logger.LogWarning("Falha de integracao na instancia {InstanceId}, etapa {StepId}: {Error}", item.Id, current.FlowStepId, result.ErrorMessage);
                current.Status = StepStatus.Failed;
                current.Notes = result.ErrorMessage ?? "Falha na integracao.";
                item.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            CompleteCurrentStep(item, current, "Etapa de integracao concluida automaticamente.", null);
            await db.SaveChangesAsync(cancellationToken);
            forceFailedCurrent = false;
        }
    }

    private IQueryable<FlowInstance> LoadInstance()
    {
        return db.FlowInstances
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Tokens)
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep);
    }

    private static void CompleteCurrentStep(FlowInstance item, StepExecution current, string? notes, Guid? completedByUserId)
    {
        var now = DateTime.UtcNow;
        current.Status = StepStatus.Completed;
        current.CompletedAt = now;
        current.Notes = notes;
        current.CompletedByUserId = completedByUserId;

        var next = item.StepExecutions
            .Where(x => x.FlowStep.Order > current.FlowStep.Order)
            .OrderBy(x => x.FlowStep.Order)
            .FirstOrDefault();

        if (next is null)
        {
            item.Status = InstanceStatus.Completed;
        }
        else
        {
            next.Status = StepStatus.InProgress;
            next.StartedAt ??= now;
            item.CurrentStepOrder = next.FlowStep.Order;
        }

        item.UpdatedAt = now;
    }

    private static void MergeIntegrationResultIntoExecutionData(Dictionary<string, JsonElement> currentData, IntegrationExecutionResult result)
    {
        currentData["_integration.success"] = JsonSerializer.SerializeToElement(result.Success);
        currentData["_integration.method"] = JsonSerializer.SerializeToElement(result.Method);
        currentData["_integration.url"] = JsonSerializer.SerializeToElement(result.Url);
        currentData["_integration.durationMs"] = JsonSerializer.SerializeToElement(result.DurationMs);
        currentData["_integration.executedAtUtc"] = JsonSerializer.SerializeToElement(DateTime.UtcNow);

        if (result.StatusCode.HasValue)
        {
            currentData["_integration.statusCode"] = JsonSerializer.SerializeToElement(result.StatusCode.Value);
        }

        if (!string.IsNullOrWhiteSpace(result.ResponsePreview))
        {
            currentData["_integration.responsePreview"] = JsonSerializer.SerializeToElement(result.ResponsePreview);
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            currentData["_integration.errorMessage"] = JsonSerializer.SerializeToElement(result.ErrorMessage);
        }
    }
}

internal sealed class ApiQueryWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration appConfig,
    IWorkerMonitor workerMonitor,
    ILogger<ApiQueryWorker> logger) : BackgroundService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Gate.WaitAsync(stoppingToken);
            try
            {
                workerMonitor.MarkRun();
                await ProcessDueQueriesAsync(stoppingToken);
                workerMonitor.MarkSuccess();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no worker de consultas agendadas.");
                workerMonitor.MarkFailure(ex.Message);
            }
            finally
            {
                Gate.Release();
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcessDueQueriesAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IInstanceAutomationService>();
        var scheduleTimeZone = ScheduleRuntimeHelper.ResolveTimeZone(appConfig["Scheduling:TimeZoneId"]);

        var candidates = await db.FlowInstances
            .AsNoTracking()
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
            .Where(x => x.Status == InstanceStatus.InProgress)
            .ToListAsync(stoppingToken);

        foreach (var instance in candidates)
        {
            var current = instance.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress || x.Status == StepStatus.Failed);
            if (current is null || current.FlowStep.Type != StepType.ApiQuery)
            {
                continue;
            }

            var config = string.IsNullOrWhiteSpace(current.FlowStep.ConfigurationJson)
                ? null
                : JsonSerializer.Deserialize<StepApiConfigDto>(current.FlowStep.ConfigurationJson);

            if (config is null || !IsDue(config, db, instance.Id, current.FlowStepId, current.StartedAt, scheduleTimeZone))
            {
                continue;
            }

            await automation.ProcessAsync(instance.Id, stoppingToken, forceFailedCurrent: current.Status == StepStatus.Failed);
        }
    }

    private static bool IsDue(StepApiConfigDto config, AppDbContext db, Guid instanceId, Guid stepId, DateTime? stepStartedAtUtc, TimeZoneInfo timeZone)
    {
        var lastAttempt = db.IntegrationAttempts
            .AsNoTracking()
            .Where(x => x.FlowInstanceId == instanceId && x.FlowStepId == stepId && x.TriggerType == IntegrationTriggerType.Runtime)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();
        var reference = lastAttempt?.CreatedAt ?? stepStartedAtUtc ?? DateTime.UtcNow;
        return ScheduleRuntimeHelper.IsDue(config, lastAttempt?.CreatedAt, reference, DateTime.UtcNow, timeZone: timeZone);
    }
}

internal sealed class AutomaticStartWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration appConfig,
    ILogger<AutomaticStartWorker> logger) : BackgroundService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Gate.WaitAsync(stoppingToken);
            try
            {
                await ProcessDueStartsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no worker de inicio automatico de fluxos.");
            }
            finally
            {
                Gate.Release();
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcessDueStartsAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var instances = scope.ServiceProvider.GetRequiredService<IInstanceManagementService>();
        var scheduleTimeZone = ScheduleRuntimeHelper.ResolveTimeZone(appConfig["Scheduling:TimeZoneId"]);

        var flows = (await db.FlowDefinitions
            .AsNoTracking()
            .Include(x => x.Steps)
            .Where(x => x.Active)
            .ToListAsync(stoppingToken))
            .GroupBy(x => x.FlowKey)
            .Select(group => FlowRuntimeSelectionHelper.SelectEffectiveVersion(group))
            .Where(flow => flow is not null)
            .Cast<FlowDefinition>()
            .ToList();

        var now = DateTime.UtcNow;
        foreach (var flow in flows)
        {
            var firstStep = flow.Steps.OrderBy(step => step.Order).FirstOrDefault();
            if (firstStep is null || firstStep.Type != StepType.Automatic || string.IsNullOrWhiteSpace(firstStep.ConfigurationJson))
            {
                continue;
            }

            var config = JsonSerializer.Deserialize<StepApiConfigDto>(firstStep.ConfigurationJson);
            if (config is null)
            {
                continue;
            }

            var flowVersionIds = await db.FlowDefinitions
                .AsNoTracking()
                .Where(candidate => candidate.FlowKey == flow.FlowKey)
                .Select(candidate => candidate.Id)
                .ToListAsync(stoppingToken);

            var lastCreatedAt = await db.FlowInstances
                .AsNoTracking()
                .Where(instance => flowVersionIds.Contains(instance.FlowDefinitionId))
                .OrderByDescending(instance => instance.CreatedAt)
                .Select(instance => (DateTime?)instance.CreatedAt)
                .FirstOrDefaultAsync(stoppingToken);

            var reference = flow.PublishedAt ?? flow.CreatedAt;
            if (!ScheduleRuntimeHelper.IsDue(config, lastCreatedAt, reference, now, timeZone: scheduleTimeZone))
            {
                continue;
            }

            await instances.CreateAsync(new CreateInstanceRequest(flow.Id, null, []), null, stoppingToken);
            logger.LogInformation("Fluxo {FlowId} iniciado automaticamente pelo agendamento da etapa inicial.", flow.Id);
        }
    }
}

internal static class ScheduleRuntimeHelper
{
    public static bool IsDue(
        StepApiConfigDto config,
        DateTime? lastRunAtUtc,
        DateTime referenceUtc,
        DateTime nowUtc,
        bool allowImmediateFirstIntervalRun = false,
        TimeZoneInfo? timeZone = null)
    {
        if (string.Equals(config.ScheduleMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(config.ScheduleMode, "interval", StringComparison.OrdinalIgnoreCase))
        {
            var minutes = ParseIntervalMinutes(config.ScheduleValue);
            if (minutes <= 0)
            {
                return false;
            }

            if (lastRunAtUtc.HasValue)
            {
                return lastRunAtUtc.Value <= nowUtc.AddMinutes(-minutes);
            }

            return allowImmediateFirstIntervalRun || referenceUtc.AddMinutes(minutes) <= nowUtc;
        }

        if (!string.Equals(config.ScheduleMode, "cron", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var resolvedTimeZone = timeZone ?? TimeZoneInfo.Utc;
        var windowStart = lastRunAtUtc ?? referenceUtc;
        return HasCronOccurrenceBetween(config.ScheduleValue, windowStart, nowUtc, resolvedTimeZone);
    }

    public static TimeZoneInfo ResolveTimeZone(string? configuredId)
    {
        var candidates = new[]
        {
            configuredId,
            "America/Sao_Paulo",
            "E. South America Standard Time",
            TimeZoneInfo.Local.Id,
            TimeZoneInfo.Utc.Id
        };

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>())
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static int ParseIntervalMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var minutes) ? Math.Max(minutes, 1) : 0;
    }

    private static bool HasCronOccurrenceBetween(string? value, DateTime startExclusiveUtc, DateTime endInclusiveUtc, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            return false;
        }

        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startExclusiveUtc, timeZone);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(endInclusiveUtc, timeZone);
        var probe = new DateTime(startLocal.Year, startLocal.Month, startLocal.Day, startLocal.Hour, startLocal.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = new DateTime(endLocal.Year, endLocal.Month, endLocal.Day, endLocal.Hour, endLocal.Minute, 0, DateTimeKind.Unspecified);
        var minimumProbe = limit.AddDays(-32);
        if (probe < minimumProbe)
        {
            probe = minimumProbe;
        }

        while (probe <= limit)
        {
            if (MatchesCron(parts, probe))
            {
                return true;
            }

            probe = probe.AddMinutes(1);
        }

        return false;
    }

    private static bool MatchesCron(IReadOnlyList<string> parts, DateTime instantLocal)
    {
        return MatchesCronPart(parts[0], instantLocal.Minute, 0, 59)
            && MatchesCronPart(parts[1], instantLocal.Hour, 0, 23)
            && MatchesCronPart(parts[2], instantLocal.Day, 1, 31)
            && MatchesCronPart(parts[3], instantLocal.Month, 1, 12)
            && MatchesCronPart(parts[4], NormalizeDayOfWeek(instantLocal.DayOfWeek), 0, 7);
    }

    private static int NormalizeDayOfWeek(DayOfWeek dayOfWeek)
    {
        return dayOfWeek == DayOfWeek.Sunday ? 0 : (int)dayOfWeek;
    }

    private static bool MatchesCronPart(string expression, int value, int min, int max)
    {
        foreach (var token in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MatchesCronToken(token, value, min, max))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesCronToken(string token, int value, int min, int max)
    {
        if (token == "*")
        {
            return true;
        }

        var step = 1;
        var rangeExpression = token;
        if (token.Contains('/'))
        {
            var split = token.Split('/', 2, StringSplitOptions.TrimEntries);
            rangeExpression = split[0];
            if (!int.TryParse(split[1], out step) || step <= 0)
            {
                return false;
            }
        }

        var start = min;
        var end = max;
        if (rangeExpression != "*" && !string.IsNullOrWhiteSpace(rangeExpression))
        {
            if (rangeExpression.Contains('-'))
            {
                var bounds = rangeExpression.Split('-', 2, StringSplitOptions.TrimEntries);
                if (!int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end))
                {
                    return false;
                }
            }
            else if (!int.TryParse(rangeExpression, out start))
            {
                return false;
            }
            else
            {
                end = start;
            }
        }

        if (value < start || value > end)
        {
            return false;
        }

        return (value - start) % step == 0;
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
