using System.Text.Json;
using AutoMapper;
using FlowTrack.Domain;

namespace FlowTrack.Application;

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, UserDto User);
public record UserDto(Guid Id, string Name, string Email, string Role, bool Active);
public record CreateUserRequest(string Name, string Email, string Password, string Role);
public record UpdateUserRequest(string Name, string Role, bool Active, string? Password);
public record FieldOptionDto(Guid? Id, string Label, string Value, int Order);
public record FieldDto(Guid? Id, string Key, string Label, FieldType Type, bool Required, int Order, IReadOnlyList<FieldOptionDto> Options);
public record FlowTokenDto(Guid? Id, string Name, string? Value, TokenType Type, string? HeaderName, bool Active);
public record StepApiConfigDto(string? Url, string? Method, string? TokenName, string? ScheduleMode, string? ScheduleValue, string? QueryTemplate, bool ValidateTls);
public record StepDto(Guid? Id, string Name, string? Description, StepType Type, int Order, Guid? AssignedUserId, IReadOnlyList<FieldDto> Fields, StepApiConfigDto? ApiConfig);
public record FlowDto(Guid Id, Guid FlowKey, string Name, string Description, bool Active, int VersionNumber, string LifecycleStatus, DateTime? PublishedAt, bool HasDraft, IReadOnlyList<FlowTokenDto> Tokens, IReadOnlyList<StepDto> Steps);
public record SaveFlowRequest(string Name, string Description, bool Active, IReadOnlyList<FlowTokenDto> Tokens, IReadOnlyList<StepDto> Steps);
public record IntegrationAttemptDto(Guid Id, string TriggerType, string Method, string Url, int? ResponseStatusCode, bool Success, int DurationMs, DateTime CreatedAt, string? ResponsePreview, string? ErrorMessage);
public record IntegrationTestRequest(Dictionary<string, JsonElement> Data);
public record IntegrationTestResponse(bool Success, int? StatusCode, int DurationMs, string Url, string Method, string? ResponsePreview, string? ErrorMessage);
public record CreateInstanceRequest(Guid FlowDefinitionId, string? Code, Dictionary<string, JsonElement> Data);
public record AdvanceStepRequest(string? Notes);
public record StepProgressDto(Guid Id, string Name, int Order, StepType Type, StepStatus Status, DateTime? StartedAt, DateTime? CompletedAt);
public record InstanceDto(Guid Id, Guid FlowDefinitionId, string FlowName, string Code, InstanceStatus Status, int CurrentStepOrder, DateTime CreatedAt, DateTime UpdatedAt, Dictionary<string, JsonElement> Data, IReadOnlyList<StepProgressDto> Steps);
public record PdfExtractionDto(Dictionary<string, string> Fields, IReadOnlyList<string> Warnings);

public interface IAppDbContext
{
    IQueryable<AppUser> Users { get; }
    IQueryable<FlowDefinition> Flows { get; }
    IQueryable<FlowInstance> Instances { get; }
    void Add<T>(T entity) where T : class;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface ITokenService { string Create(AppUser user); }
public interface IPasswordService { string Hash(AppUser user, string password); bool Verify(AppUser user, string hash, string password); }
public interface IPdfExtractionService { Task<PdfExtractionDto> ExtractAsync(Stream stream, CancellationToken cancellationToken); }
public interface IIntegrationExecutionService
{
    Task<IntegrationTestResponse> ExecuteAsync(
        FlowDefinition flow,
        FlowStep step,
        Dictionary<string, JsonElement> data,
        CancellationToken cancellationToken,
        FlowInstance? instance = null,
        StepExecution? stepExecution = null,
        IntegrationTriggerType triggerType = IntegrationTriggerType.Runtime);
}

public sealed class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<StepFieldOption, FieldOptionDto>();
        CreateMap<StepField, FieldDto>();
        CreateMap<FlowToken, FlowTokenDto>().ForMember(x => x.Value, o => o.Ignore());
    }
}
