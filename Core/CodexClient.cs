using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageDock.Core;

public sealed class CodexClient : IDisposable
{
    private readonly HttpClient http;
    public CodexClient(HttpMessageHandler? handler = null)
    {
        http = handler is null ? new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = 2 }) : new(handler);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.MaxResponseContentBufferSize = 1024 * 1024;
    }
    public static string Identity(string home)
    {
        try
        {
            using var auth = ReadAuth(home);
            var tokens = J.Get(auth.RootElement, "tokens");
            // Persist only a one-way account identity for cache isolation, never a token or email.
            var account = J.Text(tokens, "account_id");
            if (account is null) return "unavailable";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home + "\0" + account)))[..24];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return "unavailable"; }
    }
    private static JsonDocument ReadAuth(string home)
    {
        var path = Path.Combine(home, "auth.json");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Codex authentication file is unexpectedly large.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(file);
    }
    public async Task<UsageSnapshot> ReadAsync(string home, CancellationToken cancellation)
    {
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var first = await ReadDirectAsync(home, requestTimeout.Token).ConfigureAwait(false);
            if (first is not null) return first;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        // Codex owns token refresh, including its keyring backend. Never rewrite its credentials.
        return await AppServerBridge.ReadAsync(home, cancellation).ConfigureAwait(false);
    }
    private async Task<UsageSnapshot?> ReadDirectAsync(string home, CancellationToken cancellation)
    {
        using var auth = ReadAuth(home);
        var tokens = J.Get(auth.RootElement, "tokens");
        if (J.Text(tokens, "access_token") is not { Length: > 0 } token) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("UsageDock/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (J.Text(tokens, "account_id") is { Length: > 0 } account) request.Headers.Add("ChatGPT-Account-Id", account);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException("Usage checks were rate limited. The next scheduled refresh will retry.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Codex usage is temporarily unavailable (HTTP {(int)response.StatusCode}).");
        // Never retain, log or serialize the response's account/profile fields.
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (data.Length + count > 1024 * 1024) throw new InvalidDataException("Codex usage response exceeded the size limit.");
            data.Write(buffer, 0, count);
        }
        using var body = JsonDocument.Parse(data.ToArray());
        var headers = response.Headers.ToDictionary(x => x.Key.ToLowerInvariant(), x => string.Join(",", x.Value));
        var result = QuotaMapper.Parse(body.RootElement, DateTimeOffset.UtcNow, headers);
        // Optional read-only detail. A failure here never hides an otherwise valid quota reading.
        using var detailTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        detailTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            detailRequest.Headers.UserAgent.ParseAdd("UsageDock/1.0");
            detailRequest.Headers.Add("OpenAI-Beta", "codex-1");
            if (J.Text(tokens, "account_id") is { Length: > 0 } id) detailRequest.Headers.Add("ChatGPT-Account-Id", id);
            using var detailResponse = await http.SendAsync(detailRequest, detailTimeout.Token).ConfigureAwait(false);
            if (detailResponse.IsSuccessStatusCode && detailResponse.Content.Headers.ContentLength is not > 1048576)
            {
                using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(detailTimeout.Token).ConfigureAwait(false));
                var availableCount = J.Number(detail.RootElement, "available_count");
                var expiries = J.Array(J.Get(detail.RootElement, "credits")).Where(c => J.Text(c, "status") is null or "available")
                    .Select(c => J.Date(J.Get(c, "expires_at"))).Where(d => d is not null).Select(d => d!.Value).Order().ToList();
                if (availableCount >= 0) result = result with { ResetCredits = (int)availableCount, ResetExpiries = expiries };
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { }
        cancellation.ThrowIfCancellationRequested();
        return result;
    }
    public void Dispose() => http.Dispose();
}
