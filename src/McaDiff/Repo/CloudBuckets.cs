using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace McaDiff.Repo;

// NOTE: these two adapters are the only part of the cloud backend not covered by unit tests —
// the protocol logic lives in BucketTransport and is exercised against InMemoryBucket. They're
// written against the SDK APIs and compile-checked, but need a smoke test against a real
// account. ETag compare-and-swap (the ref/manifest CAS) requires the provider to honor
// conditional writes: Azure Blob always does; AWS S3 (If-Match PUT, 2024+) and Cloudflare R2 do.

/// <summary>An <see cref="IBucket"/> over Azure Blob Storage. Auth via
/// <c>AZURE_STORAGE_CONNECTION_STRING</c>, or the account name + <c>AZURE_STORAGE_KEY</c>.</summary>
public sealed class AzureBucket : IBucket
{
    private readonly BlobContainerClient _container;

    public AzureBucket(BlobContainerClient container)
    {
        _container = container;
        _container.CreateIfNotExists();
    }

    public static AzureBucket Connect(string account, string container)
    {
        if (Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING") is { Length: > 0 } conn)
            return new AzureBucket(new BlobContainerClient(conn, container));
        if (Environment.GetEnvironmentVariable("AZURE_STORAGE_KEY") is { Length: > 0 } key)
            return new AzureBucket(new BlobContainerClient(
                new Uri($"https://{account}.blob.core.windows.net/{container}"),
                new StorageSharedKeyCredential(account, key)));
        throw new InvalidOperationException("Azure auth: set AZURE_STORAGE_CONNECTION_STRING or AZURE_STORAGE_KEY");
    }

    public (byte[]?, string?) Get(string key)
    {
        try
        {
            Response<BlobDownloadResult> r = _container.GetBlobClient(key).DownloadContent();
            return (r.Value.Content.ToArray(), r.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException e) when (e.Status == 404) { return (null, null); }
    }

    public void Put(string key, byte[] data) =>
        _container.GetBlobClient(key).Upload(BinaryData.FromBytes(data), overwrite: true);

    public bool PutIfMatch(string key, byte[] data, string? expectedETag)
    {
        var options = new BlobUploadOptions
        {
            Conditions = expectedETag is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }            // create-only
                : new BlobRequestConditions { IfMatch = new ETag(expectedETag) }, // update-if-unchanged
        };
        try { _container.GetBlobClient(key).Upload(BinaryData.FromBytes(data), options); return true; }
        catch (RequestFailedException e) when (e.Status is 412 or 409) { return false; } // precondition / already exists
    }

    public IReadOnlyList<string> List(string prefix)
    {
        var keys = new List<string>();
        foreach (BlobItem item in _container.GetBlobs(BlobTraits.None, BlobStates.None, prefix, default)) keys.Add(item.Name);
        return keys;
    }

    public void Delete(string key) => _container.GetBlobClient(key).DeleteIfExists();
}

/// <summary>An <see cref="IBucket"/> over any S3-compatible store (AWS, R2, B2, MinIO, GCS).
/// Credentials come from the standard AWS chain; set <c>S3_ENDPOINT_URL</c> (+ <c>AWS_REGION</c>)
/// for non-AWS providers.</summary>
public sealed class S3Bucket : IBucket
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3Bucket(IAmazonS3 s3, string bucket) { _s3 = s3; _bucket = bucket; }

    public static S3Bucket Connect(string bucket)
    {
        var config = new AmazonS3Config();
        if (Environment.GetEnvironmentVariable("S3_ENDPOINT_URL") is { Length: > 0 } endpoint)
        {
            config.ServiceURL = endpoint;     // R2 / B2 / MinIO
            config.ForcePathStyle = true;
        }
        if (Environment.GetEnvironmentVariable("AWS_REGION") is { Length: > 0 } region)
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        return new S3Bucket(new AmazonS3Client(config), bucket); // credentials from the default chain
    }

    public (byte[]?, string?) Get(string key)
    {
        try
        {
            using GetObjectResponse r = _s3.GetObjectAsync(_bucket, key).GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            r.ResponseStream.CopyTo(ms);
            return (ms.ToArray(), r.ETag);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound) { return (null, null); }
    }

    public void Put(string key, byte[] data) =>
        _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = new MemoryStream(data) })
            .GetAwaiter().GetResult();

    public bool PutIfMatch(string key, byte[] data, string? expectedETag)
    {
        var req = new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = new MemoryStream(data) };
        if (expectedETag is null) req.IfNoneMatch = "*"; else req.IfMatch = expectedETag;
        try { _s3.PutObjectAsync(req).GetAwaiter().GetResult(); return true; }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict) { return false; }
    }

    public IReadOnlyList<string> List(string prefix)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            ListObjectsV2Response resp = _s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token })
                .GetAwaiter().GetResult();
            if (resp.S3Objects is { } objs) keys.AddRange(objs.Select(o => o.Key));
            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (token is not null);
        return keys;
    }

    public void Delete(string key) => _s3.DeleteObjectAsync(_bucket, key).GetAwaiter().GetResult();
}
