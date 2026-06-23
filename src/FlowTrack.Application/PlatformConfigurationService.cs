using Microsoft.EntityFrameworkCore;

namespace FlowTrack.Application;

public sealed class PlatformConfigurationService(
    IAppDbContext db,
    ITokenProtectionService tokenProtection,
    IAuditService audit) : IPlatformConfigurationService
{
    public async Task<MinioConfigurationDto> GetMinioAsync(CancellationToken cancellationToken)
    {
        var config = await db.MinioConfigurations
            .AsNoTracking()
            .Include(x => x.Buckets)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (config is null)
        {
            return new MinioConfigurationDto(null, "", "", "", "", true, []);
        }

        return ToDto(config);
    }

    public async Task<MinioConfigurationDto> SaveMinioAsync(SaveMinioConfigurationRequest request, Guid? actorUserId, CancellationToken cancellationToken)
    {
        Validate(request);

        var config = await db.MinioConfigurations
            .Include(x => x.Buckets)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (config is null)
        {
            config = new Domain.MinioConfiguration();
            db.Add(config);
        }

        config.Endpoint = request.Endpoint.Trim();
        config.PublicUrl = string.IsNullOrWhiteSpace(request.PublicUrl) ? request.Endpoint.Trim() : request.PublicUrl.Trim();
        config.AccessKey = tokenProtection.Protect(request.AccessKey.Trim());
        config.SecretKey = tokenProtection.Protect(request.SecretKey.Trim());
        config.Active = request.Active;
        config.UpdatedAt = DateTime.UtcNow;

        db.RemoveRange(config.Buckets);
        config.Buckets = request.Buckets
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.BucketName))
            .Select((bucket, index) => new Domain.MinioBucket
            {
                Name = bucket.Name.Trim(),
                BucketName = bucket.BucketName.Trim().ToLowerInvariant(),
                Description = string.IsNullOrWhiteSpace(bucket.Description) ? null : bucket.Description.Trim(),
                Active = bucket.Active,
                IsDefault = bucket.IsDefault || index == 0
            })
            .ToList();

        if (config.Buckets.Count > 0 && config.Buckets.All(x => !x.IsDefault))
        {
            config.Buckets[0].IsDefault = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("Configuration", "SaveMinio", "MinioConfiguration", config.Id, $"Configuracao MinIO atualizada com {config.Buckets.Count} bucket(s).", actorUserId, cancellationToken);

        var saved = await db.MinioConfigurations
            .AsNoTracking()
            .Include(x => x.Buckets)
            .SingleAsync(x => x.Id == config.Id, cancellationToken);

        return ToDto(saved);
    }

    private MinioConfigurationDto ToDto(Domain.MinioConfiguration config)
    {
        return new MinioConfigurationDto(
            config.Id,
            config.Endpoint,
            string.IsNullOrWhiteSpace(config.AccessKey) ? "" : tokenProtection.Unprotect(config.AccessKey),
            string.IsNullOrWhiteSpace(config.SecretKey) ? "" : tokenProtection.Unprotect(config.SecretKey),
            config.PublicUrl,
            config.Active,
            config.Buckets
                .OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.Name)
                .Select(x => new MinioBucketDto(x.Id, x.Name, x.BucketName, x.Description, x.Active, x.IsDefault))
                .ToList());
    }

    private static void Validate(SaveMinioConfigurationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Endpoint) || string.IsNullOrWhiteSpace(request.AccessKey) || string.IsNullOrWhiteSpace(request.SecretKey))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["minio"] = ["Endpoint, access key e secret key sao obrigatorios."] });
        }

        if (!Uri.TryCreate(request.Endpoint.Trim(), UriKind.Absolute, out _))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["endpoint"] = ["Informe uma URL valida para o endpoint do MinIO."] });
        }

        if (!string.IsNullOrWhiteSpace(request.PublicUrl) && !Uri.TryCreate(request.PublicUrl.Trim(), UriKind.Absolute, out _))
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["publicUrl"] = ["Informe uma URL publica valida."] });
        }

        var validBuckets = request.Buckets.Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.BucketName)).ToList();
        if (validBuckets.Count == 0)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["buckets"] = ["Cadastre ao menos um bucket para armazenar anexos e fotos."] });
        }
    }
}
