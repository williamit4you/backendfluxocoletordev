using System.Text.Json;
using System.Text.RegularExpressions;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.Application;

public sealed class FlowManagementService(
    IAppDbContext db,
    ITokenProtectionService tokenProtection,
    IIntegrationExecutionService integrations,
    IAuditService audit) : IFlowManagementService
{
    public async Task<IReadOnlyList<FlowDto>> GetAllAsync(string? scope, CancellationToken cancellationToken)
    {
        var normalizedScope = string.Equals(scope, "builder", StringComparison.OrdinalIgnoreCase) ? "builder" : "runtime";
        var rows = await LoadFlows()
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ThenByDescending(x => x.VersionNumber)
            .ToListAsync(cancellationToken);

        if (normalizedScope == "runtime")
        {
            return rows
                .Where(x => x.Active)
                .GroupBy(x => x.FlowKey)
                .Select(FlowRuntimeSelectionHelper.SelectEffectiveVersion)
                .Where(flow => flow is not null)
                .Select(flow => flow!)
                .Select(flow => ToDto(flow))
                .ToList();
        }

        return rows
            .GroupBy(x => x.FlowKey)
            .Select(group =>
            {
                var draft = group.FirstOrDefault(x => x.LifecycleStatus == FlowLifecycleStatus.Draft);
                var published = group.Where(x => x.LifecycleStatus == FlowLifecycleStatus.Published).OrderByDescending(x => x.VersionNumber).FirstOrDefault();
                var selected = draft ?? published ?? group.OrderByDescending(x => x.VersionNumber).First();
                return ToDto(selected, hasDraft: draft is not null && selected.Id != draft.Id ? true : draft is not null);
            })
            .OrderBy(x => x.Name)
            .ToList();
    }

    public async Task<FlowDto> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var flow = await LoadFlows().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo nao encontrado.");

        var hasDraft = await db.Flows.AnyAsync(x => x.FlowKey == flow.FlowKey && x.LifecycleStatus == FlowLifecycleStatus.Draft && x.Id != flow.Id, cancellationToken);
        return ToDto(flow, includeTokenValues: true, hasDraft: hasDraft || flow.LifecycleStatus == FlowLifecycleStatus.Draft);
    }

    public async Task<Guid> CreateAsync(SaveFlowRequest request, Guid? actorUserId, CancellationToken cancellationToken)
    {
        Validate(request);

        var flow = new FlowDefinition
        {
            FlowKey = Guid.NewGuid(),
            VersionNumber = 1,
            LifecycleStatus = FlowLifecycleStatus.Draft
        };

        Apply(flow, request);
        db.Add(flow);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("Flow", "CreateDraft", "FlowDefinition", flow.Id, $"Fluxo '{flow.Name}' criado em rascunho.", actorUserId, cancellationToken);
        return flow.Id;
    }

    public async Task<Guid> UpdateAsync(Guid id, SaveFlowRequest request, Guid? actorUserId, CancellationToken cancellationToken)
    {
        Validate(request);

        try
        {
            var dbContext = db as DbContext
                ?? throw new InvalidOperationException("O contexto de dados nao suporta operacoes transacionais para atualizar o fluxo.");
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var flow = await db.Flows.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new AppNotFoundException("Fluxo nao encontrado.");

            var flowHasExecutions = await db.Instances.AnyAsync(x => x.FlowDefinitionId == flow.Id, cancellationToken);
            if (flowHasExecutions)
            {
                if (flow.LifecycleStatus != FlowLifecycleStatus.Draft)
                {
                    throw new AppConflictException("Esta versao ja possui execucoes. Crie um rascunho para gerar uma nova versao sem alterar o historico.");
                }

                var nextVersion = await db.Flows
                    .Where(x => x.FlowKey == flow.FlowKey)
                    .MaxAsync(x => x.VersionNumber, cancellationToken) + 1;

                flow.Active = false;
                flow.LifecycleStatus = FlowLifecycleStatus.Archived;

                var replacementDraft = new FlowDefinition
                {
                    FlowKey = flow.FlowKey,
                    VersionNumber = nextVersion,
                    LifecycleStatus = FlowLifecycleStatus.Draft
                };

                Apply(replacementDraft, request);
                db.Add(replacementDraft);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await audit.WriteAsync("Flow", "UpdateDraft", "FlowDefinition", replacementDraft.Id, $"Novo rascunho v{replacementDraft.VersionNumber} criado para preservar execucoes anteriores de '{replacementDraft.Name}'.", actorUserId, cancellationToken);
                return replacementDraft.Id;
            }

            await ClearFlowDefinitionChildrenAsync(dbContext, flow.Id, cancellationToken);
            dbContext.ChangeTracker.Clear();

            flow = await db.Flows.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new AppNotFoundException("Fluxo nao encontrado.");

            Apply(flow, request);
            MarkFlowDefinitionChildrenAsAdded(dbContext, flow);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await audit.WriteAsync("Flow", "UpdateDraft", "FlowDefinition", flow.Id, $"Rascunho '{flow.Name}' atualizado.", actorUserId, cancellationToken);
            return flow.Id;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new AppConflictException("O rascunho foi alterado por outra operacao durante o salvamento. Reabra o fluxo e tente salvar novamente.", ex);
        }
    }

    private static async Task ClearFlowDefinitionChildrenAsync(DbContext dbContext, Guid flowId, CancellationToken cancellationToken)
    {
        var stepIds = await dbContext.Set<FlowStep>()
            .AsNoTracking()
            .Where(x => x.FlowDefinitionId == flowId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (stepIds.Count > 0)
        {
            var fieldIds = await dbContext.Set<StepField>()
                .AsNoTracking()
                .Where(x => stepIds.Contains(x.FlowStepId))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            if (fieldIds.Count > 0)
            {
                dbContext.RemoveRange(await dbContext.Set<StepFieldOption>()
                    .Where(x => fieldIds.Contains(x.StepFieldId))
                    .ToListAsync(cancellationToken));
                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
            }

            dbContext.RemoveRange(await dbContext.Set<StepField>()
                .Where(x => stepIds.Contains(x.FlowStepId))
                .ToListAsync(cancellationToken));
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            dbContext.RemoveRange(await dbContext.Set<FlowStepUser>()
                .Where(x => stepIds.Contains(x.FlowStepId))
                .ToListAsync(cancellationToken));
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            dbContext.RemoveRange(await dbContext.Set<FlowStep>()
                .Where(x => x.FlowDefinitionId == flowId)
                .ToListAsync(cancellationToken));
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }

        dbContext.RemoveRange(await dbContext.Set<FlowToken>()
            .Where(x => x.FlowDefinitionId == flowId)
            .ToListAsync(cancellationToken));
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        dbContext.RemoveRange(await dbContext.Set<FlowDefinitionUser>()
            .Where(x => x.FlowDefinitionId == flowId)
            .ToListAsync(cancellationToken));
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private static void MarkFlowDefinitionChildrenAsAdded(DbContext dbContext, FlowDefinition flow)
    {
        foreach (var token in flow.Tokens)
        {
            dbContext.Entry(token).State = EntityState.Added;
        }

        foreach (var assignment in flow.AssignedUsers)
        {
            dbContext.Entry(assignment).State = EntityState.Added;
        }

        foreach (var step in flow.Steps)
        {
            dbContext.Entry(step).State = EntityState.Added;

            foreach (var stepAssignment in step.AssignedUsers)
            {
                dbContext.Entry(stepAssignment).State = EntityState.Added;
            }

            foreach (var field in step.Fields)
            {
                dbContext.Entry(field).State = EntityState.Added;

                foreach (var option in field.Options)
                {
                    dbContext.Entry(option).State = EntityState.Added;
                }
            }
        }
    }

    public async Task<Guid> CreateDraftAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var source = await LoadFlows().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo nao encontrado.");

        var existingDrafts = await LoadFlows()
            .Where(x => x.FlowKey == source.FlowKey && x.LifecycleStatus == FlowLifecycleStatus.Draft)
            .ToListAsync(cancellationToken);
        var existingDraftIds = existingDrafts.Select(draft => draft.Id).ToList();
        var draftIdsWithExecutions = await db.Instances
            .Where(x => existingDraftIds.Contains(x.FlowDefinitionId))
            .Select(x => x.FlowDefinitionId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var existingDraft = existingDrafts.FirstOrDefault(x => !draftIdsWithExecutions.Contains(x.Id));
        if (existingDraft is not null)
        {
            return existingDraft.Id;
        }

        var nextVersion = await db.Flows
            .Where(x => x.FlowKey == source.FlowKey)
            .MaxAsync(x => x.VersionNumber, cancellationToken) + 1;

        var draft = CloneVersion(source, FlowLifecycleStatus.Draft, nextVersion);
        db.Add(draft);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("Flow", "CreateDraftFromPublished", "FlowDefinition", draft.Id, $"Rascunho v{draft.VersionNumber} criado a partir de '{draft.Name}'.", actorUserId, cancellationToken);
        return draft.Id;
    }

    public async Task<Guid> PublishAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var draft = await LoadFlows().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo nao encontrado.");

        if (draft.LifecycleStatus != FlowLifecycleStatus.Draft)
        {
            throw new AppConflictException("Apenas rascunhos podem ser publicados.");
        }

        ValidateDraftForPublish(draft);

        var publishedVersions = await db.Flows
            .Where(x => x.FlowKey == draft.FlowKey && x.LifecycleStatus == FlowLifecycleStatus.Published)
            .ToListAsync(cancellationToken);

        foreach (var published in publishedVersions)
        {
            published.LifecycleStatus = FlowLifecycleStatus.Archived;
        }

        draft.LifecycleStatus = FlowLifecycleStatus.Published;
        draft.PublishedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("Flow", "Publish", "FlowDefinition", draft.Id, $"Fluxo '{draft.Name}' publicado na versao {draft.VersionNumber}.", actorUserId, cancellationToken);
        return draft.Id;
    }

    public async Task<IntegrationTestResponse> TestIntegrationAsync(Guid flowId, Guid stepId, IntegrationTestRequest request, CancellationToken cancellationToken)
    {
        var flow = await LoadFlows().SingleOrDefaultAsync(x => x.Id == flowId, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo nao encontrado.");

        var step = flow.Steps.SingleOrDefault(x => x.Id == stepId)
            ?? throw new AppNotFoundException("Etapa nao encontrada.");

        if (step.Type != StepType.ApiSend && step.Type != StepType.ApiQuery)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["api"] = ["Esta etapa nao possui integracao de API para teste."] });
        }

        var result = await integrations.ExecuteAsync(flow, step, request.Data, cancellationToken, triggerType: IntegrationTriggerType.Test);
        return new IntegrationTestResponse(
            result.Success,
            result.StatusCode,
            result.DurationMs,
            result.Url,
            result.Method,
            result.ResponsePreview,
            result.ErrorMessage,
            result.MappedData?.ToDictionary(x => x.Key, x => x.Value.ToString()));
    }

    private IQueryable<FlowDefinition> LoadFlows()
    {
        return db.Flows
            .Include(x => x.Tokens)
            .Include(x => x.AssignedUsers)
            .Include(x => x.Steps)
                .ThenInclude(x => x.AssignedUsers)
            .Include(x => x.Steps)
                .ThenInclude(x => x.Fields)
                    .ThenInclude(x => x.Options);
    }

    private FlowDto ToDto(FlowDefinition flow, bool includeTokenValues = false, bool hasDraft = false)
    {
        return new FlowDto(
            flow.Id,
            flow.FlowKey,
            flow.Name,
            flow.Description,
            flow.Active,
            flow.VersionNumber,
            flow.LifecycleStatus.ToString(),
            flow.PublishedAt,
            hasDraft,
            flow.Tokens
                .OrderBy(x => x.Name)
                .Select(x => new FlowTokenDto(x.Id, x.Name, includeTokenValues && !string.IsNullOrWhiteSpace(x.Value) ? tokenProtection.Unprotect(x.Value) : null, x.Type, x.HeaderName, x.Active))
                .ToList(),
            flow.AssignedUsers
                .OrderBy(x => x.UserId)
                .Select(x => x.UserId)
                .ToList(),
            flow.Steps
                .OrderBy(x => x.Order)
                .Select(step => new StepDto(
                    step.Id,
                    step.Name,
                    step.Description,
                    step.Type,
                    step.Order,
                    step.AssignedUsers.OrderBy(x => x.UserId).Select(x => x.UserId).ToList(),
                    step.Fields.OrderBy(x => x.Order).Select(ToFieldDto).ToList(),
                    ParseApiConfig(step.ConfigurationJson)))
                .ToList());
    }

    private void Apply(FlowDefinition flow, SaveFlowRequest request)
    {
        flow.Name = request.Name.Trim();
        flow.Description = request.Description.Trim();
        flow.Active = request.Active;

        flow.Tokens = request.Tokens
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new FlowToken
            {
                Name = x.Name.Trim(),
                Value = string.IsNullOrWhiteSpace(x.Value) ? string.Empty : tokenProtection.Protect(x.Value.Trim()),
                Type = x.Type,
                HeaderName = string.IsNullOrWhiteSpace(x.HeaderName) ? null : x.HeaderName.Trim(),
                Active = x.Active
            })
            .ToList();

        flow.AssignedUsers = request.AssignedUserIds
            .Distinct()
            .Select(userId => new FlowDefinitionUser
            {
                UserId = userId
            })
            .ToList();

        flow.Steps = request.Steps
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select((step, stepIndex) => new FlowStep
            {
                Name = step.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(step.Description) ? null : step.Description.Trim(),
                Type = step.Type,
                Order = stepIndex + 1,
                AssignedUserId = step.AssignedUserIds.FirstOrDefault(),
                AssignedUsers = step.AssignedUserIds
                    .Distinct()
                    .Select(userId => new FlowStepUser
                    {
                        UserId = userId
                    })
                    .ToList(),
                ConfigurationJson = step.ApiConfig is null ? null : JsonSerializer.Serialize(NormalizeApiConfig(step.ApiConfig)),
                Fields = step.Fields
                    .Where(x => !string.IsNullOrWhiteSpace(x.Label) && !string.IsNullOrWhiteSpace(x.Key))
                    .Select((field, fieldIndex) => new StepField
                    {
                        Key = field.Key.Trim(),
                        Label = field.Label.Trim(),
                        Type = field.Type,
                        Mask = string.IsNullOrWhiteSpace(field.Mask) ? null : field.Mask.Trim(),
                        Required = field.Required,
                        Order = fieldIndex + 1,
                        Options = NormalizeFieldOptions(field).ToList()
                    })
                    .ToList()
            })
            .ToList();
    }

    private static StepApiConfigDto? ParseApiConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var config = JsonSerializer.Deserialize<StepApiConfigDto>(json);
        return config is null ? null : NormalizeApiConfig(config);
    }

    private static void ValidateApiConfig(StepDto step)
    {
        if (step.ApiConfig is null)
        {
            return;
        }

        var scheduleMode = (step.ApiConfig.ScheduleMode ?? "manual").Trim().ToLowerInvariant();
        if (scheduleMode is not ("manual" or "interval" or "cron"))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["api"] = [$"A etapa '{step.Name}' possui um modo de agendamento invalido. Use manual, interval ou cron."] });
        }

        if (scheduleMode == "interval")
        {
            if (!TryParseIntervalMinutes(step.ApiConfig.ScheduleValue, out var minutes))
            {
                throw new AppValidationException(new Dictionary<string, string[]> { ["api"] = [$"A etapa '{step.Name}' precisa de um intervalo valido. Exemplos aceitos: '5 minutos', '15 minutos', '60 minutos' ou apenas '30'."] });
            }

            if (minutes < 1 || minutes > 10080)
            {
                throw new AppValidationException(new Dictionary<string, string[]> { ["api"] = [$"A etapa '{step.Name}' precisa ter intervalo entre 1 minuto e 10080 minutos (7 dias)."] });
            }
        }

        if (scheduleMode == "cron" && !IsValidCronExpression(step.ApiConfig.ScheduleValue))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["api"] = [$"A etapa '{step.Name}' precisa de uma expressao cron valida com 5 partes. Exemplo: '*/30 * * * *'."] });
        }
    }

    private static StepApiConfigDto NormalizeApiConfig(StepApiConfigDto config)
    {
        var scheduleMode = string.IsNullOrWhiteSpace(config.ScheduleMode) ? "manual" : config.ScheduleMode.Trim().ToLowerInvariant();
        string? scheduleValue = null;
        StepScheduleAssistDto? assist;

        if (scheduleMode == "interval" && TryParseIntervalMinutes(config.ScheduleValue, out var minutes))
        {
            scheduleValue = $"{minutes} minutos";
            assist = new StepScheduleAssistDto(minutes, null, $"Execucao recorrente a cada {minutes} minuto(s).");
        }
        else if (scheduleMode == "cron" && IsValidCronExpression(config.ScheduleValue))
        {
            scheduleValue = config.ScheduleValue!.Trim();
            assist = new StepScheduleAssistDto(null, scheduleValue, $"Expressao cron validada: {scheduleValue}.");
        }
        else
        {
            scheduleMode = "manual";
            assist = new StepScheduleAssistDto(null, null, "Execucao manual, sem agendamento automatico.");
        }

        return config with
        {
            Url = string.IsNullOrWhiteSpace(config.Url) ? null : config.Url.Trim(),
            Method = string.IsNullOrWhiteSpace(config.Method) ? null : config.Method.Trim().ToUpperInvariant(),
            TokenName = string.IsNullOrWhiteSpace(config.TokenName) ? null : config.TokenName.Trim(),
            ScheduleMode = scheduleMode,
            ScheduleValue = scheduleValue,
            QueryTemplate = string.IsNullOrWhiteSpace(config.QueryTemplate) ? null : config.QueryTemplate.Trim(),
            SendFieldKeys = config.SendFieldKeys?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ResponseMappings = config.ResponseMappings?.Where(x => !string.IsNullOrWhiteSpace(x.FieldKey) && !string.IsNullOrWhiteSpace(x.ResponsePath)).Select(x => new ResponseFieldMappingDto(x.FieldKey.Trim(), x.ResponsePath.Trim())).ToList(),
            BodyMappings = config.BodyMappings?.Where(x => !string.IsNullOrWhiteSpace(x.TargetKey) && !string.IsNullOrWhiteSpace(x.SourceReference)).Select(x => new BodyFieldMappingDto(x.TargetKey.Trim(), x.SourceReference.Trim())).ToList(),
            ScheduleAssist = assist,
            Headers = config.Headers?.Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value)).Select(x => new RequestHeaderDto(x.Name.Trim(), x.Value.Trim())).ToList(),
            BodyTemplate = string.IsNullOrWhiteSpace(config.BodyTemplate) ? null : config.BodyTemplate.Trim()
        };
    }

    private static bool TryParseIntervalMinutes(string? value, out int minutes)
    {
        minutes = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        if (int.TryParse(trimmed, out minutes))
        {
            return true;
        }

        var match = Regex.Match(trimmed, @"^(?<amount>\d+)\s*(min|mins|minuto|minutos|h|hr|hora|horas)?$");
        if (!match.Success || !int.TryParse(match.Groups["amount"].Value, out var amount))
        {
            return false;
        }

        var unit = match.Groups[2].Value;
        minutes = unit is "h" or "hr" or "hora" or "horas" ? amount * 60 : amount;
        return true;
    }

    private static bool IsValidCronExpression(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts.All(part => Regex.IsMatch(part, @"^[\d\*/,\-]+$"));
    }

    private static FlowDefinition CloneVersion(FlowDefinition source, FlowLifecycleStatus status, int versionNumber)
    {
        return new FlowDefinition
        {
            FlowKey = source.FlowKey,
            Name = source.Name,
            Description = source.Description,
            Active = source.Active,
            VersionNumber = versionNumber,
            LifecycleStatus = status,
            PublishedAt = status == FlowLifecycleStatus.Published ? DateTime.UtcNow : null,
            Tokens = source.Tokens.Select(token => new FlowToken
            {
                Name = token.Name,
                Value = token.Value,
                Type = token.Type,
                HeaderName = token.HeaderName,
                Active = token.Active
            }).ToList(),
            AssignedUsers = source.AssignedUsers
                .Select(user => new FlowDefinitionUser
                {
                    UserId = user.UserId
                })
                .ToList(),
            Steps = source.Steps
                .OrderBy(x => x.Order)
                .Select(step => new FlowStep
                {
                    Name = step.Name,
                    Description = step.Description,
                    Type = step.Type,
                    Order = step.Order,
                    AssignedUserId = step.AssignedUsers.Select(x => (Guid?)x.UserId).FirstOrDefault() ?? step.AssignedUserId,
                    AssignedUsers = step.AssignedUsers
                        .Select(user => new FlowStepUser
                        {
                            UserId = user.UserId
                        })
                        .ToList(),
                    ConfigurationJson = step.ConfigurationJson,
                    Fields = step.Fields
                        .OrderBy(x => x.Order)
                        .Select(field => new StepField
                        {
                            Key = field.Key,
                            Label = field.Label,
                            Type = field.Type,
                            Mask = field.Mask,
                            Required = field.Required,
                            Order = field.Order,
                            Options = field.Options
                                .OrderBy(x => x.Order)
                                .Select(option => new StepFieldOption
                                {
                                    Label = option.Label,
                                    Value = option.Value,
                                    Key = option.Key,
                                    Type = option.Type,
                                    Mask = option.Mask,
                                    Required = option.Required,
                                    Order = option.Order
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static void Validate(SaveFlowRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["name"] = ["Nome do fluxo e obrigatorio."] });
        }

        if (request.Steps.Count == 0)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["steps"] = ["Ao menos uma etapa e obrigatoria."] });
        }

        var fieldKeys = request.Steps
            .SelectMany(x => x.Fields)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => x.Key.Trim().ToLowerInvariant())
            .ToList();

        if (fieldKeys.Count != fieldKeys.Distinct().Count())
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["fields"] = ["As chaves dos campos devem ser unicas no fluxo inteiro."] });
        }

        foreach (var step in request.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
            {
                throw new AppValidationException(new Dictionary<string, string[]> { ["steps"] = ["Todas as etapas precisam de nome."] });
            }

            if ((step.Type == StepType.ApiSend || step.Type == StepType.ApiQuery) && string.IsNullOrWhiteSpace(step.ApiConfig?.Url))
            {
                throw new AppValidationException(new Dictionary<string, string[]> { ["api"] = [$"A etapa '{step.Name}' precisa ter URL configurada."] });
            }

            ValidateApiConfig(step);

            foreach (var field in step.Fields)
            {
                if (field.Type == FieldType.Select || field.Type == FieldType.Radio)
                {
                    if (field.Options.Count == 0)
                    {
                        throw new AppValidationException(new Dictionary<string, string[]> { ["fields"] = [$"O campo '{field.Label}' precisa ter opções cadastradas."] });
                    }
                }
            }
        }
    }

    private static FieldDto ToFieldDto(StepField field)
    {
        return new FieldDto(
            field.Id,
            field.Key,
            field.Label,
            field.Type,
            field.Mask,
            field.Required,
            field.Order,
            field.Options
                .OrderBy(option => option.Order)
                .Select(option => new FieldOptionDto(option.Id, option.Label, option.Value, option.Order, option.Key, option.Type, option.Mask, option.Required))
                .ToList());
    }

    private static IEnumerable<StepFieldOption> NormalizeFieldOptions(FieldDto field)
    {
        return field.Options
            .Where(option => HasOptionContent(field.Type, option))
            .Select((option, optionIndex) =>
            {
                if (field.Type == FieldType.Select && HasStructuredListField(option))
                {
                    var key = string.IsNullOrWhiteSpace(option.Key) ? null : option.Key.Trim();
                    if (string.IsNullOrWhiteSpace(key) || !option.Type.HasValue || string.IsNullOrWhiteSpace(option.Label))
                    {
                        return new StepFieldOption
                        {
                            Label = string.IsNullOrWhiteSpace(option.Label) ? string.Empty : option.Label.Trim(),
                            Value = string.IsNullOrWhiteSpace(option.Value) ? string.Empty : option.Value.Trim(),
                            Key = key,
                            Type = option.Type,
                            Mask = string.IsNullOrWhiteSpace(option.Mask) ? null : option.Mask.Trim(),
                            Required = option.Required ?? false,
                            Order = optionIndex + 1
                        };
                    }

                    return new StepFieldOption
                    {
                        Label = option.Label.Trim(),
                        Value = string.IsNullOrWhiteSpace(option.Value) ? key : option.Value.Trim(),
                        Key = key,
                        Type = option.Type,
                        Mask = string.IsNullOrWhiteSpace(option.Mask) ? null : option.Mask.Trim(),
                        Required = option.Required ?? false,
                        Order = optionIndex + 1
                    };
                }

                var normalizedLabel = string.IsNullOrWhiteSpace(option.Label) ? option.Value.Trim() : option.Label.Trim();
                var normalizedValue = string.IsNullOrWhiteSpace(option.Value) ? option.Label.Trim() : option.Value.Trim();

                return new StepFieldOption
                {
                    Label = normalizedLabel,
                    Value = normalizedValue,
                    Key = string.IsNullOrWhiteSpace(option.Key) ? null : option.Key.Trim(),
                    Type = option.Type,
                    Mask = string.IsNullOrWhiteSpace(option.Mask) ? null : option.Mask.Trim(),
                    Required = option.Required ?? false,
                    Order = optionIndex + 1
                };
            });
    }

    private static bool HasOptionContent(FieldType fieldType, FieldOptionDto option)
    {
        return fieldType == FieldType.Select && HasStructuredListField(option)
            || !string.IsNullOrWhiteSpace(option.Label)
            || !string.IsNullOrWhiteSpace(option.Value);
    }

    private static bool HasStructuredListField(FieldOptionDto option)
    {
        return !string.IsNullOrWhiteSpace(option.Label)
            || !string.IsNullOrWhiteSpace(option.Key)
            || option.Type.HasValue
            || !string.IsNullOrWhiteSpace(option.Mask)
            || option.Required == true;
    }

    private static void ValidateDraftForPublish(FlowDefinition draft)
    {
        if (!draft.Steps.Any())
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["steps"] = ["Nao e possivel publicar um fluxo sem etapas."] });
        }

        if (draft.Steps.Any(step => string.IsNullOrWhiteSpace(step.Name)))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["steps"] = ["Todas as etapas precisam estar nomeadas antes da publicacao."] });
        }
    }
}

