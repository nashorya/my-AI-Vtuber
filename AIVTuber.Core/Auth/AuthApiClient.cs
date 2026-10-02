using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.Auth;

/// <summary>
/// HTTP client for the account service. It owns its own <see cref="HttpClient"/> so the
/// session token is attached per request and never shares a default Authorization header
/// with any vendor client (AUTH-08).
/// </summary>
public sealed class AuthApiClient : IAuthApi, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public AuthApiClient(Uri baseAddress, TimeSpan? timeout = null)
    {
        _http = new HttpClient { BaseAddress = baseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    internal HttpClient HttpClientForTests => _http;

    public Task<AuthReply> LoginAsync(AuthLoginRequest request, CancellationToken ct = default) =>
        SendAsync("v1/auth/login", request, token: null, ct);

    public Task<AuthReply> HeartbeatAsync(string token, string profileId, CancellationToken ct = default) =>
        SendAsync("v1/auth/heartbeat", new { profile_id = profileId }, token, ct);

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AuthTransportException(ex.Message, ex);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<AuthReply> SendAsync(string path, object body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AuthTransportException(ex is TaskCanceledException ? "请求超时" : ex.Message, ex);
        }

        using (response)
        {
            WireReply? wire = null;
            try
            {
                wire = await response.Content.ReadFromJsonAsync<WireReply>(Json, ct).ConfigureAwait(false);
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }

            // A reverse proxy's 502 page or similar is a transport problem, not an account verdict.
            if (wire?.Status is null)
                throw new AuthTransportException($"鉴权服务响应异常（HTTP {(int)response.StatusCode}）");

            return new AuthReply(ParseStatus(wire.Status), wire.SessionToken, wire.AccountId, wire.ServerTime,
                wire.LeaseValidUntil, wire.AccountValidUntil, wire.HeartbeatSeconds);
        }
    }

    internal static AuthCode ParseStatus(string status) => status switch
    {
        "ok" => AuthCode.Ok,
        "invalid_credentials" => AuthCode.InvalidCredentials,
        "disabled" => AuthCode.Disabled,
        "expired" => AuthCode.Expired,
        "profile_mismatch" => AuthCode.ProfileMismatch,
        "credential_revoked" => AuthCode.CredentialRevoked,
        "session_revoked" => AuthCode.SessionRevoked,
        "invalid_session" => AuthCode.InvalidSession,
        "rate_limited" => AuthCode.RateLimited,
        "bad_request" => AuthCode.BadRequest,
        _ => AuthCode.Unknown,
    };

    private sealed record WireReply(
        string? Status,
        string? SessionToken,
        string? AccountId,
        DateTimeOffset? ServerTime,
        DateTimeOffset? LeaseValidUntil,
        DateTimeOffset? AccountValidUntil,
        int HeartbeatSeconds);
}
