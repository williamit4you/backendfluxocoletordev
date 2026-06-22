using System.Security.Claims;
using System.Text.Json;
using AutoMapper;
using FlowTrack.Application;
using FlowTrack.Data;
using FlowTrack.Domain;
using FlowTrack.IoC;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFlowTrack(builder.Configuration);
var origins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:3000").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var api = app.MapGroup("/api");
api.MapPost("/auth/login", async (LoginRequest req, AppDbContext db, IPasswordService passwords, ITokenService tokens) =>
{
    var email = req.Email.Trim().ToLowerInvariant();
    var user = await db.AppUsers.SingleOrDefaultAsync(x => x.Email == email && x.Active);
    if (user is null || !passwords.Verify(user, user.PasswordHash, req.Password)) return Results.Unauthorized();
    return Results.Ok(new LoginResponse(tokens.Create(user), new UserDto(user.Id, user.Name, user.Email, user.Role.ToString())));
});

var secured = api.MapGroup("").RequireAuthorization();
secured.MapGet("/flows", async (AppDbContext db, IMapper mapper) =>
{
    var flows = await db.FlowDefinitions.AsNoTracking().Include(x => x.Fields).Include(x => x.Steps).OrderBy(x => x.Name).ToListAsync();
    return Results.Ok(flows.Select(x => ToFlowDto(x, mapper)));
});
secured.MapPost("/flows", [Authorize(Roles = "SuperAdmin")] async (CreateFlowRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || req.Steps.Count == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["flow"] = ["Nome e ao menos uma etapa são obrigatórios."] });
    var flow = new FlowDefinition { Name = req.Name.Trim(), Description = req.Description.Trim(), EntryType = req.EntryType };
    flow.Fields = req.Fields.Select((x, i) => new FlowField { Key = x.Key.Trim(), Label = x.Label.Trim(), Type = x.Type, Required = x.Required, Order = i + 1, OptionsJson = x.OptionsJson }).ToList();
    flow.Steps = req.Steps.Select((x, i) => new FlowStep { Name = x.Name.Trim(), Description = x.Description, Type = x.Type, Order = i + 1, AssignedUserId = x.AssignedUserId, ConfigurationJson = x.ConfigurationJson }).ToList();
    db.Add(flow); await db.SaveChangesAsync(); return Results.Created($"/api/flows/{flow.Id}", new { flow.Id });
});

secured.MapGet("/instances", async (Guid? flowId, string? status, string? search, AppDbContext db) =>
{
    var query = db.FlowInstances.AsNoTracking().Include(x => x.FlowDefinition).ThenInclude(x => x.Steps).Include(x => x.StepExecutions).ThenInclude(x => x.FlowStep).AsQueryable();
    if (flowId.HasValue) query = query.Where(x => x.FlowDefinitionId == flowId);
    if (Enum.TryParse<InstanceStatus>(status, true, out var parsed)) query = query.Where(x => x.Status == parsed);
    if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Code.ToLower().Contains(search.ToLower()));
    var rows = await query.OrderByDescending(x => x.UpdatedAt).Take(200).ToListAsync();
    return Results.Ok(rows.Select(ToInstanceDto));
});
secured.MapGet("/instances/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var item = await db.FlowInstances.AsNoTracking().Include(x => x.FlowDefinition).ThenInclude(x => x.Steps).Include(x => x.StepExecutions).ThenInclude(x => x.FlowStep).SingleOrDefaultAsync(x => x.Id == id);
    return item is null ? Results.NotFound() : Results.Ok(ToInstanceDto(item));
});
secured.MapPost("/instances", async (CreateInstanceRequest req, AppDbContext db) =>
{
    var flow = await db.FlowDefinitions.Include(x => x.Fields).Include(x => x.Steps).SingleOrDefaultAsync(x => x.Id == req.FlowDefinitionId && x.Active);
    if (flow is null) return Results.NotFound();
    var missing = flow.Fields.Where(x => x.Required && (!req.Data.TryGetValue(x.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || string.IsNullOrWhiteSpace(value.ToString()))).Select(x => x.Label).ToArray();
    if (missing.Length > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["required"] = missing });
    var now = DateTime.UtcNow; var ordered = flow.Steps.OrderBy(x => x.Order).ToList();
    var instance = new FlowInstance { FlowDefinitionId = flow.Id, Code = string.IsNullOrWhiteSpace(req.Code) ? $"FL-{now:yyyyMMddHHmmss}" : req.Code.Trim(), DataJson = JsonSerializer.Serialize(req.Data), CurrentStepOrder = ordered.FirstOrDefault()?.Order ?? 0 };
    instance.StepExecutions = ordered.Select((x, i) => new StepExecution { FlowStepId = x.Id, Status = i == 0 ? StepStatus.InProgress : StepStatus.Pending, StartedAt = i == 0 ? now : null }).ToList();
    if (ordered.Count == 0) instance.Status = InstanceStatus.Completed;
    db.Add(instance); await db.SaveChangesAsync(); return Results.Created($"/api/instances/{instance.Id}", new { instance.Id });
});
secured.MapPost("/instances/{id:guid}/advance", async (Guid id, AdvanceStepRequest req, ClaimsPrincipal principal, AppDbContext db) =>
{
    var item = await db.FlowInstances.Include(x => x.StepExecutions).ThenInclude(x => x.FlowStep).SingleOrDefaultAsync(x => x.Id == id);
    if (item is null) return Results.NotFound(); if (item.Status != InstanceStatus.InProgress) return Results.Conflict(new { message = "Execução não está em andamento." });
    var current = item.StepExecutions.SingleOrDefault(x => x.Status == StepStatus.InProgress); if (current is null) return Results.Conflict();
    var now = DateTime.UtcNow; current.Status = StepStatus.Completed; current.CompletedAt = now; current.Notes = req.Notes;
    if (Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"), out var userId)) current.CompletedByUserId = userId;
    var next = item.StepExecutions.Where(x => x.FlowStep.Order > current.FlowStep.Order).OrderBy(x => x.FlowStep.Order).FirstOrDefault();
    if (next is null) item.Status = InstanceStatus.Completed; else { next.Status = StepStatus.InProgress; next.StartedAt = now; item.CurrentStepOrder = next.FlowStep.Order; }
    item.UpdatedAt = now; await db.SaveChangesAsync(); return Results.NoContent();
});
secured.MapPost("/documents/nfe/extract", async (IFormFile file, IPdfExtractionService extraction, CancellationToken ct) =>
{
    if (file.Length == 0 || file.Length > 10_000_000 || !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "Envie um PDF de até 10 MB." });
    await using var stream = file.OpenReadStream(); return Results.Ok(await extraction.ExtractAsync(stream, ct));
}).DisableAntiforgery();