public sealed class AuthService(
    IAppDbContext db,
    IPasswordService passwords,
    ITokenService tokens) : IAuthService
{
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email && x.Active, cancellationToken)
            ?? throw new AppForbiddenException("Credenciais invalidas.");

        if (!passwords.Verify(user, user.PasswordHash, request.Password))
        {
            throw new AppForbiddenException("Credenciais invalidas.");
        }

        return new LoginResponse(
            tokens.Create(user),
            new UserDto(user.Id, user.Name, user.Email, user.Role.ToString(), user.Active));
    }
}

public sealed class UserManagementService(
    IAppDbContext db,
    IPasswordService passwords,
    IAuditService audit) : IUserManagementService
{
    public async Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await db.Users
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new UserDto(x.Id, x.Name, x.Email, x.Role.ToString(), x.Active))
            .ToListAsync(cancellationToken);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, string? currentUserRole, Guid? actorUserId, CancellationToken cancellationToken)
    {
        await ValidateCreateAsync(request, cancellationToken);

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["role"] = ["Perfil invalido."] });
        }

        if (IsAdminTryingToGrantSuperAdmin(currentUserRole, role))
        {
            throw new AppForbiddenException();
        }

        var user = new AppUser
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim().ToLowerInvariant(),
            Role = role,
            Active = true
        };

        user.PasswordHash = passwords.Hash(user, request.Password);
        db.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("User", "Create", "AppUser", user.Id, $"Usuario '{user.Email}' criado com perfil {user.Role}.", actorUserId, cancellationToken);

        return new UserDto(user.Id, user.Name, user.Email, user.Role.ToString(), user.Active);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, string? currentUserRole, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Role))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["user"] = ["Nome e perfil sao obrigatorios."] });
        }

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["role"] = ["Perfil invalido."] });
        }

        if (IsAdminTryingToGrantSuperAdmin(currentUserRole, role))
        {
            throw new AppForbiddenException();
        }

        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Usuario nao encontrado.");

        if (user.Role == UserRole.SuperAdmin && (!request.Active || role != UserRole.SuperAdmin))
        {
            var activeSuperAdmins = await db.Users.CountAsync(x => x.Active && x.Role == UserRole.SuperAdmin, cancellationToken);
            if (activeSuperAdmins <= 1)
            {
                throw new AppConflictException("Nao e permitido desativar ou rebaixar o ultimo super admin ativo.");
            }
        }

        user.Name = request.Name.Trim();
        user.Role = role;
        user.Active = request.Active;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length < 8)
            {
                throw new AppValidationException(new Dictionary<string, string[]> { ["password"] = ["A senha deve ter ao menos 8 caracteres."] });
            }

            user.PasswordHash = passwords.Hash(user, request.Password);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("User", "Update", "AppUser", user.Id, $"Usuario '{user.Email}' atualizado para perfil {user.Role} e status {(user.Active ? "ativo" : "inativo")}.", actorUserId, cancellationToken);
        return new UserDto(user.Id, user.Name, user.Email, user.Role.ToString(), user.Active);
    }

    private async Task ValidateCreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password) || string.IsNullOrWhiteSpace(request.Role))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["user"] = ["Nome, e-mail, senha e perfil sao obrigatorios."] });
        }

        if (request.Password.Length < 8)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["password"] = ["A senha deve ter ao menos 8 caracteres."] });
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == normalizedEmail, cancellationToken))
        {
            throw new AppConflictException("Ja existe um usuario com este e-mail.");
        }
    }

    private static bool IsAdminTryingToGrantSuperAdmin(string? currentUserRole, UserRole role)
    {
        return string.Equals(currentUserRole, UserRole.Admin.ToString(), StringComparison.OrdinalIgnoreCase)
            && role == UserRole.SuperAdmin;
    }
}

