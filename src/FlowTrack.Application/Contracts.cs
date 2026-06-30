using System.Text.Json;
using AutoMapper;
using FlowTrack.Domain;

namespace FlowTrack.Application;

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, UserDto User);
public record UserDto(Guid Id, string Name, string Email, string Role, bool Active);
public record CreateUserRequest(string Name, string Email, string Password, string Role);
public record UpdateUserRequest(string Name, string Role, bool Active, string? Password);
public record FieldOptionDto(Guid? Id, string Label, string Value, int Order, string? Key = null, FieldType? Type = null, string? Mask = null, bool? Required = null);
public record FieldDto(Guid? Id, string Key, string Label, FieldType Type, string? Mask, bool Required, int Order, IReadOnlyList<FieldOptionDto> Options);
public record FlowTokenDto(Guid? Id, string Name, string? Value, TokenType Type, string? HeaderName, bool Active);
public record MinioBucketDto(Guid? Id, string Name, string BucketName, string? Description, bool Active, bool IsDefault);
public record MinioConfigurationDto(Guid? Id, string Endpoint, string AccessKey, string SecretKey, string PublicUrl, bool Active, IReadOnlyList<MinioBucketDto> Buckets);
public record SaveMinioConfigurationRequest(string Endpoint, string AccessKey, string SecretKey, string PublicUrl, bool Active, IReadOnlyList<MinioBucketDto> Buckets);
public record ResponseFieldMappingDto(string FieldKey, string ResponsePath);
public record BodyFieldMappingDto(string TargetKey, string SourceReference);
public record RequestHeaderDto(string Name, string Value);
public record StepScheduleAssistDto(int? IntervalMinutes = null, string? CronExpression = null, string? HelperText = null);
public record ResponseRuleDto(bool Enabled = false, string? TargetPath = "$", string? ExpectedType = "array", string? EmptyBehavior = "advance", string? NonEmptyBehavior = "advance", int? RetryIntervalMinutes = null, int? MaxAttempts = null, string? FailureBehavior = "fail", string? Description = null);
public record StepApiConfigDto(string? Url, string? Method, string? TokenName, string? ScheduleMode, string? ScheduleValue, string? QueryTemplate, bool ValidateTls, IReadOnlyList<string>? SendFieldKeys = null, IReadOnlyList<ResponseFieldMappingDto>? ResponseMappings = null, IReadOnlyList<BodyFieldMappingDto>? BodyMappings = null, StepScheduleAssistDto? ScheduleAssist = null, IReadOnlyList<RequestHeaderDto>? Headers = null, string? BodyTemplate = null, bool RetryOnEmptyArray = false, int? EmptyArrayRetryMinutes = null, string? EmptyArrayAction = null, ResponseRuleDto? ResponseRule = null);
public record StepDto(Guid? Id, string Name, string? Description, StepType Type, int Order, IReadOnlyList<Guid> AssignedUserIds, IReadOnlyList<FieldDto> Fields, StepApiConfigDto? ApiConfig);
public record FlowDto(Guid Id, Guid FlowKey, string Name, string Description, bool Active, int VersionNumber, string LifecycleStatus, DateTime? PublishedAt, bool HasDraft, IReadOnlyList<FlowTokenDto> Tokens, IReadOnlyList<Guid> AssignedUserIds, IReadOnlyList<StepDto> Steps);
public record SaveFlowRequest(string Name, string Description, bool Active, IReadOnlyList<FlowTokenDto> Tokens, IReadOnlyList<Guid> AssignedUserIds, IReadOnlyList<StepDto> Steps);
public record IntegrationAttemptDto(Guid Id, string TriggerType, string Method, string Url, int? ResponseStatusCode, bool Success, int DurationMs, DateTime CreatedAt, string? RequestHeaders, string? RequestBody, string? ResponsePreview, string? ErrorMessage);
public record IntegrationTestRequest(Dictionary<string, JsonElement> Data);
public record ResponseRuleEvaluationDto(bool Enabled, string Status, string Action, string Reason, string TargetPath, string ExpectedType, bool IsEmpty, int AttemptCount, int? MaxAttempts, int? RetryIntervalMinutes, DateTime? NextAttemptAtUtc = null);
public record IntegrationTestResponse(bool Success, int? StatusCode, int DurationMs, string Url, string Method, string? ResponsePreview, string? ErrorMessage, Dictionary<string, string>? MappedFields = null, ResponseRuleEvaluationDto? ResponseRuleEvaluation = null);
public record IntegrationExecutionResult(bool Success, int? StatusCode, int DurationMs, string Url, string Method, string? RequestHeaders, string? RequestBody, string? ResponsePreview, string? ErrorMessage, Dictionary<string, JsonElement>? MappedData = null, bool AwaitingData = false, string? AwaitingDataMessage = null, int? RetryAfterMinutes = null, ResponseRuleEvaluationDto? ResponseRuleEvaluation = null);
public record UploadedFileDto(string Id, string FieldKey, string FileName, string ContentType, long Size, string Url, bool IsPhoto, DateTime UploadedAt);
public record CreateInstanceRequest(Guid FlowDefinitionId, string? Code, Dictionary<string, JsonElement> Data);
public record AdvanceStepRequest(string? Notes, Dictionary<string, JsonElement>? Data = null);
public record ExecutionFieldDto(Guid? Id, string Key, string Label, FieldType Type, string? Mask, bool Required, int Order, IReadOnlyList<FieldOptionDto> Options, string? Value);
public record StepProgressDto(Guid Id, Guid FlowStepId, string Name, int Order, StepType Type, StepStatus Status, DateTime? StartedAt, DateTime? CompletedAt, Guid? CompletedByUserId, string? CompletedByName, string? Notes, bool IsAutomatic, Dictionary<string, JsonElement> Data, IReadOnlyList<ExecutionFieldDto> Fields, IReadOnlyList<IntegrationAttemptDto> IntegrationAttempts);
public record InstanceDto(Guid Id, Guid FlowDefinitionId, string FlowName, string Code, InstanceStatus Status, int CurrentStepOrder, DateTime CreatedAt, DateTime UpdatedAt, Dictionary<string, JsonElement> Data, Guid? CurrentStepExecutionId, IReadOnlyList<StepProgressDto> Steps);
public record PdfExtractionDto(Dictionary<string, string> Fields, IReadOnlyList<string> Warnings);

