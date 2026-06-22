using System.Text.Json;
using AutoMapper;
using FlowTrack.Domain;

namespace FlowTrack.Application;

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, UserDto User);
public record UserDto(Guid Id, string Name, string Email, string Role);
public record FieldDto(Guid? Id, string Key, string Label, FieldType Type, bool Required, int Order, string? OptionsJson);
public record StepDto(Guid? Id, string Name, string? Description, StepType Type, int Order, Guid? AssignedUserId, string? ConfigurationJson);
public record FlowDto(Guid Id, string Name, string Description, EntryType EntryType, bool Active, IReadOnlyList<FieldDto> Fields, IReadOnlyList<StepDto> Steps);
public record CreateFlowRequest(string Name, string Description, EntryType EntryType, IReadOnlyList<FieldDto> Fields, IReadOnlyList<StepDto> Steps);
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

public sealed class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<FlowField, FieldDto>();
        CreateMap<FlowStep, StepDto>();
    }
}