public sealed class InstanceManagementService(
    IAppDbContext db,
    IInstanceAutomationService automation,
    IIntegrationExecutionService integrations,
    IFileStorageService fileStorage) : IInstanceManagementService
{
    public async Task<IReadOnlyList<InstanceDto>> GetAllAsync(Guid? flowId, string? status, string? search, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var query = db.Instances
            .AsNoTracking()
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.AssignedUsers)
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
                    .ThenInclude(x => x.AssignedUsers)
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
                    .ThenInclude(x => x.Fields)
                        .ThenInclude(x => x.Options)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
                    .ThenInclude(x => x.AssignedUsers)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
            .AsQueryable();

        if (flowId.HasValue)
        {
            query = query.Where(x => x.FlowDefinitionId == flowId.Value);
        }

        if (Enum.TryParse<InstanceStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(x => x.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            query = query.Where(x => x.Code.ToLower().Contains(normalized));
        }

        var rows = await query.OrderByDescending(x => x.UpdatedAt).Take(200).ToListAsync(cancellationToken);
        rows = rows.Where(row => CanViewInstance(row, actorUserId)).ToList();
        var result = new List<InstanceDto>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await ToDtoAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<InstanceDto>> GetPendingTasksAsync(Guid? actorUserId, CancellationToken cancellationToken)
    {
        var rows = await LoadInstance()
            .AsNoTracking()
            .Where(x => x.Status == InstanceStatus.InProgress)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var visibleRows = rows
            .Where(row =>
            {
                var current = row.StepExecutions.SingleOrDefault(step => step.Status == StepStatus.InProgress);
                return current is not null
                    && !IsAutomaticStep(current.FlowStep.Type)
                    && CanActOnStep(row.FlowDefinition, current.FlowStep, actorUserId);
            })
            .ToList();

        var result = new List<InstanceDto>(visibleRows.Count);
        foreach (var row in visibleRows)
        {
            result.Add(await ToDtoAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<InstanceDto> GetByIdAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        if (!CanViewInstance(item, actorUserId))
        {
            throw new AppForbiddenException();
        }

        return await ToDtoAsync(item, cancellationToken);
    }

    public async Task<Guid> CreateAsync(CreateInstanceRequest request, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var requestedFlow = await db.Flows
            .Include(x => x.Tokens)
            .Include(x => x.AssignedUsers)
            .Include(x => x.Steps)
                .ThenInclude(x => x.Fields)
            .SingleOrDefaultAsync(x => x.Id == request.FlowDefinitionId && x.Active, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo nao encontrado.");

        var relatedVersions = await db.Flows
            .Include(x => x.Tokens)
            .Include(x => x.AssignedUsers)
            .Include(x => x.Steps)
                .ThenInclude(x => x.Fields)
            .Where(x => x.FlowKey == requestedFlow.FlowKey && x.Active)
            .ToListAsync(cancellationToken);

        var flow = FlowRuntimeSelectionHelper.SelectEffectiveVersion(relatedVersions)
            ?? throw new AppConflictException("Nenhuma versao publicada deste fluxo esta disponivel para novas execucoes.");

        if (!HasFlowAccess(flow, actorUserId))
        {
            throw new AppForbiddenException();
        }

        var orderedSteps = flow.Steps.OrderBy(x => x.Order).ToList();
        var firstStep = orderedSteps.FirstOrDefault();

        var now = DateTime.UtcNow;
        var instance = new FlowInstance
        {
            FlowDefinitionId = flow.Id,
            Code = string.IsNullOrWhiteSpace(request.Code) ? $"FL-{now:yyyyMMddHHmmss}" : request.Code.Trim(),
            DataJson = JsonSerializer.Serialize(request.Data),
            CurrentStepOrder = firstStep?.Order ?? 0,
            StepExecutions = orderedSteps
                .Select((step, index) => new StepExecution
                {
                    FlowStepId = step.Id,
                    Status = index == 0 ? StepStatus.InProgress : StepStatus.Pending,
                    StartedAt = index == 0 ? now : null
                })
                .ToList()
        };

        if (orderedSteps.Count == 0)
        {
            instance.Status = InstanceStatus.Completed;
        }

        db.Add(instance);
        await db.SaveChangesAsync(cancellationToken);
        await automation.ProcessAsync(instance.Id, cancellationToken);
        return instance.Id;
    }

    public async Task<InstanceDto> SaveCurrentStepDataAsync(Guid id, Dictionary<string, JsonElement> data, string? notes, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        if (item.Status != InstanceStatus.InProgress)
        {
            throw new AppConflictException("Execucao nao esta em andamento.");
        }

        var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress);
        if (current is null)
        {
            throw new AppConflictException("Nenhuma etapa ativa encontrada.");
        }

        if (!CanActOnStep(item.FlowDefinition, current.FlowStep, actorUserId))
        {
            throw new AppForbiddenException();
        }

        if (current.FlowStep.Type == StepType.ApiSend || current.FlowStep.Type == StepType.ApiQuery || current.FlowStep.Type == StepType.Automatic)
        {
            throw new AppConflictException("A etapa atual eh automatica e nao aceita preenchimento manual.");
        }

        var mergedData = MergeStepData(item, current, data);
        ValidateRequiredFields(current, mergedData);

        current.DataJson = JsonSerializer.Serialize(mergedData);
        current.Notes = notes;
        item.DataJson = MergeInstanceData(item.DataJson, mergedData);
        item.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadInstance().AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        return await ToDtoAsync(reloaded, cancellationToken);
    }

    public async Task<InstanceDto> UploadCurrentStepFileAsync(Guid id, string fieldKey, string fileName, string? contentType, Stream stream, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        if (item.Status != InstanceStatus.InProgress)
        {
            throw new AppConflictException("Execucao nao esta em andamento.");
        }

        var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress);
        if (current is null)
        {
            throw new AppConflictException("Nenhuma etapa ativa encontrada.");
        }

        if (!CanActOnStep(item.FlowDefinition, current.FlowStep, actorUserId))
        {
            throw new AppForbiddenException();
        }

        if (current.FlowStep.Type == StepType.ApiSend || current.FlowStep.Type == StepType.ApiQuery || current.FlowStep.Type == StepType.Automatic)
        {
            throw new AppConflictException("A etapa atual eh automatica e nao aceita anexos manuais.");
        }

        var normalizedFieldKey = fieldKey.Trim();
        var field = current.FlowStep.Fields.SingleOrDefault(x => string.Equals(x.Key, normalizedFieldKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppValidationException(new Dictionary<string, string[]> { ["fieldKey"] = ["Campo da etapa nao encontrado."] });

        var isPhoto = field.Type == FieldType.Photo;
        var isAttachment = field.Type == FieldType.Attachment || field.Type == FieldType.Document;
        if (!isPhoto && !isAttachment)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["fieldKey"] = ["O campo informado nao aceita upload de arquivos."] });
        }

        if (isPhoto && !string.IsNullOrWhiteSpace(contentType) && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["file"] = ["O campo de foto aceita apenas imagens."] });
        }

        var uploaded = await fileStorage.SaveStepFileAsync(id, current.Id, normalizedFieldKey, fileName, contentType ?? "application/octet-stream", stream, isPhoto, actorUserId, cancellationToken);

        var mergedData = MergeStepData(item, current, null);
        var existingFiles = ReadUploadedFiles(mergedData, normalizedFieldKey);
        existingFiles.Add(uploaded);
        mergedData[normalizedFieldKey] = JsonSerializer.SerializeToElement(existingFiles);

        current.DataJson = JsonSerializer.Serialize(mergedData);
        item.DataJson = MergeInstanceData(item.DataJson, mergedData);
        item.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadInstance().AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        return await ToDtoAsync(reloaded, cancellationToken);
    }

    public async Task AdvanceAsync(Guid id, AdvanceStepRequest request, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        if (item.Status != InstanceStatus.InProgress)
        {
            throw new AppConflictException("Execucao nao esta em andamento.");
        }

        var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress);
        if (current is null)
        {
            throw new AppConflictException("Nenhuma etapa ativa encontrada.");
        }

        if (!CanActOnStep(item.FlowDefinition, current.FlowStep, actorUserId))
        {
            throw new AppForbiddenException();
        }

        if (current.FlowStep.Type == StepType.ApiSend || current.FlowStep.Type == StepType.ApiQuery || current.FlowStep.Type == StepType.Automatic)
        {
            throw new AppConflictException("A etapa atual eh automatica. Use o retry de integracao se necessario.");
        }

        var mergedData = MergeStepData(item, current, request.Data);
        ValidateRequiredFields(current, mergedData);
        current.DataJson = JsonSerializer.Serialize(mergedData);
        item.DataJson = MergeInstanceData(item.DataJson, mergedData);

        CompleteCurrentStep(item, current, request.Notes, actorUserId);
        await db.SaveChangesAsync(cancellationToken);
        await automation.ProcessAsync(item.Id, cancellationToken);
    }

    public async Task<InstanceDto> RetryIntegrationAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        if (!CanViewInstance(item, actorUserId))
        {
            throw new AppForbiddenException();
        }

        await automation.ProcessAsync(item.Id, cancellationToken, forceFailedCurrent: true);
        var reloaded = await LoadInstance().AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        return await ToDtoAsync(reloaded, cancellationToken);
    }

    public async Task<InstanceDto> ReprocessStepAsync(Guid id, Guid stepExecutionId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        if (!CanViewInstance(item, actorUserId))
        {
            throw new AppForbiddenException();
        }

        var target = item.StepExecutions.SingleOrDefault(x => x.Id == stepExecutionId)
            ?? throw new AppNotFoundException("Etapa da execucao nao encontrada.");

        var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress || x.Status == StepStatus.Failed);
        var isCurrentStep = current?.Id == target.Id;
        var isAutomaticType = target.FlowStep.Type == StepType.ApiSend || target.FlowStep.Type == StepType.ApiQuery || target.FlowStep.Type == StepType.Automatic;

        if (!isAutomaticType)
        {
            throw new AppConflictException("A etapa selecionada nao suporta reprocessamento manual.");
        }

        if (isCurrentStep)
        {
            await automation.ProcessAsync(item.Id, cancellationToken, forceFailedCurrent: target.Status == StepStatus.Failed);
            var reloadedCurrent = await LoadInstance().AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
            return await ToDtoAsync(reloadedCurrent, cancellationToken);
        }

        if (target.Status != StepStatus.Completed)
        {
            throw new AppConflictException("Somente a etapa automatica atual ou etapas concluidas podem ser reprocessadas manualmente.");
        }

        if (target.FlowStep.Type != StepType.ApiSend && target.FlowStep.Type != StepType.ApiQuery)
        {
            throw new AppConflictException("Reprocessamento de etapa concluida esta disponivel apenas para integracoes.");
        }

        var mergedData = MergeStepData(item, target, null);
        var result = await integrations.ExecuteAsync(item.FlowDefinition, target.FlowStep, mergedData, cancellationToken, item, target, IntegrationTriggerType.Runtime);

        target.Notes = result.Success
            ? "Etapa reprocessada manualmente."
            : result.ErrorMessage ?? "Falha no reprocessamento manual.";

        if (result.Success && (target.FlowStep.Type == StepType.ApiQuery || target.FlowStep.Type == StepType.ApiSend) && result.MappedData is not null)
        {
            foreach (var mapped in result.MappedData)
            {
                mergedData[mapped.Key] = mapped.Value;
            }

            target.DataJson = JsonSerializer.Serialize(mergedData);
            item.DataJson = MergeInstanceData(item.DataJson, mergedData);
        }

        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var reloaded = await LoadInstance().AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        return await ToDtoAsync(reloaded, cancellationToken);
    }

    private IQueryable<FlowInstance> LoadInstance()
    {
        return db.Instances
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.AssignedUsers)
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Tokens)
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
                    .ThenInclude(x => x.AssignedUsers)
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
                    .ThenInclude(x => x.Fields)
                        .ThenInclude(x => x.Options)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
                    .ThenInclude(x => x.AssignedUsers)
            .Include(x => x.StepExecutions)
                .ThenInclude(x => x.FlowStep)
                    .ThenInclude(x => x.Fields)
                        .ThenInclude(x => x.Options);
    }

    private static bool IsAutomaticStep(StepType stepType)
    {
        return stepType == StepType.ApiSend || stepType == StepType.ApiQuery || stepType == StepType.Automatic;
    }

    private static bool HasFlowAccess(FlowDefinition flow, Guid? actorUserId)
    {
        if (flow.AssignedUsers.Count == 0)
        {
            return true;
        }

        return actorUserId.HasValue && flow.AssignedUsers.Any(user => user.UserId == actorUserId.Value);
    }

    private static bool CanActOnStep(FlowDefinition flow, FlowStep step, Guid? actorUserId)
    {
        var hasStepAssignments = step.AssignedUsers.Count > 0;
        var assignedToStep = actorUserId.HasValue && step.AssignedUsers.Any(user => user.UserId == actorUserId.Value);

        if (hasStepAssignments)
        {
            return assignedToStep || HasFlowAccess(flow, actorUserId);
        }

        return HasFlowAccess(flow, actorUserId) || (flow.AssignedUsers.Count == 0 && !step.AssignedUserId.HasValue);
    }

    private static bool CanViewInstance(FlowInstance item, Guid? actorUserId)
    {
        if (HasFlowAccess(item.FlowDefinition, actorUserId))
        {
            return true;
        }

        var currentStep = item.StepExecutions.SingleOrDefault(step => step.Status == StepStatus.InProgress);
        if (currentStep is null || !actorUserId.HasValue)
        {
            return false;
        }

        return currentStep.FlowStep.AssignedUsers.Any(user => user.UserId == actorUserId.Value);
    }

    private static Dictionary<string, JsonElement> MergeStepData(FlowInstance item, StepExecution current, Dictionary<string, JsonElement>? newData)
    {
        var instanceData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.DataJson) ?? [];
        var stepData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.DataJson) ?? [];

        foreach (var entry in instanceData)
        {
            stepData.TryAdd(entry.Key, entry.Value);
        }

        if (newData is not null)
        {
            foreach (var entry in newData)
            {
                stepData[entry.Key] = entry.Value;
            }
        }

        return stepData;
    }

    private static void ValidateRequiredFields(StepExecution current, Dictionary<string, JsonElement> data)
    {
        var missing = current.FlowStep.Fields
            .Where(x => x.Required)
            .Where(x => IsMissingRequiredValue(x, data))
            .Select(x => x.Label)
            .ToList();

        var invalidStructuredRows = current.FlowStep.Fields
            .SelectMany(field => ValidateStructuredListRows(field, data))
            .ToList();

        if (missing.Count > 0 || invalidStructuredRows.Count > 0)
        {
            throw new AppValidationException(new Dictionary<string, string[]>
            {
                ["required"] = [.. missing, .. invalidStructuredRows]
            });
        }
    }

    private static string MergeInstanceData(string instanceDataJson, Dictionary<string, JsonElement> stepData)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(instanceDataJson) ?? [];
        foreach (var entry in stepData)
        {
            data[entry.Key] = entry.Value;
        }

        return JsonSerializer.Serialize(data);
    }

    private static bool IsMissingRequiredValue(StepField field, Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue(field.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (field.Type is FieldType.Document or FieldType.Attachment or FieldType.Photo)
        {
            return value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0;
        }

        if (field.Type == FieldType.Select && field.Options.Any(option => !string.IsNullOrWhiteSpace(option.Key) && option.Type.HasValue))
        {
            return value.ValueKind != JsonValueKind.Array || !value.EnumerateArray().Any(row => row.ValueKind == JsonValueKind.Object && row.EnumerateObject().Any(property => !string.IsNullOrWhiteSpace(property.Value.ToString())));
        }

        return string.IsNullOrWhiteSpace(value.ToString());
    }

    private static IEnumerable<string> ValidateStructuredListRows(StepField field, Dictionary<string, JsonElement> data)
    {
        if (field.Type != FieldType.Select || !field.Options.Any(option => !string.IsNullOrWhiteSpace(option.Key) && option.Type.HasValue))
        {
            return [];
        }

        if (!data.TryGetValue(field.Key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var errors = new List<string>();
        var requiredColumns = field.Options
            .Where(option => option.Required && !string.IsNullOrWhiteSpace(option.Key))
            .Select(option => new { option.Label, Key = option.Key!.Trim() })
            .ToList();

        if (requiredColumns.Count == 0)
        {
            return errors;
        }

        var rowIndex = 0;
        foreach (var row in value.EnumerateArray())
        {
            rowIndex++;
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var hasAnyContent = row.EnumerateObject().Any(property => !string.IsNullOrWhiteSpace(property.Value.ToString()));
            if (!hasAnyContent)
            {
                continue;
            }

            foreach (var column in requiredColumns)
            {
                if (!row.TryGetProperty(column.Key, out var cell) || string.IsNullOrWhiteSpace(cell.ToString()))
                {
                    errors.Add($"{field.Label}: item {rowIndex} precisa preencher '{column.Label}'.");
                }
            }
        }

        return errors;
    }

    private static List<UploadedFileDto> ReadUploadedFiles(Dictionary<string, JsonElement> data, string fieldKey)
    {
        if (!data.TryGetValue(fieldKey, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var files = new List<UploadedFileDto>();
        foreach (var item in value.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString()
                : item.TryGetProperty("Id", out var legacyIdProp) ? legacyIdProp.GetString()
                : null;
            var fileName = item.TryGetProperty("fileName", out var fileNameProp) ? fileNameProp.GetString()
                : item.TryGetProperty("FileName", out var legacyFileNameProp) ? legacyFileNameProp.GetString()
                : null;
            var contentType = item.TryGetProperty("contentType", out var contentTypeProp) ? contentTypeProp.GetString()
                : item.TryGetProperty("ContentType", out var legacyContentTypeProp) ? legacyContentTypeProp.GetString()
                : null;
            var url = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString()
                : item.TryGetProperty("Url", out var legacyUrlProp) ? legacyUrlProp.GetString()
                : null;
            var uploadedAtText = item.TryGetProperty("uploadedAt", out var uploadedAtProp) ? uploadedAtProp.GetString()
                : item.TryGetProperty("UploadedAt", out var legacyUploadedAtProp) ? legacyUploadedAtProp.GetString()
                : null;
            var fieldKeyValue = item.TryGetProperty("fieldKey", out var fieldKeyProp) ? fieldKeyProp.GetString()
                : item.TryGetProperty("FieldKey", out var legacyFieldKeyProp) ? legacyFieldKeyProp.GetString()
                : fieldKey;
            var size = item.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var parsedSize) ? parsedSize
                : item.TryGetProperty("Size", out var legacySizeProp) && legacySizeProp.TryGetInt64(out var parsedLegacySize) ? parsedLegacySize
                : 0;
            var isPhoto = item.TryGetProperty("isPhoto", out var isPhotoProp) && isPhotoProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? isPhotoProp.GetBoolean()
                : item.TryGetProperty("IsPhoto", out var legacyIsPhotoProp) && legacyIsPhotoProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? legacyIsPhotoProp.GetBoolean()
                    : false;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            files.Add(new UploadedFileDto(
                id,
                fieldKeyValue ?? fieldKey,
                fileName,
                contentType ?? "application/octet-stream",
                size,
                url ?? string.Empty,
                isPhoto,
                DateTime.TryParse(uploadedAtText, out var uploadedAt) ? uploadedAt : DateTime.UtcNow));
        }

        return files;
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

    private async Task<InstanceDto> ToDtoAsync(FlowInstance item, CancellationToken cancellationToken)
    {
        var userIds = item.StepExecutions
            .Where(x => x.CompletedByUserId.HasValue)
            .Select(x => x.CompletedByUserId!.Value)
            .Distinct()
            .ToArray();

        var userLookup = userIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Users
                .AsNoTracking()
                .Where(x => userIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var stepExecutionIds = item.StepExecutions.Select(x => x.Id).ToArray();
        var storedFiles = stepExecutionIds.Length == 0
            ? []
            : await db.StoredFiles
                .AsNoTracking()
                .Where(x => stepExecutionIds.Contains(x.StepExecutionId))
                .OrderBy(x => x.UploadedAt)
                .ToListAsync(cancellationToken);
        var integrationAttemptsByStep = await LoadIntegrationAttemptsByStepAsync(item.Id, cancellationToken);

        var storedFileDtos = new Dictionary<Guid, List<UploadedFileDto>>();
        foreach (var file in storedFiles)
        {
            var url = await fileStorage.CreateReadUrlAsync(file.BucketName, file.ObjectKey, file.FileName, cancellationToken);
            if (!storedFileDtos.TryGetValue(file.StepExecutionId, out var list))
            {
                list = [];
                storedFileDtos[file.StepExecutionId] = list;
            }

            list.Add(new UploadedFileDto(file.Id.ToString(), file.FieldKey, file.FileName, file.ContentType, file.Size, url, file.IsPhoto, file.UploadedAt));
        }

        return new InstanceDto(
            item.Id,
            item.FlowDefinitionId,
            item.FlowDefinition.Name,
            item.Code,
            item.Status,
            item.CurrentStepOrder,
            item.CreatedAt,
            item.UpdatedAt,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.DataJson) ?? [],
            item.StepExecutions.FirstOrDefault(x => x.Status == StepStatus.InProgress)?.Id,
            item.StepExecutions
                .OrderBy(x => x.FlowStep.Order)
                .Select(x =>
                {
                    var rawData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(x.DataJson) ?? [];
                    var enrichedData = new Dictionary<string, JsonElement>(rawData, StringComparer.OrdinalIgnoreCase);

                    if (storedFileDtos.TryGetValue(x.Id, out var stepFiles))
                    {
                        foreach (var groupedFile in stepFiles.GroupBy(file => file.FieldKey, StringComparer.OrdinalIgnoreCase))
                        {
                            enrichedData[groupedFile.Key] = JsonSerializer.SerializeToElement(groupedFile.ToList());
                        }
                    }

                    return new StepProgressDto(
                        x.Id,
                        x.FlowStepId,
                        x.FlowStep.Name,
                        x.FlowStep.Order,
                        x.FlowStep.Type,
                        x.Status,
                        x.StartedAt,
                        x.CompletedAt,
                        x.CompletedByUserId,
                        x.CompletedByUserId.HasValue && userLookup.TryGetValue(x.CompletedByUserId.Value, out var completedByName) ? completedByName : null,
                        x.Notes,
                        x.FlowStep.Type == StepType.Automatic || x.FlowStep.Type == StepType.ApiSend || x.FlowStep.Type == StepType.ApiQuery,
                        enrichedData,
                        x.FlowStep.Fields
                            .OrderBy(f => f.Order)
                            .Select(f => new ExecutionFieldDto(
                                f.Id,
                                f.Key,
                                f.Label,
                                f.Type,
                                f.Mask,
                                f.Required,
                                f.Order,
                                f.Options.OrderBy(o => o.Order).Select(o => new FieldOptionDto(o.Id, o.Label, o.Value, o.Order, o.Key, o.Type, o.Mask, o.Required)).ToList(),
                                f.Type is FieldType.Attachment or FieldType.Photo or FieldType.Document
                                    ? string.Join(", ", (storedFileDtos.TryGetValue(x.Id, out var uploadFiles) ? uploadFiles.Where(file => string.Equals(file.FieldKey, f.Key, StringComparison.OrdinalIgnoreCase)).Select(file => file.FileName) : Enumerable.Empty<string>()).ToArray())
                                    : rawData.TryGetValue(f.Key, out var fieldValue) ? fieldValue.ToString() : null))
                            .ToList(),
                        integrationAttemptsByStep.TryGetValue(x.FlowStepId, out var attempts) ? attempts : []);
                })
                .ToList());
    }

    private async Task<Dictionary<Guid, IReadOnlyList<IntegrationAttemptDto>>> LoadIntegrationAttemptsByStepAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var attempts = await db.IntegrationAttempts
            .AsNoTracking()
            .Where(x => x.FlowInstanceId == instanceId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.FlowStepId,
                x.TriggerType,
                x.Method,
                x.Url,
                x.ResponseStatusCode,
                x.Success,
                x.DurationMs,
                x.CreatedAt,
                x.ResponsePreview,
                x.ErrorMessage
            })
            .ToListAsync(cancellationToken);

        return attempts
            .GroupBy(x => x.FlowStepId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<IntegrationAttemptDto>)group
                    .Select(x => new IntegrationAttemptDto(
                        x.Id,
                        x.TriggerType.ToString(),
                        x.Method,
                        x.Url,
                        x.ResponseStatusCode,
                        x.Success,
                        x.DurationMs,
                        x.CreatedAt,
                        null,
                        null,
                        x.ResponsePreview,
                        x.ErrorMessage))
                    .ToList());
    }
}

public static class FlowRuntimeSelectionHelper
{
    public static FlowDefinition? SelectEffectiveVersion(IEnumerable<FlowDefinition> versions)
    {
        return versions
            .Where(x => x.LifecycleStatus == FlowLifecycleStatus.Published)
            .OrderByDescending(x => x.VersionNumber)
            .FirstOrDefault();
    }
}
