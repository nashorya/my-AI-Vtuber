using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.AuthServer;

/// <summary>Builds the three-endpoint HTTP service. Kept separate from Program so tests can
/// host it on a loopback port.</summary>
public static class AuthServerApp
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static WebApplication Build(string[] args, AuthServerOptions? overrideOptions = null, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var options = overrideOptions ?? builder.Configuration.GetSection("Auth").Get<AuthServerOptions>() ?? new AuthServerOptions();
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = Json.PropertyNamingPolicy;
            o.SerializerOptions.DefaultIgnoreCondition = Json.DefaultIgnoreCondition;
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(clock ?? TimeProvider.System);
        builder.Services.AddSingleton(_ => AuthStore.Open(options.DatabasePath));
        builder.Services.AddSingleton<AuthService>();

        var app = builder.Build();
        var log = app.Logger;

        app.MapPost("/v1/auth/login", (LoginRequest request, HttpContext http, AuthService auth) =>
        {
            var result = auth.Login(request, http.Connection.RemoteIpAddress?.ToString() ?? "");
            // Log account/state/version only — never password, token or request body.
            log.LogInformation("login status={Status} account={Account} profile={Profile} version={Version}",
                result.Status, result.AccountId ?? "-", request.ProfileId, request.AppVersion);
            return Respond(result);
        });

        app.MapPost("/v1/auth/heartbeat", (HeartbeatRequest request, HttpContext http, AuthService auth) =>
        {
            var result = auth.Heartbeat(BearerToken(http), request.ProfileId);
            if (result.Status != AuthStatus.Ok)
                log.LogInformation("heartbeat denied status={Status} profile={Profile}", result.Status, request.ProfileId);
            return Respond(result);
        });

        app.MapPost("/v1/auth/logout", (HttpContext http, AuthService auth) =>
        {
            auth.Logout(BearerToken(http));
            return Results.NoContent();
        });

        return app;
    }

    private static IResult Respond(AuthResult result) => result.Status switch
    {
        AuthStatus.Ok => Results.Json(result, Json),
        AuthStatus.BadRequest => Results.Json(result, Json, statusCode: 400),
        AuthStatus.RateLimited => Results.Json(result, Json, statusCode: 429),
        AuthStatus.InvalidCredentials or AuthStatus.InvalidSession or AuthStatus.SessionRevoked =>
            Results.Json(result, Json, statusCode: 401),
        _ => Results.Json(result, Json, statusCode: 403),
    };

    private static string BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : "";
    }
}
