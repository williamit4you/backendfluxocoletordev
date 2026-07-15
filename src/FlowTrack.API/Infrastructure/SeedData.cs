using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowTrack.API.Infrastructure;

internal static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SeedData");
        var passwords = scope.ServiceProvider.GetRequiredService<FlowTrack.Application.IPasswordService>();
        var tokenProtection = scope.ServiceProvider.GetRequiredService<FlowTrack.Application.ITokenProtectionService>();

        await db.Database.MigrateAsync();
        await EnsureRuntimeColumnsAsync(db);

        var seedAdminEmail = configuration["Seed:AdminEmail"];
        var seedAdminPassword = configuration["Seed:AdminPassword"];
        var seedMinioEndpoint = configuration["Seed:Minio:Endpoint"];
        var seedMinioPublicUrl = configuration["Seed:Minio:PublicUrl"];
        var seedMinioAccessKey = configuration["Seed:Minio:AccessKey"];
        var seedMinioSecretKey = configuration["Seed:Minio:SecretKey"];

        if (!string.IsNullOrWhiteSpace(seedAdminEmail) &&
            !string.IsNullOrWhiteSpace(seedAdminPassword) &&
            !await db.AppUsers.AnyAsync(x => x.Email == seedAdminEmail))
        {
            var user = new AppUser
            {
                Name = "Diogo",
                Email = seedAdminEmail,
                Role = UserRole.SuperAdmin
            };
            user.PasswordHash = passwords.Hash(user, seedAdminPassword);
            db.AppUsers.Add(user);
        }
        else if (string.IsNullOrWhiteSpace(seedAdminEmail) || string.IsNullOrWhiteSpace(seedAdminPassword))
        {
            logger.LogWarning("Seed do administrador ignorado porque Seed:AdminEmail ou Seed:AdminPassword nao foi configurado.");
        }

        if (!await db.FlowDefinitions.AnyAsync(x => x.Name == "Recebimento de caminhão / NF-e"))
        {
            var flow = new FlowDefinition
            {
                FlowKey = Guid.NewGuid(),
                Name = "Recebimento de caminhão / NF-e",
                Description = "Acompanhamento da entrada até a saída de produção.",
                Active = true,
                VersionNumber = 1,
                LifecycleStatus = FlowLifecycleStatus.Published,
                PublishedAt = DateTime.UtcNow,
                Tokens = [],
                Steps =
                [
                    new FlowStep
                    {
                        Name = "Entrada do caminhão",
                        Type = StepType.Reader,
                        Order = 1,
                        Fields =
                        [
                            new() { Key = "chaveAcesso", Label = "Chave de acesso", Type = FieldType.Text, Required = true, Order = 1, ConfigurationJson = """{"enableNfeLookup":true,"nfeLookupRole":"accessKey"}""" },
                            new() { Key = "numeroNfe", Label = "Número da NF-e", Type = FieldType.Text, Required = true, Order = 2 },
                            new() { Key = "serie", Label = "Série", Type = FieldType.Text, Required = false, Order = 3 },
                            new() { Key = "emitente", Label = "Emitente", Type = FieldType.Text, Required = false, Order = 4 },
                            new() { Key = "cnpjEmitente", Label = "CNPJ do emitente", Type = FieldType.Text, Mask = "cnpj", Required = false, Order = 5 },
                            new() { Key = "destinatario", Label = "Destinatário", Type = FieldType.Text, Required = false, Order = 6 },
                            new() { Key = "cnpjDestinatario", Label = "CNPJ do destinatário", Type = FieldType.Text, Mask = "cnpj", Required = false, Order = 7 },
                            new() { Key = "dataEmissao", Label = "Data de emissão", Type = FieldType.Date, Required = false, Order = 8 },
                            new() { Key = "valorTotal", Label = "Valor total", Type = FieldType.Number, Required = false, Order = 9 },
                            new() { Key = "placa", Label = "Placa", Type = FieldType.Text, Required = false, Order = 10 },
                            new() { Key = "motorista", Label = "Motorista", Type = FieldType.Text, Required = false, Order = 11 },
                            new() { Key = "observacoes", Label = "Observações", Type = FieldType.Text, Required = false, Order = 12 }
                        ]
                    },
                    new() { Name = "Saída", Type = StepType.UserTask, Order = 2 },
                    new() { Name = "Entrada no Alvo / ERP", Type = StepType.ExternalMonitor, Order = 3 },
                    new() { Name = "Entrada na sala de inspeção", Type = StepType.ExternalMonitor, Order = 4 },
                    new() { Name = "Lote aprovado", Type = StepType.ExternalMonitor, Order = 5 },
                    new() { Name = "Saída de produção", Type = StepType.ExternalMonitor, Order = 6 }
                ]
            };

            db.FlowDefinitions.Add(flow);
        }

        if (!await db.FlowDefinitions.AnyAsync(x => x.Name == "Consulta NF-e automatica"))
        {
            db.FlowDefinitions.Add(CreateConsultaNfeAutomaticaFlow());
        }

        var legacyFields = await db.StepFields
            .Where(x => x.Key == "chaveAcesso" || x.Key == "cnpjEmitente" || x.Key == "cnpjDestinatario")
            .ToListAsync();

        foreach (var field in legacyFields)
        {
            field.Type = FieldType.Text;

            if (field.Key.Contains("cnpj", StringComparison.OrdinalIgnoreCase))
            {
                field.Mask = "cnpj";
            }
        }

        var minioConfig = await db.MinioConfigurationEntries
            .Include(x => x.Buckets)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync();

        if (minioConfig is null)
        {
            if (string.IsNullOrWhiteSpace(seedMinioEndpoint) ||
                string.IsNullOrWhiteSpace(seedMinioPublicUrl) ||
                string.IsNullOrWhiteSpace(seedMinioAccessKey) ||
                string.IsNullOrWhiteSpace(seedMinioSecretKey))
            {
                logger.LogWarning("Seed da configuracao MinIO ignorado porque Seed:Minio nao foi configurado.");
            }
            else
            {
                minioConfig = new MinioConfiguration
                {
                    Endpoint = seedMinioEndpoint,
                    PublicUrl = seedMinioPublicUrl,
                    AccessKey = tokenProtection.Protect(seedMinioAccessKey),
                    SecretKey = tokenProtection.Protect(seedMinioSecretKey),
                    Active = true,
                    Buckets =
                    [
                        new MinioBucket
                        {
                            Name = "Anexos gerais",
                            BucketName = "coletoranexo",
                            Description = "Bucket unico para anexos e fotos do FlowTrack.",
                            Active = true,
                            IsDefault = true
                        }
                    ]
                };

                db.MinioConfigurationEntries.Add(minioConfig);
            }
        }
        else if (!minioConfig.Buckets.Any())
        {
            minioConfig.Buckets.Add(new MinioBucket
            {
                Name = "Anexos gerais",
                BucketName = "coletoranexo",
                Description = "Bucket unico para anexos e fotos do FlowTrack.",
                Active = true,
                IsDefault = true
            });
        }

        await db.SaveChangesAsync();
    }

    private static Task EnsureRuntimeColumnsAsync(AppDbContext db)
    {
        return db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "IntegrationAttempts" ADD COLUMN IF NOT EXISTS "RequestHeaders" text;
            ALTER TABLE "IntegrationAttempts" ADD COLUMN IF NOT EXISTS "RequestBody" text;
            ALTER TABLE "IntegrationAttempts" ALTER COLUMN "ResponsePreview" TYPE text;
            ALTER TABLE "StepFields" ADD COLUMN IF NOT EXISTS "ConfigurationJson" text;
            """);
    }

    private static FlowDefinition CreateConsultaNfeAutomaticaFlow()
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
}
