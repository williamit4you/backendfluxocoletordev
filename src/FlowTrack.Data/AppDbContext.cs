using FlowTrack.Application;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlowTrack.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<FlowDefinition> FlowDefinitions => Set<FlowDefinition>();
    public DbSet<FlowField> FlowFields => Set<FlowField>();
    public DbSet<FlowStep> FlowSteps => Set<FlowStep>();
    public DbSet<FlowInstance> FlowInstances => Set<FlowInstance>();
    public DbSet<StepExecution> StepExecutions => Set<StepExecution>();
    public IQueryable<AppUser> Users => AppUsers;
    public IQueryable<FlowDefinition> Flows => FlowDefinitions;
    public IQueryable<FlowInstance> Instances => FlowInstances;
    public new void Add<T>(T entity) where T : class => Set<T>().Add(entity);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresEnum<UserRole>(); b.HasPostgresEnum<EntryType>(); b.HasPostgresEnum<StepType>();
        b.HasPostgresEnum<FieldType>(); b.HasPostgresEnum<InstanceStatus>(); b.HasPostgresEnum<StepStatus>();
        b.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        b.Entity<FlowField>().HasIndex(x => new { x.FlowDefinitionId, x.Key }).IsUnique();
        b.Entity<FlowStep>().HasIndex(x => new { x.FlowDefinitionId, x.Order }).IsUnique();
        b.Entity<FlowDefinition>().HasMany(x => x.Fields).WithOne().HasForeignKey(x => x.FlowDefinitionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlowDefinition>().HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.FlowDefinitionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FlowInstance>().HasMany(x => x.StepExecutions).WithOne().HasForeignKey(x => x.FlowInstanceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StepExecution>().HasOne(x => x.FlowStep).WithMany().HasForeignKey(x => x.FlowStepId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<FlowDefinition>().Property(x => x.Name).HasMaxLength(160);
        b.Entity<FlowInstance>().Property(x => x.Code).HasMaxLength(120);
    }
}
