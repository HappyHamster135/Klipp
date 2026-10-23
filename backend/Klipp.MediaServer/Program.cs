using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Klipp.MediaServer;

public static class Program
{
    public static Task Main(string[] args) => MediaServerApplication.Build(args).RunAsync();
}

public static class MediaServerApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://127.0.0.1:5080");
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = MediaOptions.MaximumVideoBytes;
            server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        // Request-start logs include queries; signed capabilities must never be logged.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        var options = builder.Configuration.GetSection("Media").Get<MediaOptions>() ?? new MediaOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IStorageGuard, LinuxStorageGuard>();
        builder.Services.AddSingleton<IBackupGate, BackupGate>();
        builder.Services.AddSingleton<PrivateMediaStore>();
        builder.Services.AddSingleton<MediaSigner>();
        builder.Services.AddSingleton<PlaybackGovernor>();
        builder.Services.AddSingleton<IMediaValidator, FfprobeMediaValidator>();
        builder.Services.AddHttpClient<ICommunityGateway, SupabaseCommunityGateway>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        builder.Services.AddHttpClient<IExpiryGateway, SupabaseCommunityGateway>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        builder.Services.AddHostedService<ExpiryJanitor>();
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy.WithOrigins(options.AllowedOrigins)
            .WithMethods("GET", "HEAD", "POST", "DELETE").WithHeaders("Authorization", "Content-Type", "Range")
            .WithExposedHeaders("Accept-Ranges", "Content-Range", "Content-Length")));
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy("control", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 40, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            limiter.AddPolicy("assets", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Services.GetRequiredService<MediaOptions>().Validate();
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            try { await next(context); }
            catch (MediaException error) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error.StatusCode;
                await context.Response.WriteAsJsonAsync(new { code = error.Code, message = error.Message }, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error.StatusCode;
                await context.Response.WriteAsJsonAsync(new { code = "invalid_request", message = "The upload could not be read. Try again." }, context.RequestAborted);
            }
            catch (Exception error) when (!context.Response.HasStarted)
            {
                app.Logger.LogWarning("Media request failed ({ExceptionType}).", error.GetType().Name);
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { code = "storage_unavailable", message = "Clip storage is temporarily unavailable. Try again later." }, context.RequestAborted);
            }
        });
        app.UseCors();
        app.UseRateLimiter();
        MediaEndpoints.Map(app);
        return app;
    }
}
