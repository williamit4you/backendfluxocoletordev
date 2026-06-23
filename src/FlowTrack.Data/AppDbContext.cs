using FlowTrack.Application;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<FlowDefinition> FlowDefinitions => Set<FlowDefinition>();
    public DbSet<FlowToken> FlowTokens => Set<FlowToken>();
    public DbSet<FlowStep> FlowSteps => Set<FlowStep>();
    public DbSet<StepField> StepFields => Set<StepField>();
    public DbSet<StepFieldOption> StepFieldOptions => Set<StepFieldOption>();
    public DbSet<FlowInstance> FlowInstances => Set<FlowInstance>();
    public DbSet<StepExecution> StepExecutions => Set<StepExecution>();
    public DbSet<IntegrationAttempt> IntegrationAttempts => Set<IntegrationAttempt>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public IQueryable<AppUser> Users => AppUsers;
    public IQueryable<FlowDefinition> Flows => FlowDefinitions;
    public IQueryable<FlowInstance> Instances => FlowInstances;
    public IQueryable<FlowToken> Tokens => FlowTokens;
    public IQueryable<FlowStep> Steps => FlowSteps;
    public IQueryable<StepField> Fields => StepFields;
    public IQueryable<StepFieldOption> FieldOptions => StepFieldOptions;
    IQueryable<StepExecution> IAppDbContext.StepExecutions => StepExecutions;
    IQueryable<IntegrationAttempt> IAppDbContext.IntegrationAttempts => IntegrationAttempts;
    IQueryable<AuditEntry> IAppDbContext.AuditEntries => AuditEntries;
    public new void Add<T>(T entity) where T : class => Set<T>().Add(entity);
    public void RemoveRange<T>(IEnumerable<T> entities) where T : class => Set<T>().RemoveRange(entities);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresEnum<UserRole>(); b.HasPostgresEnum<EntryType>(); b.HasPostgresEnum<StepType>();
        b.HasPostgresEnum<FieldType>(); b.HasPostgresEnum<InstanceStatus>(); b.HasPostgresEnum<StepStatus>(); b.HasPostgresEnum<TokenType>(); b.HasPostgresEnum<FlowLifecycleStatus>(); b.HasPostgresEnum<IntegrationTriggerType>();
        b.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        b.Entity<FlowToken>().HasIndex(x => new { x.FlowDefinitionId, x.Name }).IsUnique();
        b.Entity<FlowStep>().HasIndex(x => new { x.FlowDefinitionId, x.Order }).IsUnique();
        b.Entity<FlowDefinition>().HasIndex(x => new { x.FlowKey, x.VersionNumber }).IsUnique();
        b.Entity<StepField>().HasIndex(x => new { x.FlowStepId, x.Key }).IsUnique();
        b.Entity<StepFieldOption>().HasIndex(x => new { x.StepFieldId, x.Order }).IsUnique();
        b.Entity<FlowDefinition>().HasMany(x => x.Tokens).WithOne().HasForeignKey(x => x.FlowDefinitionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlowDefinition>().HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.FlowDefinitionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlowStep>().HasMany(x => x.Fields).WithOne().HasForeignKey(x => x.FlowStepId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StepField>().HasMany(x => x.Options).WithOne().HasForeignKey(x => x.StepFieldId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlowInstance>().HasMany(x => x.StepExecutions).WithOne().HasForeignKey(x => x.FlowInstanceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlowInstance>().HasMany(x => x.IntegrationAttempts).WithOne().HasForeignKey(x => x.FlowInstanceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StepExecution>().HasOne(x => x.FlowStep).WithMany().HasForeignKey(x => x.FlowStepId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<IntegrationAttempt>().HasIndex(x => x.FlowInstanceId);
        b.Entity<IntegrationAttempt>().HasIndex(x => x.FlowStepId);
        b.Entity<AuditEntry>().HasIndex(x => x.CreatedAt);
        b.Entity<FlowDefinition>().Property(x => x.Name).HasMaxLength(160);
        b.Entity<FlowInstance>().Property(x => x.Code).HasMaxLength(120);
        b.Entity<FlowToken>().Property(x => x.Name).HasMaxLength(120);
        b.Entity<FlowToken>().Property(x => x.Value).HasMaxLength(2000);
        b.Entity<FlowStep>().Property(x => x.Name).HasMaxLength(160);
        b.Entity<StepField>().Property(x => x.Key).HasMaxLength(120);
        b.Entity<StepField>().Property(x => x.Label).HasMaxLength(160);
        b.Entity<StepFieldOption>().Property(x => x.Label).HasMaxLength(120);
        b.Entity<StepFieldOption>().Property(x => x.Value).HasMaxLength(240);
        b.Entity<IntegrationAttempt>().Property(x => x.Method).HasMaxLength(10);
        b.Entity<IntegrationAttempt>().Property(x => x.Url).HasMaxLength(2000);
        b.Entity<IntegrationAttempt>().Property(x => x.ResponsePreview).HasMaxLength(2000);
        b.Entity<IntegrationAttempt>().Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.Entity<AuditEntry>().Property(x => x.Category).HasMaxLength(80);
        b.Entity<AuditEntry>().Property(x => x.Action).HasMaxLength(80);
        b.Entity<AuditEntry>().Property(x => x.EntityType).HasMaxLength(80);
        b.Entity<AuditEntry>().Property(x => x.Summary).HasMaxLength(1000);
    }
}
