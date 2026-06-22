namespace FlowTrack.Domain;

public enum UserRole { SuperAdmin, Admin, User }
public enum EntryType { Manual, Reader, Automatic }
public enum StepType { Reader, UserTask, ExternalMonitor, Automatic }
public enum FieldType { Text, Number, Date, Document, Email, Select }
public enum InstanceStatus { InProgress, Completed, Cancelled }
public enum StepStatus { Pending, InProgress, Completed, Failed }

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
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public EntryType EntryType { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<FlowField> Fields { get; set; } = [];
    public List<FlowStep> Steps { get; set; } = [];
}

public sealed class FlowField : Entity
{
    public Guid FlowDefinitionId { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }
    public bool Required { get; set; }
    public int Order { get; set; }
    public string? OptionsJson { get; set; }
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
}