public interface IAppDbContext
{
    IQueryable<AppUser> Users { get; }
    IQueryable<FlowDefinition> Flows { get; }
    IQueryable<FlowInstance> Instances { get; }
    IQueryable<FlowToken> Tokens { get; }
    IQueryable<FlowDefinitionUser> FlowAssignments { get; }
    IQueryable<FlowStepUser> StepAssignments { get; }
    IQueryable<FlowStep> Steps { get; }
    IQueryable<StepField> Fields { get; }
    IQueryable<StepFieldOption> FieldOptions { get; }
    IQueryable<StepExecution> StepExecutions { get; }
    IQueryable<IntegrationAttempt> IntegrationAttempts { get; }
    IQueryable<AuditEntry> AuditEntries { get; }
    IQueryable<MinioConfiguration> MinioConfigurations { get; }
    IQueryable<MinioBucket> MinioBuckets { get; }
    IQueryable<StoredFile> StoredFiles { get; }
    void Add<T>(T entity) where T : class;
    void RemoveRange<T>(IEnumerable<T> entities) where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface ITokenService { string Create(AppUser user); }
public interface IPasswordService { string Hash(AppUser user, string password); bool Verify(AppUser user, string hash, string password); }
public interface IPdfExtractionService { Task<PdfExtractionDto> ExtractAsync(Stream stream, CancellationToken cancellationToken); }
public interface IAuthService { Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken); }
public interface ITokenProtectionService
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}
public interface IIntegrationExecutionService
{
    Task<IntegrationExecutionResult> ExecuteAsync(
        FlowDefinition flow,
        FlowStep step,
        Dictionary<string, JsonElement> data,
        CancellationToken cancellationToken,
        FlowInstance? instance = null,
        StepExecution? stepExecution = null,
        IntegrationTriggerType triggerType = IntegrationTriggerType.Runtime);
}
public interface IInstanceAutomationService
{
    Task ProcessAsync(Guid instanceId, CancellationToken cancellationToken, bool forceFailedCurrent = false);
}
public interface IAuditService
{
    Task WriteAsync(string category, string action, string entityType, Guid entityId, string summary, Guid? actorUserId, CancellationToken cancellationToken);
}
public interface IPlatformConfigurationService
{
    Task<MinioConfigurationDto> GetMinioAsync(CancellationToken cancellationToken);
    Task<MinioConfigurationDto> SaveMinioAsync(SaveMinioConfigurationRequest request, Guid? actorUserId, CancellationToken cancellationToken);
}
public interface IFileStorageService
{
    Task<UploadedFileDto> SaveStepFileAsync(Guid instanceId, Guid stepExecutionId, string fieldKey, string fileName, string contentType, Stream stream, bool isPhoto, Guid? actorUserId, CancellationToken cancellationToken);
    Task<string> CreateReadUrlAsync(string bucketName, string objectKey, string fileName, CancellationToken cancellationToken);
}
public interface IWorkerMonitor
{
    DateTime? LastRunAtUtc { get; }
    DateTime? LastSuccessAtUtc { get; }
    string? LastError { get; }
    void MarkRun();
    void MarkSuccess();
    void MarkFailure(string error);
}

