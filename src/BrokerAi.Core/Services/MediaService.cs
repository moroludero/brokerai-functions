using System.Net.Http.Json;
using Azure.Storage.Blobs;
using BrokerAi.Core.Options;
using Microsoft.Extensions.Options;

namespace BrokerAi.Core.Services;

public interface IMediaService
{
    /// <summary>Downloads a Meta media object (by media_id) and uploads it to Blob storage, returning the public/blob URL.</summary>
    Task<string> DownloadAndStoreAsync(string mediaId, CancellationToken ct = default);

    /// <summary>
    /// Downloads the given photo URLs, composes the grid collage (up to
    /// <see cref="CollageBuilder.MaxPhotos"/>), uploads it as collage-{key}.jpg
    /// and returns its URL. Null when fewer than 2 photos (no grid needed).
    /// </summary>
    Task<string?> RebuildCollageAsync(string key, IReadOnlyList<string> photoUrls, CancellationToken ct = default);
}

/// <summary>Handles Meta media download → Azure Blob storage for property photos.</summary>
public sealed class MediaService(
    HttpClient http,
    BlobServiceClient blobService,
    IOptions<MetaOptions> metaOptions,
    IOptions<AppOptions> appOptions) : IMediaService
{
    public async Task<string> DownloadAndStoreAsync(string mediaId, CancellationToken ct = default)
    {
        var meta = metaOptions.Value;

        // Step 1: resolve the media URL from Meta's Graph API
        var metaRequest = new HttpRequestMessage(HttpMethod.Get,
            $"https://graph.facebook.com/{meta.GraphApiVersion}/{mediaId}");
        metaRequest.Headers.Authorization = new("Bearer", meta.AccessToken);
        var metaResponse = await http.SendAsync(metaRequest, ct);
        metaResponse.EnsureSuccessStatusCode();
        var metaJson = await metaResponse.Content.ReadFromJsonAsync<MetaMediaResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Empty media metadata for {mediaId}");

        // Step 2: download the actual bytes (URL requires the same bearer token)
        var mediaRequest = new HttpRequestMessage(HttpMethod.Get, metaJson.Url);
        mediaRequest.Headers.Authorization = new("Bearer", meta.AccessToken);
        var mediaResponse = await http.SendAsync(mediaRequest, ct);
        mediaResponse.EnsureSuccessStatusCode();
        await using var stream = await mediaResponse.Content.ReadAsStreamAsync(ct);

        // Step 3: upload to Blob storage. Public read is required: WhatsApp and
        // Facebook fetch these URLs from Meta's servers — a private blob makes
        // image messages silently undeliverable (accepted by the API, never sent).
        var container = blobService.GetBlobContainerClient(appOptions.Value.BlobContainerName);
        await container.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.Blob, cancellationToken: ct);
        var isPng = metaJson.MimeType?.Contains("png") == true;
        var blobName = $"{mediaId}.{(isPng ? "png" : "jpg")}";
        var blobClient = container.GetBlobClient(blobName);
        // Explicit image content type: Meta refuses to deliver images served as
        // application/octet-stream (the blob default when no type is set).
        var uploadOptions = new Azure.Storage.Blobs.Models.BlobUploadOptions
        {
            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders
            {
                ContentType = isPng ? "image/png" : "image/jpeg",
            },
        };
        await blobClient.UploadAsync(stream, uploadOptions, ct);

        return blobClient.Uri.ToString();
    }

    public async Task<string?> RebuildCollageAsync(string key, IReadOnlyList<string> photoUrls, CancellationToken ct = default)
    {
        if (photoUrls.Count < 2) return null;

        var bytes = new List<byte[]>();
        foreach (var url in photoUrls.Take(CollageBuilder.MaxPhotos))
            bytes.Add(await http.GetByteArrayAsync(url, ct));

        var collage = CollageBuilder.Build(bytes);

        var container = blobService.GetBlobContainerClient(appOptions.Value.BlobContainerName);
        await container.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.Blob, cancellationToken: ct);
        var blobClient = container.GetBlobClient($"collage-{key}.jpg");
        var uploadOptions = new Azure.Storage.Blobs.Models.BlobUploadOptions
        {
            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = "image/jpeg" },
        };
        await blobClient.UploadAsync(new MemoryStream(collage), uploadOptions, ct);
        return blobClient.Uri.ToString();
    }

    private sealed record MetaMediaResponse(string Url, string? MimeType);
}
