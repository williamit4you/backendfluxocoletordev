using System.Text.Json;
using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;

var appSettingsPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "backend", "src", "FlowTrack.API", "appsettings.json"));
if (!File.Exists(appSettingsPath))
{
    Console.Error.WriteLine($"Arquivo nao encontrado: {appSettingsPath}");
    return 1;
}

using var appSettingsDocument = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
var connectionString = appSettingsDocument.RootElement
    .GetProperty("ConnectionStrings")
    .GetProperty("DefaultConnection")
    .GetString();

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings:DefaultConnection nao encontrado em appsettings.json");
    return 1;
}

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var db = new AppDbContext(options);

const string flowName = "Consulta NF-e automatica";
var existingFlow = await db.FlowDefinitions.FirstOrDefaultAsync(x => x.Name == flowName);
if (existingFlow is not null)
{
    Console.WriteLine($"Fluxo '{flowName}' ja existe com Id {existingFlow.Id}.");
    return 0;
}

db.FlowDefinitions.Add(CreateConsultaNfeAutomaticaFlow());
await db.SaveChangesAsync();

var createdFlow = await db.FlowDefinitions.AsNoTracking().FirstAsync(x => x.Name == flowName);
Console.WriteLine($"Fluxo '{flowName}' criado com Id {createdFlow.Id}.");
return 0;

static FlowDefinition CreateConsultaNfeAutomaticaFlow()
{
    return new FlowDefinition
    {
        FlowKey = Guid.NewGuid(),
        Name = "Consulta NF-e automatica",
        Description = "Fluxo de exemplo para consultar a chave de acesso e preencher automaticamente os dados da nota fiscal.",
        Active = true,
        VersionNumber = 1,
        LifecycleStatus = FlowLifecycleStatus.Published,
        PublishedAt = DateTime.UtcNow,
        Tokens = [],
        Steps =
        [
            new FlowStep
            {
                Name = "Leitura da NF-e",
                Type = StepType.Reader,
                Order = 1,
                Fields =
                [
                    new() { Key = "nfe_chave_acesso", Label = "Chave de acesso", Type = FieldType.Text, Required = true, Order = 1, ConfigurationJson = """{"enableNfeLookup":true,"nfeLookupRole":"accessKey"}""" },
                    new() { Key = "nfe_numero", Label = "Numero da NF-e", Type = FieldType.Text, Required = false, Order = 2 },
                    new() { Key = "nfe_serie", Label = "Serie", Type = FieldType.Text, Required = false, Order = 3 },
                    new() { Key = "nfe_natureza_operacao", Label = "Natureza da operacao", Type = FieldType.Text, Required = false, Order = 4 },
                    new() { Key = "nfe_data_emissao", Label = "Data de emissao", Type = FieldType.Date, Required = false, Order = 5 },
                    new() { Key = "nfe_protocolo_autorizacao", Label = "Protocolo de autorizacao", Type = FieldType.Text, Required = false, Order = 6 },
                    new() { Key = "emitente_cnpj", Label = "CNPJ do emitente", Type = FieldType.Text, Mask = "cnpj", Required = false, Order = 7 },
                    new() { Key = "emitente_razao_social", Label = "Razao social do emitente", Type = FieldType.Text, Required = false, Order = 8 },
                    new() { Key = "destinatario_cnpj_cpf", Label = "CNPJ/CPF do destinatario", Type = FieldType.Text, Required = false, Order = 9 },
                    new() { Key = "destinatario_razao_social", Label = "Razao social do destinatario", Type = FieldType.Text, Required = false, Order = 10 },
                    new() { Key = "total_nota", Label = "Valor total da nota", Type = FieldType.Number, Required = false, Order = 11 },
                    new()
                    {
                        Key = "itens",
                        Label = "Itens",
                        Type = FieldType.Select,
                        Required = false,
                        Order = 12,
                        Options =
                        [
                            new() { Label = "Codigo do produto", Value = "codigo_produto", Key = "codigo_produto", Type = FieldType.Text, Order = 1 },
                            new() { Label = "Descricao", Value = "descricao", Key = "descricao", Type = FieldType.Text, Order = 2 },
                            new() { Label = "NCM", Value = "ncm", Key = "ncm", Type = FieldType.Text, Order = 3 },
                            new() { Label = "CST", Value = "cst", Key = "cst", Type = FieldType.Text, Order = 4 },
                            new() { Label = "CFOP", Value = "cfop", Key = "cfop", Type = FieldType.Text, Order = 5 },
                            new() { Label = "Unidade", Value = "unidade", Key = "unidade", Type = FieldType.Text, Order = 6 },
                            new() { Label = "Quantidade", Value = "quantidade", Key = "quantidade", Type = FieldType.Number, Order = 7 },
                            new() { Label = "Valor unitario", Value = "valor_unitario", Key = "valor_unitario", Type = FieldType.Number, Mask = "valor", Order = 8 },
                            new() { Label = "Valor total item", Value = "valor_total_item", Key = "valor_total_item", Type = FieldType.Number, Mask = "valor", Order = 9 }
                        ]
                    }
                ]
            },
            new FlowStep
            {
                Name = "Conferencia",
                Type = StepType.UserTask,
                Order = 2
            }
        ]
    };
}