await SeedAsync(app.Services);
app.Run();

static FlowDto ToFlowDto(FlowDefinition x, IMapper mapper) => new(x.Id, x.Name, x.Description, x.EntryType, x.Active, x.Fields.OrderBy(f => f.Order).Select(mapper.Map<FieldDto>).ToList(), x.Steps.OrderBy(s => s.Order).Select(mapper.Map<StepDto>).ToList());
static InstanceDto ToInstanceDto(FlowInstance x) => new(x.Id, x.FlowDefinitionId, x.FlowDefinition.Name, x.Code, x.Status, x.CurrentStepOrder, x.CreatedAt, x.UpdatedAt, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(x.DataJson) ?? [], x.StepExecutions.OrderBy(s => s.FlowStep.Order).Select(s => new StepProgressDto(s.Id, s.FlowStep.Name, s.FlowStep.Order, s.FlowStep.Type, s.Status, s.StartedAt, s.CompletedAt)).ToList());
static async Task SeedAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (!await db.AppUsers.AnyAsync(x => x.Email == "diogo@it4you.inf.br"))
    {
        var user = new AppUser { Name = "Diogo", Email = "diogo@it4you.inf.br", Role = UserRole.SuperAdmin };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordService>().Hash(user, "Diogo#2026"); db.AppUsers.Add(user);
    }
    if (!await db.FlowDefinitions.AnyAsync(x => x.Name == "Recebimento de caminhão / NF-e"))
    {
        var flow = new FlowDefinition { Name = "Recebimento de caminhão / NF-e", Description = "Acompanhamento da entrada à saída de produção.", EntryType = EntryType.Reader };
        var fields = new[] { ("chaveAcesso", "Chave de acesso", FieldType.Document, true), ("numeroNfe", "Número da NF-e", FieldType.Text, true), ("serie", "Série", FieldType.Text, false), ("emitente", "Emitente", FieldType.Text, false), ("cnpjEmitente", "CNPJ do emitente", FieldType.Document, false), ("destinatario", "Destinatário", FieldType.Text, false), ("cnpjDestinatario", "CNPJ do destinatário", FieldType.Document, false), ("dataEmissao", "Data de emissão", FieldType.Date, false), ("valorTotal", "Valor total", FieldType.Number, false), ("placa", "Placa", FieldType.Text, false), ("motorista", "Motorista", FieldType.Text, false), ("observacoes", "Observações", FieldType.Text, false) };
        flow.Fields = fields.Select((x, i) => new FlowField { Key = x.Item1, Label = x.Item2, Type = x.Item3, Required = x.Item4, Order = i + 1 }).ToList();
        var steps = new[] { ("Entrada do caminhão", StepType.Reader), ("Saída para Sandra", StepType.UserTask), ("Entrada no Alvo / ERP", StepType.ExternalMonitor), ("Entrada na sala de inspeção", StepType.ExternalMonitor), ("Lote aprovado", StepType.ExternalMonitor), ("Saída de produção", StepType.ExternalMonitor) };
        flow.Steps = steps.Select((x, i) => new FlowStep { Name = x.Item1, Type = x.Item2, Order = i + 1, ConfigurationJson = x.Item2 == StepType.ExternalMonitor ? "{\"source\":\"pending-configuration\"}" : null }).ToList(); db.FlowDefinitions.Add(flow);
    }
    await db.SaveChangesAsync();
}

public partial class Program { }
