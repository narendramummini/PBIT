using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PbixMigrator.Services;

/// <summary>Calls B2 only with short-lived backend-issued migration tickets.</summary>
public sealed class B2MigrationBridge(IHttpClientFactory clients)
{
    private readonly string? _baseUrl = Environment.GetEnvironmentVariable("B2_API_URL");
    private readonly string? _secret = Environment.GetEnvironmentVariable("MIGRATION_TICKET_SECRET");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl);

    public async Task<string> SubmitPbixAsync(string ownerId, string migrationId,
        string filename, byte[] pbix, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pbix);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(filename));
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/pipeline/from-pbix"))
        { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateTicket(ownerId, migrationId));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var response = await clients.CreateClient().SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("B2 migration submission failed.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        if (!json.RootElement.TryGetProperty("job_id", out var jobId) || string.IsNullOrWhiteSpace(jobId.GetString()))
            throw new InvalidOperationException("B2 returned no job identifier.");
        return jobId.GetString()!;
    }

    public async Task<object> GetJobStatusAsync(string ownerId, string migrationId, string jobId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl("/jobs/" + Uri.EscapeDataString(jobId)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateTicket(ownerId, migrationId));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await clients.CreateClient().SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("B2 migration status is unavailable.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        var root = json.RootElement;
        return new
        {
            jobId,
            status = root.TryGetProperty("status", out var status) ? status.GetString() : "unknown",
            progress = root.TryGetProperty("progress_message", out var progress) ? progress.GetString() : ""
        };
    }

    private string BuildUrl(string path)
    {
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("B2 API URL is not configured correctly.");
        return new Uri(baseUri, path).ToString();
    }

    private string CreateTicket(string ownerId, string migrationId)
    {
        if (string.IsNullOrWhiteSpace(_secret) || Encoding.UTF8.GetByteCount(_secret) < 32)
            throw new InvalidOperationException("Migration ticket signing is not configured.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            owner_id = ownerId,
            migration_id = migrationId,
            exp = DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeSeconds()
        });
        var encoded = Base64Url(payload);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secret), Encoding.UTF8.GetBytes(encoded));
        return encoded + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
