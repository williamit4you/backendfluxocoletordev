namespace FlowTrack.Domain;

public enum UserRole { SuperAdmin, Admin, User }
public enum EntryType { Manual, Reader, Automatic, ApiSend, ApiQuery }
public enum StepType { Reader, UserTask, ExternalMonitor, Automatic, ApiSend, ApiQuery }
public enum FieldType { Text, Number, Date, Document, Email, Select, Boolean, Attachment, Photo, Radio }
public enum InstanceStatus { InProgress, Completed, Cancelled }
public enum StepStatus { Pending, InProgress, Completed, Failed }
public enum TokenType { Bearer, ApiKey }
public enum FlowLifecycleStatus { Draft, Published, Archived }
public enum IntegrationTriggerType { Runtime, Test }

public abstract class Entity { public Guid Id { get; set; } = Guid.NewGuid(); }

public sealed class AppUser : Entity
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class FlowDefinition : Entity
{
    public Guid FlowKey { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Active { get; set; } = true;
    public int VersionNumber { get; set; } = 1;
    public FlowLifecycleStatus LifecycleStatus { get; set; } = FlowLifecycleStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public List<FlowToken> Tokens { get; set; } = [];
    public List<FlowStep> Steps { get; set; } = [];
}

public sealed class FlowToken : Entity
{
    public Guid FlowDefinitionId { get; set; }
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public TokenType Type { get; set; } = TokenType.Bearer;
    public string? HeaderName { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class StepField : Entity
{
    public Guid FlowStepId { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }
    public string? Mask { get; set; }
    public bool Required { get; set; }
    public int Order { get; set; }
    public List<StepFieldOption> Options { get; set; } = [];
}

public sealed class StepFieldOption : Entity
{
    public Guid StepFieldId { get; set; }
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public int Order { get; set; }
}

public sealed class FlowStep : Entity
{
    public Guid FlowDefinitionId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public StepType Type { get; set; }
    public int Order { get; set; }
    public Guid? AssignedUserId { get; set; }
    public string? ConfigurationJson { get; set; }
    public List<StepField> Fields { get; set; } = [];
}

public sealed class FlowInstance : Entity
{
    public Guid FlowDefinitionId { get; set; }
    public FlowDefinition FlowDefinition { get; set; } = null!;
    public string Code { get; set; } = "";
    public string DataJson { get; set; } = "{}";
    public InstanceStatus Status { get; set; } = InstanceStatus.InProgress;
    public int CurrentStepOrder { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<StepExecution> StepExecutions { get; set; } = [];
    public List<IntegrationAttempt> IntegrationAttempts { get; set; } = [];
}

public sealed class StepExecution : Entity
{
    public Guid FlowInstanceId { get; set; }
    public Guid FlowStepId { get; set; }
    public FlowStep FlowStep { get; set; } = null!;
    public StepStatus Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public string? Notes { get; set; }
    public string DataJson { get; set; } = "{}";
}

public sealed class IntegrationAttempt : Entity
{
    public Guid? FlowInstanceId { get; set; }
    public Guid FlowStepId { get; set; }
    public Guid? StepExecutionId { get; set; }
    public IntegrationTriggerType TriggerType { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public int? ResponseStatusCode { get; set; }
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? ResponsePreview { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class AuditEntry : Entity
{
    public Guid? ActorUserId { get; set; }
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Summary { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
