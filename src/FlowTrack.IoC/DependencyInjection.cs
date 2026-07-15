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
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
    private static readonly JsonSerializerOptions RelaxedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
            return await SaveAttemptAsync(step, triggerType, "GET", "", false, null, 0, null, null, null, "Etapa sem configuracao de integracao.", null, false, null, null, null, instance, stepExecution, cancellationToken);
        }

        var method = ResolveMethod(step.Type, config.Method);
        var resolvedUrl = ResolveTemplate(config.Url, data);
        if (step.Type == StepType.ApiQuery && !string.IsNullOrWhiteSpace(config.QueryTemplate))
        {
            resolvedUrl = $"{resolvedUrl}{ResolveTemplate(config.QueryTemplate, data)}";
        }

        var unresolvedTemplateFields = FindUnresolvedTemplateFields(resolvedUrl);
        if (unresolvedTemplateFields.Count > 0)
        {
            var missingFields = string.Join(", ", unresolvedTemplateFields);
            return await SaveAttemptAsync(
                step,
                triggerType,
                method,
                SanitizeUrl(resolvedUrl),
                false,
                null,
                0,
                null,
                null,
                null,
                $"Campos obrigatorios para a integracao nao foram preenchidos: {missingFields}.",
                null,
                false,
                null,
                null,
                null,
                instance,
                stepExecution,
                cancellationToken);
        }

        var validationError = await ValidateDestinationAsync(resolvedUrl, appConfig, cancellationToken);
        if (validationError is not null)
        {
            logger.LogWarning("Destino de integracao bloqueado para a etapa {StepId}: {Error}", step.Id, validationError);
            return await SaveAttemptAsync(step, triggerType, method, SanitizeUrl(resolvedUrl), false, null, 0, null, null, null, validationError, null, false, null, null, null, instance, stepExecution, cancellationToken);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), resolvedUrl);
        ApplyToken(flow, config, request);
        ApplyHeaders(config, data, request);
        Dictionary<string, JsonElement>? apiSendPayload = null;

        if (step.Type == StepType.ApiSend)
        {
            apiSendPayload = BuildApiSendPayload(config, data);
            request.Content = JsonContent.Create(apiSendPayload, options: RelaxedJsonOptions);
        }

        var requestHeadersPreview = BuildRequestHeadersPreview(request);
        var requestBodyPreview = BuildRequestBodyPreview(apiSendPayload);

        var watch = Stopwatch.StartNew();

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            watch.Stop();

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var preview = responseText;
            var success = response.IsSuccessStatusCode;
            if (success && (step.Type == StepType.ApiQuery || step.Type == StepType.ApiSend))
            {
                logger.LogInformation(
                    "Antes do mapeamento da resposta. StepId={StepId}, Tipo={StepType}, ResponseMappings={ResponseMappings}, ResponsePreview={ResponsePreview}",
                    step.Id,
                    step.Type,
                    config.ResponseMappings is null
                        ? "[]"
                        : JsonSerializer.Serialize(config.ResponseMappings, RelaxedJsonOptions),
                    preview);
            }

            var mappedData = success && (step.Type == StepType.ApiQuery || step.Type == StepType.ApiSend)
                ? MapResponseData(config, responseText)
                : null;
            var attemptCount = await CountRuntimeAttemptsAsync(instance, step.Id, triggerType, cancellationToken) + 1;
            var nowUtc = DateTime.UtcNow;
            var ruleEvaluation = success && (step.Type == StepType.ApiQuery || step.Type == StepType.ApiSend)
                ? EvaluateResponseRule(config, responseText, attemptCount, nowUtc)
                : !success
                    ? EvaluateHttpErrorRule(config, response.StatusCode, responseText, attemptCount, nowUtc)
                    : null;
            var awaitingData = ruleEvaluation?.Action == "retry";
            var semanticSuccess = success
                ? ruleEvaluation?.Action != "fail"
                : ruleEvaluation?.Action == "advance";
            var semanticError = semanticSuccess
                ? null
                : ruleEvaluation?.Reason ?? $"Resposta HTTP {(int)response.StatusCode}.";
            var awaitingDataMessage = awaitingData
                ? ruleEvaluation?.Reason
                : null;

            if ((step.Type == StepType.ApiQuery || step.Type == StepType.ApiSend) && !success && awaitingData)
            {
                logger.LogInformation(
                    "Resposta HTTP fora de 2xx ficara em nova tentativa. StepId={StepId}, StatusCode={StatusCode}, RetryAfterMinutes={RetryAfterMinutes}",
                    step.Id,
                    (int)response.StatusCode,
                    ruleEvaluation?.RetryIntervalMinutes ?? 3);
            }
            else if (success && (step.Type == StepType.ApiQuery || step.Type == StepType.ApiSend))
            {
                if (awaitingData)
                {
                    logger.LogInformation(
                        "Retorno vazio detectado e a etapa ficara aguardando nova tentativa. StepId={StepId}, RetryAfterMinutes={RetryAfterMinutes}",
                        step.Id,
                        config.EmptyArrayRetryMinutes ?? 3);
                }
                else if (mappedData is null || mappedData.Count == 0)
                {
                    logger.LogWarning(
                        "Nenhum valor foi capturado no mapeamento da resposta. StepId={StepId}, Tipo={StepType}, ResponseMappings={ResponseMappings}",
                        step.Id,
                        step.Type,
                        config.ResponseMappings is null
                            ? "[]"
                            : JsonSerializer.Serialize(config.ResponseMappings, RelaxedJsonOptions));
                }
                else
                {
                    logger.LogInformation(
                        "Depois do mapeamento da resposta. StepId={StepId}, Tipo={StepType}, MappedData={MappedData}",
                        step.Id,
                        step.Type,
                        JsonSerializer.Serialize(mappedData, RelaxedJsonOptions));
                }
            }

            return await SaveAttemptAsync(
                step,
                triggerType,
                method,
                SanitizeUrl(resolvedUrl),
                semanticSuccess,
                (int)response.StatusCode,
                (int)watch.ElapsedMilliseconds,
                requestHeadersPreview,
                requestBodyPreview,
                preview,
                semanticError,
                mappedData,
                awaitingData,
                awaitingDataMessage,
                ruleEvaluation?.RetryIntervalMinutes,
                ruleEvaluation,
                instance,
                stepExecution,
                cancellationToken);
        }
        catch (Exception ex)
        {
            watch.Stop();
            var attemptCount = await CountRuntimeAttemptsAsync(instance, step.Id, triggerType, cancellationToken) + 1;
            var transportRuleEvaluation = EvaluateTransportErrorRule(config, ex.Message, attemptCount, DateTime.UtcNow);
            var shouldAwaitTransportRetry = transportRuleEvaluation?.Action == "retry";
            var shouldAdvanceAfterTransportError = transportRuleEvaluation?.Action == "advance";

            return await SaveAttemptAsync(
                step,
                triggerType,
                method,
                SanitizeUrl(resolvedUrl),
                shouldAdvanceAfterTransportError,
                null,
                (int)watch.ElapsedMilliseconds,
                requestHeadersPreview,
                requestBodyPreview,
                null,
                Truncate(ex.Message, 400),
                null,
                shouldAwaitTransportRetry,
                shouldAwaitTransportRetry ? transportRuleEvaluation?.Reason : null,
                shouldAwaitTransportRetry ? transportRuleEvaluation?.RetryIntervalMinutes : null,
                transportRuleEvaluation,
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
        return ResolveTemplateText(template, data);
    }

    private static List<string> FindUnresolvedTemplateFields(string value)
    {
        return Regex.Matches(value ?? string.Empty, @"\{\{\s*(?<key>[^}]+?)\s*\}\}")
            .Select(match => match.Groups["key"].Value.Trim())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        if (!string.IsNullOrWhiteSpace(config.BodyTemplate))
        {
            return BuildApiSendPayloadFromTemplate(config.BodyTemplate, data);
        }

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

    private static Dictionary<string, JsonElement> BuildApiSendPayloadFromTemplate(string bodyTemplate, Dictionary<string, JsonElement> data)
    {
        var resolvedTemplate = ResolveBodyTemplate(bodyTemplate, data);

        using var document = JsonDocument.Parse(resolvedTemplate);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("O body customizado precisa ser um JSON com objeto na raiz.");
        }

        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveBodyTemplate(string template, Dictionary<string, JsonElement> data)
    {
        var normalizedTemplate = NormalizeTemplateText(template);

        normalizedTemplate = Regex.Replace(
            normalizedTemplate,
            "\"\\{\\{(?<key>[^}]+)\\}\\}\"",
            match =>
            {
                var key = match.Groups["key"].Value.Trim();
                return TryResolveTemplateValue(key, data, out var value)
                    ? value.GetRawText()
                    : match.Value;
            },
            RegexOptions.IgnoreCase);

        return ResolveTemplateText(normalizedTemplate, data);
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
            if (TryResolveTemplateValue(key, data, out var jsonValue))
            {
                return jsonValue;
            }

            return JsonSerializer.SerializeToElement(string.Empty);
        }

        return JsonSerializer.SerializeToElement(ResolveTemplateText(sourceReference, data));
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

    private async Task<int> CountRuntimeAttemptsAsync(FlowInstance? instance, Guid stepId, IntegrationTriggerType triggerType, CancellationToken cancellationToken)
    {
        if (instance is null || triggerType != IntegrationTriggerType.Runtime)
        {
            return 0;
        }

        return await db.IntegrationAttempts
            .AsNoTracking()
            .CountAsync(x => x.FlowInstanceId == instance.Id && x.FlowStepId == stepId && x.TriggerType == IntegrationTriggerType.Runtime, cancellationToken);
    }

    private static ResponseRuleEvaluationDto? EvaluateResponseRule(StepApiConfigDto config, string responseText, int attemptCount, DateTime nowUtc)
    {
        var rule = BuildRuntimeResponseRule(config);
        if (!rule.Enabled)
        {
            return null;
        }

        var targetPath = string.IsNullOrWhiteSpace(rule.TargetPath) ? "$" : rule.TargetPath.Trim();
        var expectedType = NormalizeExpectedType(rule.ExpectedType);
        var mode = NormalizeRuleMode(rule.Mode);
        var emptyBehavior = NormalizeRuleBehavior(rule.EmptyBehavior, "advance");
        var nonEmptyBehavior = NormalizeRuleBehavior(rule.NonEmptyBehavior, "advance");
        var failureBehavior = NormalizeRuleBehavior(rule.FailureBehavior, "fail");

        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (!TryResolveResponseRulePath(document.RootElement, targetPath, out var selected))
            {
                return BuildRuleFailure(rule, targetPath, expectedType, attemptCount, $"Caminho '{targetPath}' nao encontrado na resposta.", failureBehavior, nowUtc);
            }

            if (mode == "condition")
            {
                return EvaluateConditionResponseRule(rule, selected, targetPath, expectedType, attemptCount, nowUtc);
            }

            var isEmpty = IsResponseValueEmpty(selected, expectedType);
            var action = isEmpty ? emptyBehavior : nonEmptyBehavior;
            if (action == "retry" && rule.MaxAttempts.HasValue && attemptCount >= rule.MaxAttempts.Value)
            {
                return new ResponseRuleEvaluationDto(
                    true,
                    "failed",
                    "fail",
                    $"Limite de {rule.MaxAttempts.Value} tentativa(s) atingido com retorno vazio em {targetPath}.",
                    targetPath,
                    expectedType,
                    isEmpty,
                    attemptCount,
                    rule.MaxAttempts,
                    rule.RetryIntervalMinutes,
                    Mode: mode);
            }

            if (action == "retry")
            {
                var retryMinutes = Math.Clamp(rule.RetryIntervalMinutes ?? 3, 1, 10080);
                return new ResponseRuleEvaluationDto(
                    true,
                    "waiting",
                    "retry",
                    $"Retorno vazio em {targetPath}. Nova tentativa em {retryMinutes} minuto(s).",
                    targetPath,
                    expectedType,
                    isEmpty,
                    attemptCount,
                    rule.MaxAttempts,
                    retryMinutes,
                    nowUtc.AddMinutes(retryMinutes),
                    Mode: mode);
            }

            if (action == "fail")
            {
                return new ResponseRuleEvaluationDto(
                    true,
                    "failed",
                    "fail",
                    isEmpty ? $"Retorno vazio em {targetPath}." : $"Regra configurada para falhar quando {targetPath} tiver conteudo.",
                    targetPath,
                    expectedType,
                    isEmpty,
                    attemptCount,
                    rule.MaxAttempts,
                    rule.RetryIntervalMinutes,
                    Mode: mode);
            }

            return new ResponseRuleEvaluationDto(
                true,
                "matched",
                "advance",
                isEmpty ? $"Retorno vazio em {targetPath} aceito pela regra." : $"Retorno com conteudo encontrado em {targetPath}.",
                targetPath,
                expectedType,
                isEmpty,
                attemptCount,
                rule.MaxAttempts,
                rule.RetryIntervalMinutes,
                Mode: mode);
        }
        catch (JsonException)
        {
            return BuildRuleFailure(rule, targetPath, expectedType, attemptCount, "Resposta nao e um JSON valido.", failureBehavior, nowUtc);
        }
    }

    private static ResponseRuleEvaluationDto EvaluateConditionResponseRule(ResponseRuleDto rule, JsonElement selected, string targetPath, string expectedType, int attemptCount, DateTime nowUtc)
    {
        var operatorName = NormalizeConditionOperator(rule.Operator, expectedType);
        var expectedValue = rule.ExpectedValue ?? string.Empty;
        var actualValue = JsonElementToComparableText(selected, expectedType);
        var matched = EvaluateCondition(selected, actualValue, expectedValue, expectedType, operatorName, rule.CaseSensitive ?? false);
        var action = NormalizeRuleBehavior(matched ? rule.OnMatchBehavior : rule.OnMismatchBehavior, matched ? "advance" : "retry");
        var comparisonText = BuildConditionText(targetPath, operatorName, expectedValue);

        if (action == "retry" && rule.MaxAttempts.HasValue && attemptCount >= rule.MaxAttempts.Value)
        {
            return new ResponseRuleEvaluationDto(
                true,
                "failed",
                "fail",
                $"Limite de {rule.MaxAttempts.Value} tentativa(s) atingido aguardando {comparisonText}. Ultimo valor: {actualValue}.",
                targetPath,
                expectedType,
                IsResponseValueEmpty(selected, expectedType),
                attemptCount,
                rule.MaxAttempts,
                rule.RetryIntervalMinutes,
                Mode: "condition",
                Operator: operatorName,
                ActualValue: actualValue,
                ExpectedValue: expectedValue,
                Matched: matched);
        }

        if (action == "retry")
        {
            var retryMinutes = Math.Clamp(rule.RetryIntervalMinutes ?? 3, 1, 10080);
            return new ResponseRuleEvaluationDto(
                true,
                "waiting",
                "retry",
                $"Condicao ainda nao atendida: {comparisonText}. Valor atual: {actualValue}. Nova tentativa em {retryMinutes} minuto(s).",
                targetPath,
                expectedType,
                IsResponseValueEmpty(selected, expectedType),
                attemptCount,
                rule.MaxAttempts,
                retryMinutes,
                nowUtc.AddMinutes(retryMinutes),
                "condition",
                operatorName,
                actualValue,
                expectedValue,
                matched);
        }

        if (action == "fail")
        {
            return new ResponseRuleEvaluationDto(
                true,
                "failed",
                "fail",
                matched
                    ? $"Condicao atendida e configurada para falhar: {comparisonText}."
                    : $"Condicao nao atendida: {comparisonText}. Valor atual: {actualValue}.",
                targetPath,
                expectedType,
                IsResponseValueEmpty(selected, expectedType),
                attemptCount,
                rule.MaxAttempts,
                rule.RetryIntervalMinutes,
                Mode: "condition",
                Operator: operatorName,
                ActualValue: actualValue,
                ExpectedValue: expectedValue,
                Matched: matched);
        }

        return new ResponseRuleEvaluationDto(
            true,
            "matched",
            "advance",
            matched
                ? $"Condicao atendida: {comparisonText}."
                : $"Condicao nao atendida, mas configurada para avancar: {comparisonText}. Valor atual: {actualValue}.",
            targetPath,
            expectedType,
            IsResponseValueEmpty(selected, expectedType),
            attemptCount,
            rule.MaxAttempts,
            rule.RetryIntervalMinutes,
            Mode: "condition",
            Operator: operatorName,
            ActualValue: actualValue,
            ExpectedValue: expectedValue,
            Matched: matched);
    }

    private static ResponseRuleEvaluationDto BuildRuleFailure(ResponseRuleDto rule, string targetPath, string expectedType, int attemptCount, string reason, string failureBehavior, DateTime nowUtc)
    {
        if (failureBehavior == "retry")
        {
            var retryMinutes = Math.Clamp(rule.RetryIntervalMinutes ?? 3, 1, 10080);
            return new ResponseRuleEvaluationDto(true, "waiting", "retry", reason, targetPath, expectedType, true, attemptCount, rule.MaxAttempts, retryMinutes, nowUtc.AddMinutes(retryMinutes), NormalizeRuleMode(rule.Mode));
        }

        if (failureBehavior == "advance")
        {
            return new ResponseRuleEvaluationDto(true, "matched", "advance", reason, targetPath, expectedType, true, attemptCount, rule.MaxAttempts, rule.RetryIntervalMinutes, Mode: NormalizeRuleMode(rule.Mode));
        }

        return new ResponseRuleEvaluationDto(true, "failed", "fail", reason, targetPath, expectedType, true, attemptCount, rule.MaxAttempts, rule.RetryIntervalMinutes, Mode: NormalizeRuleMode(rule.Mode));
    }

    private static ResponseRuleEvaluationDto? EvaluateTransportErrorRule(StepApiConfigDto config, string? errorMessage, int attemptCount, DateTime nowUtc)
    {
        var rule = BuildRuntimeResponseRule(config);
        var action = NormalizeRuleBehavior(rule.TransportErrorBehavior, "fail");
        var reason = $"Erro de transporte: {Truncate(errorMessage, 300)}";

        if (action == "retry")
        {
            var maxAttempts = Math.Clamp(rule.TransportMaxAttempts ?? rule.MaxAttempts ?? 20, 1, 100000);
            if (attemptCount >= maxAttempts)
            {
                return new ResponseRuleEvaluationDto(
                    true,
                    "failed",
                    "fail",
                    $"Limite de {maxAttempts} tentativa(s) atingido apos erro de transporte. Ultimo erro: {Truncate(errorMessage, 220)}.",
                    "$transport",
                    "any",
                    true,
                    attemptCount,
                    maxAttempts,
                    rule.TransportRetryIntervalMinutes ?? rule.RetryIntervalMinutes,
                    Mode: "transportError");
            }

            var retryMinutes = Math.Clamp(rule.TransportRetryIntervalMinutes ?? rule.RetryIntervalMinutes ?? 3, 1, 10080);
            return new ResponseRuleEvaluationDto(
                true,
                "waiting",
                "retry",
                $"{reason}. Nova tentativa em {retryMinutes} minuto(s).",
                "$transport",
                "any",
                true,
                attemptCount,
                maxAttempts,
                retryMinutes,
                nowUtc.AddMinutes(retryMinutes),
                Mode: "transportError");
        }

        if (action == "advance")
        {
            return new ResponseRuleEvaluationDto(
                true,
                "matched",
                "advance",
                $"{reason}. Etapa configurada para avancar mesmo sem resposta HTTP.",
                "$transport",
                "any",
                true,
                attemptCount,
                rule.TransportMaxAttempts,
                rule.TransportRetryIntervalMinutes,
                Mode: "transportError");
        }

        return new ResponseRuleEvaluationDto(
            true,
            "failed",
            "fail",
            reason,
            "$transport",
            "any",
            true,
            attemptCount,
            rule.TransportMaxAttempts,
            rule.TransportRetryIntervalMinutes,
            Mode: "transportError");
    }

    private static ResponseRuleEvaluationDto? EvaluateHttpErrorRule(StepApiConfigDto config, HttpStatusCode statusCode, string? responseText, int attemptCount, DateTime nowUtc)
    {
        var rule = BuildRuntimeResponseRule(config);
        var action = NormalizeRuleBehavior(rule.HttpErrorBehavior, "fail");
        var statusCodeNumber = (int)statusCode;
        var reason = $"Resposta HTTP {statusCodeNumber}.";
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            reason = $"{reason} Preview: {Truncate(responseText, 220)}";
        }

        if (action == "retry")
        {
            var maxAttempts = Math.Clamp(rule.HttpErrorMaxAttempts ?? rule.MaxAttempts ?? 20, 1, 100000);
            if (attemptCount >= maxAttempts)
            {
                return new ResponseRuleEvaluationDto(
                    true,
                    "failed",
                    "fail",
                    $"Limite de {maxAttempts} tentativa(s) atingido apos resposta HTTP {statusCodeNumber}.",
                    $"$status:{statusCodeNumber}",
                    "number",
                    true,
                    attemptCount,
                    maxAttempts,
                    rule.HttpErrorRetryIntervalMinutes ?? rule.RetryIntervalMinutes,
                    Mode: "httpError",
                    ActualValue: statusCodeNumber.ToString());
            }

            var retryMinutes = Math.Clamp(rule.HttpErrorRetryIntervalMinutes ?? rule.RetryIntervalMinutes ?? 3, 1, 10080);
            return new ResponseRuleEvaluationDto(
                true,
                "waiting",
                "retry",
                $"Resposta HTTP {statusCodeNumber}. Nova tentativa em {retryMinutes} minuto(s).",
                $"$status:{statusCodeNumber}",
                "number",
                true,
                attemptCount,
                maxAttempts,
                retryMinutes,
                nowUtc.AddMinutes(retryMinutes),
                Mode: "httpError",
                ActualValue: statusCodeNumber.ToString());
        }

        if (action == "advance")
        {
            return new ResponseRuleEvaluationDto(
                true,
                "matched",
                "advance",
                $"Resposta HTTP {statusCodeNumber}. Etapa configurada para avancar mesmo fora de 2xx.",
                $"$status:{statusCodeNumber}",
                "number",
                true,
                attemptCount,
                rule.HttpErrorMaxAttempts,
                rule.HttpErrorRetryIntervalMinutes,
                Mode: "httpError",
                ActualValue: statusCodeNumber.ToString());
        }

        return new ResponseRuleEvaluationDto(
            true,
            "failed",
            "fail",
            reason,
            $"$status:{statusCodeNumber}",
            "number",
            true,
            attemptCount,
            rule.HttpErrorMaxAttempts,
            rule.HttpErrorRetryIntervalMinutes,
            Mode: "httpError",
            ActualValue: statusCodeNumber.ToString());
    }

    private static ResponseRuleDto BuildRuntimeResponseRule(StepApiConfigDto config)
    {
        if (config.ResponseRule is not null)
        {
            return config.ResponseRule;
        }

        if (config.RetryOnEmptyArray)
        {
            return new ResponseRuleDto(true, "$", "array", "retry", "advance", config.EmptyArrayRetryMinutes ?? 3, 20, "fail", null);
        }

        return new ResponseRuleDto();
    }

    private static string NormalizeRuleMode(string? value)
    {
        return string.Equals(value?.Trim(), "condition", StringComparison.OrdinalIgnoreCase)
            ? "condition"
            : "emptyCheck";
    }

    private static string NormalizeRuleBehavior(string? value, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "advance" or "retry" or "fail" ? normalized : fallback;
    }

    private static string NormalizeExpectedType(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "array" or "object" or "string" or "number" or "boolean" or "any" ? normalized : "array";
    }

    private static string NormalizeConditionOperator(string? value, string expectedType)
    {
        var normalized = value?.Trim();
        if (IsValidConditionOperator(expectedType, normalized))
        {
            return normalized!;
        }

        return expectedType is "array" or "object" or "any" ? "isNotEmpty" : "equals";
    }

    private static bool IsValidConditionOperator(string expectedType, string? operatorName)
    {
        if (string.IsNullOrWhiteSpace(operatorName))
        {
            return false;
        }

        return expectedType switch
        {
            "string" => operatorName is "equals" or "notEquals" or "contains" or "notContains" or "startsWith" or "endsWith" or "isEmpty" or "isNotEmpty",
            "number" => operatorName is "equals" or "notEquals" or "greaterThan" or "greaterThanOrEqual" or "lessThan" or "lessThanOrEqual" or "isEmpty" or "isNotEmpty",
            "boolean" => operatorName is "equals" or "notEquals" or "isEmpty" or "isNotEmpty",
            "array" or "object" or "any" => operatorName is "isEmpty" or "isNotEmpty",
            _ => false
        };
    }

    private static bool TryResolveResponseRulePath(JsonElement root, string path, out JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "$")
        {
            value = root;
            return true;
        }

        return TryResolveJsonPath(root, path.Trim().TrimStart('$').TrimStart('.'), out value);
    }

    private static bool IsResponseValueEmpty(JsonElement value, string expectedType)
    {
        return expectedType switch
        {
            "array" => value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0,
            "object" => value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Any(),
            "string" => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || string.IsNullOrWhiteSpace(value.ToString()),
            "number" => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || value.ValueKind != JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False),
            _ => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        };
    }

    private static bool EvaluateCondition(JsonElement selected, string actualValue, string expectedValue, string expectedType, string operatorName, bool caseSensitive)
    {
        if (operatorName == "isEmpty")
        {
            return IsResponseValueEmpty(selected, expectedType);
        }

        if (operatorName == "isNotEmpty")
        {
            return !IsResponseValueEmpty(selected, expectedType);
        }

        if (expectedType == "number")
        {
            var actualNumberOk = decimal.TryParse(actualValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var actualNumber);
            var expectedNumberOk = decimal.TryParse(expectedValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var expectedNumber);
            if (!actualNumberOk || !expectedNumberOk)
            {
                return false;
            }

            return operatorName switch
            {
                "equals" => actualNumber == expectedNumber,
                "notEquals" => actualNumber != expectedNumber,
                "greaterThan" => actualNumber > expectedNumber,
                "greaterThanOrEqual" => actualNumber >= expectedNumber,
                "lessThan" => actualNumber < expectedNumber,
                "lessThanOrEqual" => actualNumber <= expectedNumber,
                _ => false
            };
        }

        if (expectedType == "boolean")
        {
            var actualBoolOk = bool.TryParse(actualValue, out var actualBool);
            var expectedBoolOk = bool.TryParse(expectedValue, out var expectedBool);
            if (!actualBoolOk || !expectedBoolOk)
            {
                return false;
            }

            return operatorName switch
            {
                "equals" => actualBool == expectedBool,
                "notEquals" => actualBool != expectedBool,
                _ => false
            };
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return operatorName switch
        {
            "equals" => string.Equals(actualValue, expectedValue, comparison),
            "notEquals" => !string.Equals(actualValue, expectedValue, comparison),
            "contains" => actualValue.Contains(expectedValue, comparison),
            "notContains" => !actualValue.Contains(expectedValue, comparison),
            "startsWith" => actualValue.StartsWith(expectedValue, comparison),
            "endsWith" => actualValue.EndsWith(expectedValue, comparison),
            _ => false
        };
    }

    private static string JsonElementToComparableText(JsonElement value, string expectedType)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (expectedType == "string" && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (expectedType == "number" && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        if (expectedType == "boolean" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean().ToString();
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static string BuildConditionText(string targetPath, string operatorName, string expectedValue)
    {
        var operatorText = operatorName switch
        {
            "equals" => "igual a",
            "notEquals" => "diferente de",
            "contains" => "contendo",
            "notContains" => "nao contendo",
            "startsWith" => "comecando com",
            "endsWith" => "terminando com",
            "greaterThan" => "maior que",
            "greaterThanOrEqual" => "maior ou igual a",
            "lessThan" => "menor que",
            "lessThanOrEqual" => "menor ou igual a",
            "isEmpty" => "vazio",
            "isNotEmpty" => "preenchido",
            _ => operatorName
        };

        return operatorName is "isEmpty" or "isNotEmpty"
            ? $"{targetPath} {operatorText}"
            : $"{targetPath} {operatorText} {expectedValue}";
    }

    private static string? BuildRequestHeadersPreview(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in request.Headers)
        {
            headers[header.Key] = MaskHeaderValue(header.Key, string.Join(", ", header.Value));
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = MaskHeaderValue(header.Key, string.Join(", ", header.Value));
            }
        }

        return headers.Count == 0 ? null : Truncate(JsonSerializer.Serialize(headers, RelaxedJsonOptions), 4000);
    }

    private static string? BuildRequestBodyPreview(Dictionary<string, JsonElement>? payload)
    {
        return payload is null || payload.Count == 0
            ? null
            : Truncate(JsonSerializer.Serialize(payload, RelaxedJsonOptions), 6000);
    }

    private static string ResolveTemplateText(string template, Dictionary<string, JsonElement> data)
    {
        return Regex.Replace(
            NormalizeTemplateText(template),
            @"\{\{(?<key>[^}]+)\}\}",
            match =>
            {
                var key = match.Groups["key"].Value.Trim();
                return TryResolveTemplateValue(key, data, out var value)
                    ? value.ToString()
                    : match.Value;
            },
            RegexOptions.IgnoreCase);
    }

    private static bool TryResolveTemplateValue(string key, Dictionary<string, JsonElement> data, out JsonElement value)
    {
        if (data.TryGetValue(key, out value))
        {
            return true;
        }

        var normalizedKey = NormalizeLookupKey(key);
        foreach (var entry in data)
        {
            if (NormalizeLookupKey(entry.Key) == normalizedKey)
            {
                value = entry.Value;
                return true;
            }
        }

        foreach (var alias in ExpandLookupAliases(normalizedKey))
        {
            foreach (var entry in data)
            {
                if (NormalizeLookupKey(entry.Key) == alias)
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeLookupKey(string value)
    {
        return Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);
    }

    private static IEnumerable<string> ExpandLookupAliases(string normalizedKey)
    {
        yield return normalizedKey;

        if (normalizedKey is "cpfcnpjentidade" or "cpfcnpj" or "cnpjcpf" or "documento" or "documentoentidade")
        {
            yield return "cnpj";
            yield return "cpf";
            yield return "cpfcnpj";
            yield return "cpfcnpjentidade";
        }

        if (normalizedKey is "cnpj" or "cnpjemitente" or "emitentecnpj")
        {
            yield return "cnpj";
            yield return "cnpjemitente";
            yield return "emitentecnpj";
        }

        if (normalizedKey is "razaosocial" or "emitente" or "emitenterazaosocial")
        {
            yield return "razaosocial";
            yield return "emitente";
            yield return "emitenterazaosocial";
        }

        if (normalizedKey is "inscricaoestadual" or "ie" or "emitenteinscricaoestadual")
        {
            yield return "inscricaoestadual";
            yield return "ie";
            yield return "emitenteinscricaoestadual";
        }

        if (normalizedKey is "cep" or "cepemitente" or "emitentecep" or "cep2" or "destinatariocep")
        {
            yield return "cep";
            yield return "cepemitente";
            yield return "emitentecep";
            yield return "destinatariocep";
            yield return "cep2";
        }

        if (normalizedKey is "bairro" or "bairroemitente" or "emitentebairro" or "destinatariobairro")
        {
            yield return "bairro";
            yield return "bairroemitente";
            yield return "emitentebairro";
            yield return "destinatariobairro";
        }

        if (normalizedKey is "municipio" or "cidade" or "emitentemunicipio" or "destinatariomunicipio")
        {
            yield return "municipio";
            yield return "cidade";
            yield return "emitentemunicipio";
            yield return "destinatariomunicipio";
        }

        if (normalizedKey is "estado" or "uf" or "emitenteuf" or "destinatariouf")
        {
            yield return "estado";
            yield return "uf";
            yield return "emitenteuf";
            yield return "destinatariouf";
        }

        if (normalizedKey is "valortotal" or "valortotaldanota" or "totalnota")
        {
            yield return "valortotal";
            yield return "valortotaldanota";
            yield return "totalnota";
        }
    }

    private static string NormalizeTemplateText(string value)
    {
        return value
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"');
    }

    private static string MaskHeaderValue(string headerName, string value)
    {
        var normalized = headerName.Trim().ToLowerInvariant();
        var isSensitive = normalized.Contains("authorization")
            || normalized.Contains("token")
            || normalized.Contains("api-key")
            || normalized.Contains("apikey")
            || normalized.Contains("secret")
            || normalized.Contains("cookie");

        if (!isSensitive || string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (value.Length <= 8)
        {
            return "***";
        }

        return $"{value[..4]}***{value[^4..]}";
    }

    private static bool TryResolveJsonPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var normalizedPath = path.Trim();
        if (normalizedPath == "$")
        {
            return true;
        }

        normalizedPath = normalizedPath.TrimStart('$').TrimStart('.');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return true;
        }

        foreach (var segment in normalizedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind == JsonValueKind.Object && TryGetPropertyCaseInsensitive(value, segment, out var property))
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

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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

    private static void ApplyHeaders(StepApiConfigDto config, Dictionary<string, JsonElement> data, HttpRequestMessage request)
    {
        if (config.Headers is null || config.Headers.Count == 0)
        {
            return;
        }

        foreach (var header in config.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name) || string.IsNullOrWhiteSpace(header.Value))
            {
                continue;
            }

            var headerName = header.Name.Trim();
            var headerValue = ResolveTemplate(header.Value.Trim(), data);

            if (string.Equals(headerName, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.Remove(headerName);
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }
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
        string? requestHeaders,
        string? requestBody,
        string? preview,
        string? error,
        Dictionary<string, JsonElement>? mappedData,
        bool awaitingData,
        string? awaitingDataMessage,
        int? retryAfterMinutes,
        ResponseRuleEvaluationDto? responseRuleEvaluation,
        FlowInstance? instance,
        StepExecution? stepExecution,
        CancellationToken cancellationToken)
    {
        var createdAt = DateTime.UtcNow;
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
            CreatedAt = createdAt,
            RequestHeaders = Truncate(requestHeaders, 3900),
            RequestBody = Truncate(requestBody, 5900),
            ResponsePreview = string.IsNullOrWhiteSpace(preview) ? null : preview,
            ErrorMessage = string.IsNullOrWhiteSpace(error) ? null : Truncate(error, 900)
        };

        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""IntegrationAttempts""
                (""Id"", ""FlowInstanceId"", ""FlowStepId"", ""StepExecutionId"", ""TriggerType"", ""Method"", ""Url"", ""ResponseStatusCode"", ""Success"", ""DurationMs"", ""CreatedAt"", ""RequestHeaders"", ""RequestBody"", ""ResponsePreview"", ""ErrorMessage"")
                VALUES
                ({attempt.Id}, {attempt.FlowInstanceId}, {attempt.FlowStepId}, {attempt.StepExecutionId}, {(int)attempt.TriggerType}, {attempt.Method}, {attempt.Url}, {attempt.ResponseStatusCode}, {attempt.Success}, {attempt.DurationMs}, {attempt.CreatedAt}, {attempt.RequestHeaders}, {attempt.RequestBody}, {attempt.ResponsePreview}, {attempt.ErrorMessage})
            ", cancellationToken);
        }
        catch (Exception exception) when (IsUndefinedColumnException(exception))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""IntegrationAttempts""
                (""Id"", ""FlowInstanceId"", ""FlowStepId"", ""StepExecutionId"", ""TriggerType"", ""Method"", ""Url"", ""ResponseStatusCode"", ""Success"", ""DurationMs"", ""CreatedAt"", ""ResponsePreview"", ""ErrorMessage"")
                VALUES
                ({attempt.Id}, {attempt.FlowInstanceId}, {attempt.FlowStepId}, {attempt.StepExecutionId}, {(int)attempt.TriggerType}, {attempt.Method}, {attempt.Url}, {attempt.ResponseStatusCode}, {attempt.Success}, {attempt.DurationMs}, {attempt.CreatedAt}, {attempt.ResponsePreview}, {attempt.ErrorMessage})
            ", cancellationToken);
        }

        return new IntegrationExecutionResult(success, statusCode, durationMs, attempt.Url, method, attempt.RequestHeaders, attempt.RequestBody, attempt.ResponsePreview, attempt.ErrorMessage, mappedData, awaitingData, awaitingDataMessage, retryAfterMinutes, responseRuleEvaluation);
    }

    private static bool IsUndefinedColumnException(Exception exception)
    {
        return exception is PostgresException { SqlState: PostgresErrorCodes.UndefinedColumn }
            || exception.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.UndefinedColumn };
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

        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Nao foi possivel descriptografar o token. O valor salvo provavelmente foi protegido com outra chave. Confira TokenEncryption:Key ou Jwt:Secret do ambiente atual.",
                ex);
        }
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
                    && !IsApiQueryAttemptDue(config, db, item.Id, current.FlowStepId, current.StartedAt, scheduleTimeZone, currentData))
                {
                    return;
                }
            }

            var result = await integrations.ExecuteAsync(item.FlowDefinition, current.FlowStep, currentData, cancellationToken, item, current, IntegrationTriggerType.Runtime);
            MergeIntegrationResultIntoExecutionData(currentData, result);

            if ((stepType == StepType.ApiQuery || stepType == StepType.ApiSend) && result.MappedData is not null)
            {
                foreach (var mapped in result.MappedData)
                {
                    currentData[mapped.Key] = mapped.Value;
                }
            }

            current.DataJson = JsonSerializer.Serialize(currentData);
            item.DataJson = JsonSerializer.Serialize(currentData);

            if ((stepType == StepType.ApiQuery || stepType == StepType.ApiSend) && result.AwaitingData)
            {
                current.Notes = result.AwaitingDataMessage;
                item.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            if (!result.Success)
            {
                logger.LogWarning("Falha de integracao na instancia {InstanceId}, etapa {StepId}: {Error}", item.Id, current.FlowStepId, result.ErrorMessage);
                current.Status = StepStatus.Failed;
                current.Notes = result.ErrorMessage ?? "Falha na integracao.";
                item.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var completionNotes = result.ResponseRuleEvaluation?.Mode == "transportError" && result.ResponseRuleEvaluation.Action == "advance"
                ? result.ResponseRuleEvaluation.Reason
                : "Etapa de integracao concluida automaticamente.";
            CompleteCurrentStep(item, current, completionNotes, null);
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
        currentData.Remove("_integration.mappingWarning");
        currentData.Remove("_integration.mappingResult");
        currentData.Remove("_integration.awaitingData");
        currentData.Remove("_integration.awaitingDataMessage");
        currentData.Remove("_integration.emptyResultRetryMinutes");
        currentData.Remove("_integration.responseRule.status");
        currentData.Remove("_integration.responseRule.reason");
        currentData.Remove("_integration.responseRule.targetPath");
        currentData.Remove("_integration.responseRule.expectedType");
        currentData.Remove("_integration.responseRule.mode");
        currentData.Remove("_integration.responseRule.operator");
        currentData.Remove("_integration.responseRule.actualValue");
        currentData.Remove("_integration.responseRule.expectedValue");
        currentData.Remove("_integration.responseRule.matched");
        currentData.Remove("_integration.responseRule.retryIntervalMinutes");
        currentData.Remove("_integration.responseRule.attemptCount");
        currentData.Remove("_integration.responseRule.maxAttempts");
        currentData.Remove("_integration.responseRule.nextAttemptAtUtc");
        currentData["_integration.success"] = JsonSerializer.SerializeToElement(result.Success);
        currentData["_integration.method"] = JsonSerializer.SerializeToElement(result.Method);
        currentData["_integration.url"] = JsonSerializer.SerializeToElement(result.Url);
        currentData["_integration.durationMs"] = JsonSerializer.SerializeToElement(result.DurationMs);
        currentData["_integration.executedAtUtc"] = JsonSerializer.SerializeToElement(DateTime.UtcNow);

        if (result.StatusCode.HasValue)
        {
            currentData["_integration.statusCode"] = JsonSerializer.SerializeToElement(result.StatusCode.Value);
        }

        if (!string.IsNullOrWhiteSpace(result.RequestHeaders))
        {
            currentData["_integration.requestHeaders"] = JsonSerializer.SerializeToElement(result.RequestHeaders);
        }

        if (!string.IsNullOrWhiteSpace(result.RequestBody))
        {
            currentData["_integration.requestBody"] = JsonSerializer.SerializeToElement(result.RequestBody);
        }

        if (!string.IsNullOrWhiteSpace(result.ResponsePreview))
        {
            currentData["_integration.responsePreview"] = JsonSerializer.SerializeToElement(result.ResponsePreview);
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            currentData["_integration.errorMessage"] = JsonSerializer.SerializeToElement(result.ErrorMessage);
        }

        if (result.ResponseRuleEvaluation is not null)
        {
            currentData["_integration.responseRule.status"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.Status);
            currentData["_integration.responseRule.reason"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.Reason);
            currentData["_integration.responseRule.targetPath"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.TargetPath);
            currentData["_integration.responseRule.expectedType"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.ExpectedType);
            currentData["_integration.responseRule.attemptCount"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.AttemptCount);

            if (!string.IsNullOrWhiteSpace(result.ResponseRuleEvaluation.Mode))
            {
                currentData["_integration.responseRule.mode"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.Mode);
            }

            if (!string.IsNullOrWhiteSpace(result.ResponseRuleEvaluation.Operator))
            {
                currentData["_integration.responseRule.operator"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.Operator);
            }

            if (result.ResponseRuleEvaluation.ActualValue is not null)
            {
                currentData["_integration.responseRule.actualValue"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.ActualValue);
            }

            if (result.ResponseRuleEvaluation.ExpectedValue is not null)
            {
                currentData["_integration.responseRule.expectedValue"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.ExpectedValue);
            }

            if (result.ResponseRuleEvaluation.Matched.HasValue)
            {
                currentData["_integration.responseRule.matched"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.Matched.Value);
            }

            if (result.ResponseRuleEvaluation.MaxAttempts.HasValue)
            {
                currentData["_integration.responseRule.maxAttempts"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.MaxAttempts.Value);
            }

            if (result.ResponseRuleEvaluation.RetryIntervalMinutes.HasValue)
            {
                currentData["_integration.responseRule.retryIntervalMinutes"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.RetryIntervalMinutes.Value);
            }

            if (result.ResponseRuleEvaluation.NextAttemptAtUtc.HasValue)
            {
                currentData["_integration.responseRule.nextAttemptAtUtc"] = JsonSerializer.SerializeToElement(result.ResponseRuleEvaluation.NextAttemptAtUtc.Value);
            }
        }

        if (result.AwaitingData)
        {
            currentData["_integration.awaitingData"] = JsonSerializer.SerializeToElement(true);
            currentData["_integration.awaitingDataMessage"] = JsonSerializer.SerializeToElement(result.AwaitingDataMessage ?? "Consulta aguardando retorno com conteudo.");

            if (result.RetryAfterMinutes.HasValue)
            {
                currentData["_integration.emptyResultRetryMinutes"] = JsonSerializer.SerializeToElement(result.RetryAfterMinutes.Value);
            }
        }
        else if (result.MappedData is null || result.MappedData.Count == 0)
        {
            currentData["_integration.mappingWarning"] = JsonSerializer.SerializeToElement("Nenhum valor foi capturado no mapeamento da resposta.");
        }
        else
        {
            currentData["_integration.mappingResult"] = JsonSerializer.SerializeToElement(
                result.MappedData.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool IsApiQueryAttemptDue(
        StepApiConfigDto config,
        AppDbContext db,
        Guid instanceId,
        Guid stepId,
        DateTime? stepStartedAtUtc,
        TimeZoneInfo timeZone,
        Dictionary<string, JsonElement>? currentData = null)
    {
        if (currentData is not null && TryGetWaitingRuleNextAttempt(currentData, out var nextAttemptAtUtc))
        {
            return DateTime.UtcNow >= nextAttemptAtUtc;
        }

        if (string.Equals(config.ScheduleMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ScheduleRuntimeHelper.IsDue(config, null, stepStartedAtUtc ?? DateTime.UtcNow, DateTime.UtcNow, timeZone: timeZone);
    }

    private static bool TryGetWaitingRuleNextAttempt(Dictionary<string, JsonElement> data, out DateTime nextAttemptAtUtc)
    {
        nextAttemptAtUtc = default;
        if (!data.TryGetValue("_integration.responseRule.status", out var status)
            || !string.Equals(status.ToString(), "waiting", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (data.TryGetValue("_integration.responseRule.nextAttemptAtUtc", out var nextAttempt)
            && DateTime.TryParse(nextAttempt.ToString(), out nextAttemptAtUtc))
        {
            return true;
        }

        return false;
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
            var currentData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current?.DataJson ?? "{}") ?? [];
            var isApiQuery = current?.FlowStep.Type == StepType.ApiQuery;
            var isApiSendAwaitingData = current?.FlowStep.Type == StepType.ApiSend
                && TryGetWaitingRuleNextAttempt(currentData, out _);

            if (current is null || (!isApiQuery && !isApiSendAwaitingData))
            {
                continue;
            }

            var config = string.IsNullOrWhiteSpace(current.FlowStep.ConfigurationJson)
                ? null
                : JsonSerializer.Deserialize<StepApiConfigDto>(current.FlowStep.ConfigurationJson);

            if (config is null || !IsApiQueryAttemptDue(config, db, instance.Id, current.FlowStepId, current.StartedAt, scheduleTimeZone, currentData))
            {
                continue;
            }

            await automation.ProcessAsync(instance.Id, stoppingToken, forceFailedCurrent: current.Status == StepStatus.Failed);
        }
    }

    private static bool IsApiQueryAttemptDue(
        StepApiConfigDto config,
        AppDbContext db,
        Guid instanceId,
        Guid stepId,
        DateTime? stepStartedAtUtc,
        TimeZoneInfo timeZone,
        Dictionary<string, JsonElement>? currentData = null)
    {
        if (currentData is not null && TryGetWaitingRuleNextAttempt(currentData, out var nextAttemptAtUtc))
        {
            return DateTime.UtcNow >= nextAttemptAtUtc;
        }

        if (string.Equals(config.ScheduleMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ScheduleRuntimeHelper.IsDue(config, null, stepStartedAtUtc ?? DateTime.UtcNow, DateTime.UtcNow, timeZone: timeZone);
    }

    private static bool TryGetWaitingRuleNextAttempt(Dictionary<string, JsonElement> data, out DateTime nextAttemptAtUtc)
    {
        nextAttemptAtUtc = default;
        if (!data.TryGetValue("_integration.responseRule.status", out var status)
            || !string.Equals(status.ToString(), "waiting", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (data.TryGetValue("_integration.responseRule.nextAttemptAtUtc", out var nextAttempt)
            && DateTime.TryParse(nextAttempt.ToString(), out nextAttemptAtUtc))
        {
            return true;
        }

        return false;
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
