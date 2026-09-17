using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NexusPort.Infrastructure.ExternalServices;

public interface IS3StorageService
{
    Task<string> UploadFileAsync(Stream stream, string fileName, string contentType, string folder = "drivers", CancellationToken cancellationToken = default);
    Task<string> UploadBytesAsync(byte[] bytes, string fileName, string contentType, string folder = "drivers", CancellationToken cancellationToken = default);
    string GetPresignedUrl(string? keyOrUrl, int expireDays = 7);
}

public class S3StorageService : IS3StorageService
{
    private readonly string? _accessKey;
    private readonly string? _secretKey;
    private readonly string _bucketName;
    private readonly string _region;
    private readonly IAmazonS3 _s3Client;
    private readonly ILogger<S3StorageService> _logger;

    private readonly string _defaultFolder;

    public S3StorageService(IConfiguration configuration, ILogger<S3StorageService> logger)
    {
        _logger = logger;
        _accessKey = configuration["AWS:AccessKey"] ?? configuration["AWS:AccessKeyId"];
        _secretKey = configuration["AWS:SecretKey"] ?? configuration["AWS:SecretAccessKey"];
        _bucketName = configuration["AWS:BucketName"] ?? "nexusport";
        _region = configuration["AWS:Region"] ?? "us-east-1";
        _defaultFolder = configuration["AWS:Folder"] ?? "NexusPort/drivers";

        var regionEndpoint = RegionEndpoint.GetBySystemName(_region);
        
        if (!string.IsNullOrEmpty(_accessKey) && !string.IsNullOrEmpty(_secretKey))
        {
            _s3Client = new AmazonS3Client(_accessKey, _secretKey, regionEndpoint);
        }
        else
        {
            _s3Client = new AmazonS3Client(regionEndpoint);
        }
    }

    public string GetPresignedUrl(string? keyOrUrl, int expireDays = 7)
    {
        if (string.IsNullOrWhiteSpace(keyOrUrl)) return string.Empty;

        // If it's already a presigned URL containing X-Amz-Signature, return as is
        if (keyOrUrl.Contains("X-Amz-Signature") || keyOrUrl.Contains("X-Amz-Credential"))
        {
            return keyOrUrl;
        }

        // If it's a local/relative URL not on S3, return as is
        if (!keyOrUrl.Contains("amazonaws.com") && !keyOrUrl.StartsWith("NexusPort", StringComparison.OrdinalIgnoreCase) && !keyOrUrl.StartsWith("drivers", StringComparison.OrdinalIgnoreCase))
        {
            return keyOrUrl;
        }

        string key;
        if (keyOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || keyOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(keyOrUrl);
                key = uri.AbsolutePath.TrimStart('/');
            }
            catch
            {
                key = keyOrUrl;
            }
        }
        else
        {
            key = keyOrUrl.TrimStart('/');
        }

        try
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Expires = DateTime.UtcNow.AddDays(expireDays)
            };
            return _s3Client.GetPreSignedURL(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate presigned URL for key: {Key}", key);
            return keyOrUrl;
        }
    }

    public async Task<string> UploadFileAsync(Stream stream, string fileName, string contentType, string folder = "NexusPort/drivers", CancellationToken cancellationToken = default)
    {
        string key;
        if (string.IsNullOrEmpty(folder))
        {
            key = $"{_defaultFolder.TrimEnd('/')}/{fileName}";
        }
        else if (folder.StartsWith("NexusPort", StringComparison.OrdinalIgnoreCase))
        {
            key = $"{folder.TrimEnd('/')}/{fileName}";
        }
        else
        {
            key = $"NexusPort/{folder.TrimStart('/').TrimEnd('/')}/{fileName}";
        }
        
        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType
        };

        try
        {
            await _s3Client.PutObjectAsync(request, cancellationToken);
            var presignedUrl = GetPresignedUrl(key, 7);
            _logger.LogInformation("Successfully uploaded file to AWS S3: {Key}, Presigned URL generated", key);
            return presignedUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file to AWS S3. Bucket: {Bucket}, Key: {Key}", _bucketName, key);
            throw;
        }
    }

    public async Task<string> UploadBytesAsync(byte[] bytes, string fileName, string contentType, string folder = "drivers", CancellationToken cancellationToken = default)
    {
        using var memoryStream = new MemoryStream(bytes);
        return await UploadFileAsync(memoryStream, fileName, contentType, folder, cancellationToken);
    }
}