public interface IFlowManagementService
{
    Task<IReadOnlyList<FlowDto>> GetAllAsync(string? scope, CancellationToken cancellationToken);
    Task<FlowDto> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(SaveFlowRequest request, Guid? actorUserId, CancellationToken cancellationToken);
    Task<Guid> UpdateAsync(Guid id, SaveFlowRequest request, Guid? actorUserId, CancellationToken cancellationToken);
    Task<Guid> CreateDraftAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken);
    Task<Guid> PublishAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken);
    Task<IntegrationTestResponse> TestIntegrationAsync(Guid flowId, Guid stepId, IntegrationTestRequest request, CancellationToken cancellationToken);
}

public interface IUserManagementService
{
    Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken cancellationToken);
    Task<UserDto> CreateAsync(CreateUserRequest request, string? currentUserRole, Guid? actorUserId, CancellationToken cancellationToken);
    Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, string? currentUserRole, Guid? actorUserId, CancellationToken cancellationToken);
}

public interface IInstanceManagementService
{
    Task<IReadOnlyList<InstanceDto>> GetAllAsync(Guid? flowId, string? status, string? search, Guid? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstanceDto>> GetPendingTasksAsync(Guid? actorUserId, CancellationToken cancellationToken);
    Task<InstanceDto> GetByIdAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(CreateInstanceRequest request, Guid? actorUserId, CancellationToken cancellationToken);
    Task<InstanceDto> SaveCurrentStepDataAsync(Guid id, Dictionary<string, JsonElement> data, string? notes, Guid? actorUserId, CancellationToken cancellationToken);
    Task<InstanceDto> UploadCurrentStepFileAsync(Guid id, string fieldKey, string fileName, string? contentType, Stream stream, Guid? actorUserId, CancellationToken cancellationToken);
    Task AdvanceAsync(Guid id, AdvanceStepRequest request, Guid? actorUserId, CancellationToken cancellationToken);
    Task<InstanceDto> RetryIntegrationAsync(Guid id, Guid? actorUserId, CancellationToken cancellationToken);
    Task<InstanceDto> ReprocessStepAsync(Guid id, Guid stepExecutionId, Guid? actorUserId, CancellationToken cancellationToken);
}

public abstract class AppServiceException(string message) : Exception(message);
public sealed class AppValidationException(Dictionary<string, string[]> errors) : AppServiceException("Falha de validacao.")
{
    public Dictionary<string, string[]> Errors { get; } = errors;
}
public sealed class AppNotFoundException(string message) : AppServiceException(message);
public sealed class AppConflictException : AppServiceException
{
    public AppConflictException(string message) : base(message)
    {
    }

    public AppConflictException(string message, Exception? innerException) : base(message)
    {
        if (innerException is not null)
        {
            Data["InnerExceptionMessage"] = innerException.Message;
        }
    }
}
public sealed class AppForbiddenException(string message = "Operacao nao permitida.") : AppServiceException(message);

public sealed class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<StepFieldOption, FieldOptionDto>();
        CreateMap<StepField, FieldDto>();
        CreateMap<FlowToken, FlowTokenDto>().ForMember(x => x.Value, o => o.Ignore());
    }
}
