using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowTrack.API.Infrastructure;

internal static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<FlowTrack.Application.IPasswordService>();
        var tokenProtection = scope.ServiceProvider.GetRequiredService<FlowTrack.Application.ITokenProtectionService>();

        await db.Database.MigrateAsync();
        await EnsureIntegrationAttemptColumnsAsync(db);

        if (!await db.AppUsers.AnyAsync(x => x.Email == "diogo@it4you.inf.br"))
        {
            var user = new AppUser
            {
                Name = "Diogo",
                Email = "diogo@it4you.inf.br",
                Role = UserRole.SuperAdmin
            };
            user.PasswordHash = passwords.Hash(user, "Diogo#2026");
            db.AppUsers.Add(user);
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
                            new() { Key = "chaveAcesso", Label = "Chave de acesso", Type = FieldType.Text, Required = true, Order = 1 },
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
            minioConfig = new MinioConfiguration
            {
                Endpoint = "https://desenvolvimento-minio.ykzlki.easypanel.host",
                PublicUrl = "https://desenvolvimento-minio.ykzlki.easypanel.host",
                AccessKey = tokenProtection.Protect("admin"),
                SecretKey = tokenProtection.Protect("VgrZtPnAiHmmUjEn"),
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

    private static Task EnsureIntegrationAttemptColumnsAsync(AppDbContext db)
    {
        return db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "IntegrationAttempts" ADD COLUMN IF NOT EXISTS "RequestHeaders" text;
            ALTER TABLE "IntegrationAttempts" ADD COLUMN IF NOT EXISTS "RequestBody" text;
            """);
    }
}
