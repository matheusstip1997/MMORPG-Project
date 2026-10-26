using System.Text.Json.Serialization;
using AdminPanel.Queries;
using GameServer.Admin;
using GameServer.Host;
using GameServer.Host.Config;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Json;
using Npgsql;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Util;
using StackExchange.Redis;

namespace AdminPanel;

/// <summary>Builds the admin panel: services, sign-in cookie, error handling, endpoints and the static page.</summary>
public static class AdminPanelApp
{
    public const string CookieName = "mmorpg_admin";

    public static WebApplication Build(string[] args)
    {
        // The page ships next to the binaries; the working directory is the repo root (data/tables), like the Auth API.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        var urls = builder.Configuration[AdminPanelSettings.UrlsVariable];
        urls = string.IsNullOrWhiteSpace(urls) ? AdminPanelSettings.DefaultUrls : urls;
        // Never public (R-2.5-05): plain HTTP on loopback only, or the dev container's wildcard bind published on the
        // host's 127.0.0.1 (HttpListenPolicy). Checked before listening and again on the bound addresses.
        var devContainerHttp = HttpListenPolicy.AllowsDevContainerHttp(AdminIdentities.DevCompiled, name => builder.Configuration[name]);
        var unsafeUrl = HttpListenPolicy.FirstRefused(urls, devContainerHttp);
        if (unsafeUrl != null)
            throw new InvalidOperationException($"{AdminPanelSettings.UrlsVariable}: refusing plain HTTP on {unsafeUrl}; use a loopback address (R-2.5-05).");
        builder.WebHost.UseUrls(urls);

        var services = builder.Services;
        services.AddSingleton(sp => ReadSettings(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => LoadTables(sp.GetRequiredService<AdminPanelSettings>().DataDirectory));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(sp => AdminDatabases.Open(sp.GetRequiredService<AdminPanelSettings>()));
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var configuration = ConfigurationOptions.Parse(sp.GetRequiredService<AdminPanelSettings>().Redis);
            configuration.AbortOnConnectFail = false; // the panel starts and reports ServerUnavailable while Redis is down
            return ConnectionMultiplexer.Connect(configuration);
        });
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AdminPanelSettings>();
            var databases = sp.GetRequiredService<AdminDatabases>();
            return new CharacterQueries(databases.Global, databases.Realm, sp.GetRequiredService<IConnectionMultiplexer>(),
                settings.RedisKeyPrefix, settings.RealmId);
        });
        services.AddSingleton(sp => new AdminActionRepository(sp.GetRequiredService<AdminDatabases>().Realm,
            sp.GetRequiredService<AdminPanelSettings>().RealmId, sp.GetRequiredService<IClock>()));
        services.AddSingleton(sp => AdminIdentities.Create(sp.GetRequiredService<AdminPanelSettings>().IdentityMode));

        services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.SlidingExpiration = false;
            // API callers get a reason code (AdminEndpoints), never a redirect to a login page.
            o.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<AdminSettings>((o, admin) => o.ExpireTimeSpan = TimeSpan.FromMilliseconds(admin.SessionLifetimeMs));

        var app = builder.Build();

        // Resolve settings and tables now, so a bad setup fails at startup, not on a request.
        var settings = app.Services.GetRequiredService<AdminPanelSettings>();
        app.Services.GetRequiredService<AdminSettings>();
        app.Logger.LogInformation("Admin panel: {Settings}", settings);
        app.Lifetime.ApplicationStarted.Register(() => RefuseUnsafeAddresses(app, devContainerHttp));

        app.Use(ReasonCodesForFailures(app.Logger));
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        AdminEndpoints.Map(app);
        return app;
    }

    private static AdminPanelSettings ReadSettings(IConfiguration configuration) =>
        AdminPanelSettings.Read(name => configuration[name], out var errors)
        ?? throw new InvalidOperationException("Admin panel settings are invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors));

    /// <summary>Loads every table, as the game server does, so a broken table stops both.</summary>
    private static AdminSettings LoadTables(string directory)
    {
        var result = ConfigLoader.LoadAndValidate(directory, ConfigTables.All);
        if (!result.Ok)
            throw new InvalidOperationException(
                $"Config tables in {Path.GetFullPath(directory)} are invalid:{Environment.NewLine}{string.Join(Environment.NewLine, result.Errors)}");
        return result.Set.Single<AdminSettings>();
    }

    /// <summary>Every refusal carries a reason code: bad requests and unexpected failures included.</summary>
    private static Func<HttpContext, RequestDelegate, Task> ReasonCodesForFailures(ILogger logger) => async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException) when (!context.Response.HasStarted)
        {
            await AdminEndpoints.Refuse(AdminReasonCode.RequestInvalid).ExecuteAsync(context);
        }
        catch (Exception e) when (e is not OperationCanceledException && !context.Response.HasStarted)
        {
            logger.LogError(e, "Request {Method} {Path} failed", context.Request.Method, context.Request.Path);
            await AdminEndpoints.Refuse(AdminReasonCode.ServerUnavailable).ExecuteAsync(context);
        }
    };

    private static void RefuseUnsafeAddresses(WebApplication app, bool devContainerHttp)
    {
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var address in addresses)
        {
            if (HttpListenPolicy.IsAllowed(address, devContainerHttp))
                continue;
            app.Logger.LogCritical("Refusing to serve plain HTTP on {Address}: use a loopback address", address);
            app.Lifetime.StopApplication();
            return;
        }
    }
}

/// <summary>The global and realm databases (R-2.1-03), disposed with the app.</summary>
public sealed class AdminDatabases(NpgsqlDataSource global, NpgsqlDataSource realm) : IAsyncDisposable
{
    public NpgsqlDataSource Global { get; } = global;

    public NpgsqlDataSource Realm { get; } = realm;

    public static AdminDatabases Open(AdminPanelSettings settings) =>
        new(NpgsqlDataSource.Create(settings.Persistence.GlobalConnectionString),
            NpgsqlDataSource.Create(settings.Persistence.RealmConnectionString));

    public async ValueTask DisposeAsync()
    {
        await Global.DisposeAsync();
        await Realm.DisposeAsync();
    }
}
