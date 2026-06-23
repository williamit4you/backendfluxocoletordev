using System.Text.Json;
using AutoMapper;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.Application;

public sealed class FlowManagementService(
    IAppDbContext db,
    IMapper mapper,
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
                .Where(x => x.Active && x.LifecycleStatus == FlowLifecycleStatus.Published)
                .GroupBy(x => x.FlowKey)
                .Select(group => group.OrderByDescending(x => x.VersionNumber).First())
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

        var flow = await LoadFlows().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo nao encontrado.");

        if (flow.LifecycleStatus != FlowLifecycleStatus.Draft)
        {
            throw new AppConflictException("Somente versoes em rascunho podem ser alteradas. Gere um rascunho antes de editar.");
        }

        db.RemoveRange(flow.Steps.SelectMany(x => x.Fields).SelectMany(x => x.Options));
        db.RemoveRange(flow.Steps.SelectMany(x => x.Fields));
        db.RemoveRange(flow.Steps);
        db.RemoveRange(flow.Tokens);

        Apply(flow, request);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("Flow", "UpdateDraft", "FlowDefinition", flow.Id, $"Rascunho '{flow.Name}' atualizado.", actorUserId, cancellationToken);
        return flow.Id;
    }

    public async Task<Guid> CreateDraftAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var source = await LoadFlows().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo nao encontrado.");

        var existingDraft = await LoadFlows().SingleOrDefaultAsync(x => x.FlowKey == source.FlowKey && x.LifecycleStatus == FlowLifecycleStatus.Draft, cancellationToken);
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
            flow.Steps
                .OrderBy(x => x.Order)
                .Select(step => new StepDto(
                    step.Id,
                    step.Name,
                    step.Description,
                    step.Type,
                    step.Order,
                    step.AssignedUserId,
                    step.Fields.OrderBy(x => x.Order).Select(mapper.Map<FieldDto>).ToList(),
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

        flow.Steps = request.Steps
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select((step, stepIndex) => new FlowStep
            {
                Name = step.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(step.Description) ? null : step.Description.Trim(),
                Type = step.Type,
                Order = stepIndex + 1,
                AssignedUserId = step.AssignedUserId,
                ConfigurationJson = step.ApiConfig is null ? null : JsonSerializer.Serialize(step.ApiConfig),
                Fields = step.Fields
                    .Where(x => !string.IsNullOrWhiteSpace(x.Label) && !string.IsNullOrWhiteSpace(x.Key))
                    .Select((field, fieldIndex) => new StepField
                    {
                        Key = field.Key.Trim(),
                        Label = field.Label.Trim(),
                        Type = field.Type,
                        Required = field.Required,
                        Order = fieldIndex + 1,
                        Options = field.Options
                            .Where(x => !string.IsNullOrWhiteSpace(x.Label) || !string.IsNullOrWhiteSpace(x.Value))
                            .Select((option, optionIndex) => new StepFieldOption
                            {
                                Label = string.IsNullOrWhiteSpace(option.Label) ? option.Value.Trim() : option.Label.Trim(),
                                Value = string.IsNullOrWhiteSpace(option.Value) ? option.Label.Trim() : option.Value.Trim(),
                                Order = optionIndex + 1
                            })
                            .ToList()
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

        return JsonSerializer.Deserialize<StepApiConfigDto>(json);
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
            Steps = source.Steps
                .OrderBy(x => x.Order)
                .Select(step => new FlowStep
                {
                    Name = step.Name,
                    Description = step.Description,
                    Type = step.Type,
                    Order = step.Order,
                    AssignedUserId = step.AssignedUserId,
                    ConfigurationJson = step.ConfigurationJson,
                    Fields = step.Fields
                        .OrderBy(x => x.Order)
                        .Select(field => new StepField
                        {
                            Key = field.Key,
                            Label = field.Label,
                            Type = field.Type,
                            Required = field.Required,
                            Order = field.Order,
                            Options = field.Options
                                .OrderBy(x => x.Order)
                                .Select(option => new StepFieldOption
                                {
                                    Label = option.Label,
                                    Value = option.Value,
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
        }
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
    IInstanceAutomationService automation) : IInstanceManagementService
{
    public async Task<IReadOnlyList<InstanceDto>> GetAllAsync(Guid? flowId, string? status, string? search, CancellationToken cancellationToken)
    {
        var query = db.Instances
            .AsNoTracking()
            .Include(x => x.FlowDefinition)
                .ThenInclude(x => x.Steps)
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
        return rows.Select(ToDto).ToList();
    }

    public async Task<InstanceDto> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        return ToDto(item);
    }

    public async Task<Guid> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken)
    {
        var flow = await db.Flows
            .Include(x => x.Tokens)
            .Include(x => x.Steps)
                .ThenInclude(x => x.Fields)
            .SingleOrDefaultAsync(x => x.Id == request.FlowDefinitionId && x.Active && x.LifecycleStatus == FlowLifecycleStatus.Published, cancellationToken)
            ?? throw new AppNotFoundException("Fluxo publicado nao encontrado.");

        var orderedSteps = flow.Steps.OrderBy(x => x.Order).ToList();
        var firstStep = orderedSteps.FirstOrDefault();

        if (firstStep is not null)
        {
            var missing = firstStep.Fields
                .Where(x => x.Required)
                .Where(x => !request.Data.TryGetValue(x.Key, out var value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || string.IsNullOrWhiteSpace(value.ToString()))
                .Select(x => x.Label)
                .ToArray();

            if (missing.Length > 0)
            {
                throw new AppValidationException(new Dictionary<string, string[]> { ["required"] = missing });
            }
        }

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

        if (current.FlowStep.Type == StepType.ApiSend || current.FlowStep.Type == StepType.ApiQuery || current.FlowStep.Type == StepType.Automatic)
        {
            throw new AppConflictException("A etapa atual eh automatica. Use o retry de integracao se necessario.");
        }

        CompleteCurrentStep(item, current, request.Notes, actorUserId);
        await db.SaveChangesAsync(cancellationToken);
        await automation.ProcessAsync(item.Id, cancellationToken);
    }

    public async Task<InstanceDto> RetryIntegrationAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await LoadInstance().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppNotFoundException("Execucao nao encontrada.");

        await automation.ProcessAsync(item.Id, cancellationToken, forceFailedCurrent: true);
        var reloaded = await LoadInstance().AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        return ToDto(reloaded);
    }

    private IQueryable<FlowInstance> LoadInstance()
    {
        return db.Instances
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

    private static InstanceDto ToDto(FlowInstance item)
    {
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
            item.StepExecutions
                .OrderBy(x => x.FlowStep.Order)
                .Select(x => new StepProgressDto(
                    x.Id,
                    x.FlowStep.Name,
                    x.FlowStep.Order,
                    x.FlowStep.Type,
                    x.Status,
                    x.StartedAt,
                    x.CompletedAt))
                .ToList());
    }
}
