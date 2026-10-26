using System.Text.Json.Serialization;
using AuthApi.Characters;
using AuthApi.Identity;
using AuthApi.Tokens;
using GameServer.Host;
using GameServer.Net.Tickets;
using GameServer.Persistence;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Npgsql;
using Shared.Protocol.Auth;
using Shared.Util;

namespace AuthApi;

/// <summary>Builds the Auth API: services, error handling, and endpoints.</summary>
public static class AuthApiApp
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var urls = builder.Configuration[AuthApiSettings.UrlsVariable];
        urls = string.IsNullOrWhiteSpace(urls) ? AuthApiSettings.DefaultUrls : urls;
        // Checked here, before listening, and again on the bound addresses once started (other sources can add URLs).
        // Dev containers may listen on the wildcard address (HttpListenPolicy; compose publishes it on 127.0.0.1 only).
        var devContainerHttp = HttpListenPolicy.AllowsDevContainerHttp(IdentityProviders.DevLoginCompiled,name => builder.Configuration[name]);
        var unsafeUrl = HttpListenPolicy.FirstRefused(urls, devContainerHttp);
        if (unsafeUrl != null)
            throw new InvalidOperationException($"{AuthApiSettings.UrlsVariable}: refusing plain HTTP on {unsafeUrl}; use https:// or a loopback address (R-2.5-02).");
        builder.WebHost.UseUrls(urls);

        // Settings and tables are resolved from the container, after every configuration source (including test
        // overrides) is in place. Build() below resolves them once, so a bad setup fails at startup, not on a request.
        var services = builder.Services;
        services.AddSingleton(sp => ReadSettings(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => AuthConfig.Load(sp.GetRequiredService<AuthApiSettings>().DataDirectory));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(sp => AuthDatabases.Open(sp.GetRequiredService<AuthApiSettings>().Persistence));
        services.AddSingleton(sp => new AccountRepository(sp.GetRequiredService<AuthDatabases>().Global, sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => new CharacterRepository(
            sp.GetRequiredService<AuthDatabases>().Realm, sp.GetRequiredService<AuthApiSettings>().RealmId, sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AuthApiSettings>();
            return new AccessTokenService(settings.AccessTokenKeyId, settings.AccessTokenSecret, sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<AuthConfig>().Limits.AccessTokenLifetimeMs);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AuthApiSettings>();
            // The same issuer the game server's tests and DevConnect use: one ticket format, no second implementation.
            return new DevTicketIssuer(settings.TicketKeyId, settings.TicketSecret, sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<AuthConfig>().NetSecurity.TicketLifetimeMs);
        });
        services.AddSingleton(sp => IdentityProviders.Create(sp.GetRequiredService<AuthApiSettings>().IdentityMode));
        services.AddSingleton<CharacterService>();

        services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        // Malformed bodies and path values throw instead of returning an empty 400, so they get a reason code below.
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        var app = builder.Build();

        var settings = app.Services.GetRequiredService<AuthApiSettings>();
        var config = app.Services.GetRequiredService<AuthConfig>();
        app.Logger.LogInformation("Auth API: {Settings}", settings);
        app.Lifetime.ApplicationStarted.Register(() => RefusePlainHttpOffLoopback(app, devContainerHttp));

        app.Use(LimitRequestBody(config.Limits.MaxRequestBodyBytes));
        app.Use(ReasonCodesForFailures(app.Logger));
        AuthEndpoints.Map(app);
        return app;
    }

    private static AuthApiSettings ReadSettings(IConfiguration configuration) =>
        AuthApiSettings.Read(name => configuration[name], out var errors)
        ?? throw new InvalidOperationException("Auth API settings are invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

    private static Func<HttpContext, RequestDelegate, Task> LimitRequestBody(int maxBytes) => (context, next) =>
    {
        // Kestrel enforces this; hosts without the feature (the in-memory test server) skip it.
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maxBytes;
        return next(context);
    };

    /// <summary>Every refusal carries a reason code (spec 9): bad requests and unexpected failures included.</summary>
    private static Func<HttpContext, RequestDelegate, Task> ReasonCodesForFailures(ILogger logger) => async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            var status = e.StatusCode == StatusCodes.Status413PayloadTooLarge ? e.StatusCode : StatusCodes.Status400BadRequest;
            await Results.Json(new AuthResponse { ReasonCode = AuthReasonCode.RequestInvalid }, statusCode: status).ExecuteAsync(context);
        }
        catch (Exception e) when (e is not OperationCanceledException && !context.Response.HasStarted)
        {
            // Database down, timeouts, bugs: the client can only retry later.
            var status = e is NpgsqlException or TimeoutException
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status500InternalServerError;
            logger.LogError(e, "Request {Method} {Path} failed", context.Request.Method, context.Request.Path);
            await Results.Json(new AuthResponse { ReasonCode = AuthReasonCode.ServerUnavailable }, statusCode: status).ExecuteAsync(context);
        }
    };

    /// <summary>
    /// The enter-world response contains the session key, which may only travel over HTTPS (R-2.5-02). Plain HTTP is
    /// allowed on loopback for local dev, where it never leaves the machine. TODO(infra): production TLS.
    /// </summary>
    private static void RefusePlainHttpOffLoopback(WebApplication app, bool devContainerHttp)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            if (HttpListenPolicy.IsAllowed(address, devContainerHttp))
                continue;
            app.Logger.LogCritical("Refusing to serve plain HTTP on {Address}: use https:// or a loopback address", address);
            app.Lifetime.StopApplication();
            return;
        }
    }
}

/// <summary>The global and realm databases (R-2.1-03), disposed with the app.</summary>
public sealed class AuthDatabases(NpgsqlDataSource global, NpgsqlDataSource realm) : IAsyncDisposable
{
    public NpgsqlDataSource Global { get; } = global;

    public NpgsqlDataSource Realm { get; } = realm;

    public static AuthDatabases Open(PersistenceSettings settings) =>
        new(NpgsqlDataSource.Create(settings.GlobalConnectionString), NpgsqlDataSource.Create(settings.RealmConnectionString));

    public async ValueTask DisposeAsync()
    {
        await Global.DisposeAsync();
        await Realm.DisposeAsync();
    }
}
