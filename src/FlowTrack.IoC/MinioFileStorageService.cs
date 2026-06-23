using FlowTrack.Application;
using FlowTrack.Data;
using FlowTrack.Domain;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;

namespace FlowTrack.IoC;

internal sealed class MinioFileStorageService(
    AppDbContext db,
    ITokenProtectionService tokenProtection) : IFileStorageService
{
    private const long AttachmentMaxBytes = 10 * 1024 * 1024;
    private const long PhotoMaxBytes = 5 * 1024 * 1024;

    public async Task<UploadedFileDto> SaveStepFileAsync(Guid instanceId, Guid stepExecutionId, string fieldKey, string fileName, string contentType, Stream stream, bool isPhoto, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var (config, bucket) = await GetActiveConfigurationAsync(cancellationToken);
        var client = BuildClient(config);

        await EnsureBucketAsync(client, bucket.BucketName, cancellationToken);

        await using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;

        var size = memory.Length;
        var limit = isPhoto ? PhotoMaxBytes : AttachmentMaxBytes;
        if (size <= 0 || size > limit)
        {
            throw new AppValidationException(new Dictionary<string, string[]> { ["file"] = [$"O arquivo excede o limite permitido de {(isPhoto ? "5 MB" : "10 MB")}."] });
        }

        var extension = Path.GetExtension(fileName);
        var objectKey = $"instances/{instanceId:N}/{stepExecutionId:N}/{fieldKey}/{Guid.NewGuid():N}{extension}";

        await client.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(bucket.BucketName)
                .WithObject(objectKey)
                .WithStreamData(memory)
                .WithObjectSize(size)
                .WithContentType(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType),
            cancellationToken);

        var storedFile = new StoredFile
        {
            FlowInstanceId = instanceId,
            StepExecutionId = stepExecutionId,
            MinioBucketId = bucket.Id,
            FieldKey = fieldKey,
            BucketName = bucket.BucketName,
            ObjectKey = objectKey,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Size = size,
            IsPhoto = isPhoto,
            UploadedByUserId = actorUserId
        };

        db.StoredFileEntries.Add(storedFile);
        await db.SaveChangesAsync(cancellationToken);

        var url = await CreateReadUrlAsync(bucket.BucketName, objectKey, fileName, cancellationToken);
        return new UploadedFileDto(storedFile.Id.ToString(), fieldKey, fileName, storedFile.ContentType, size, url, isPhoto, storedFile.UploadedAt);
    }

    public async Task<string> CreateReadUrlAsync(string bucketName, string objectKey, string fileName, CancellationToken cancellationToken)
    {
        var (config, _) = await GetActiveConfigurationAsync(cancellationToken);
        var client = BuildClient(config);
        var expiry = 60 * 60 * 12;
        return await client.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectKey)
                .WithExpiry(expiry));
    }

    private async Task<(MinioConfiguration Config, MinioBucket Bucket)> GetActiveConfigurationAsync(CancellationToken cancellationToken)
    {
        var config = await db.MinioConfigurationEntries
            .AsNoTracking()
            .Include(x => x.Buckets)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(x => x.Active, cancellationToken)
            ?? throw new AppConflictException("Nenhuma configuracao ativa do MinIO foi encontrada.");

        var bucket = config.Buckets.FirstOrDefault(x => x.Active && x.IsDefault)
            ?? config.Buckets.FirstOrDefault(x => x.Active)
            ?? throw new AppConflictException("Nenhum bucket ativo do MinIO foi encontrado.");

        return (config, bucket);
    }

    private IMinioClient BuildClient(MinioConfiguration config)
    {
        var endpoint = new Uri(config.Endpoint);
        var accessKey = tokenProtection.Unprotect(config.AccessKey);
        var secretKey = tokenProtection.Unprotect(config.SecretKey);

        var builder = endpoint.IsDefaultPort
            ? new MinioClient().WithEndpoint(endpoint.Host)
            : new MinioClient().WithEndpoint(endpoint.Host, endpoint.Port);

        builder = builder.WithCredentials(accessKey, secretKey);

        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            builder = builder.WithSSL();
        }

        return builder.Build();
    }

    private static async Task EnsureBucketAsync(IMinioClient client, string bucketName, CancellationToken cancellationToken)
    {
        var exists = await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucketName), cancellationToken);
        if (!exists)
        {
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName), cancellationToken);
        }
    }
}
