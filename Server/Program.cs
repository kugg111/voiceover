using System.Security.Claims;
using System.Threading.RateLimiting;
using Voiceover.Server.Auth;
using Voiceover.Server.Data;
using Voiceover.Server.Hubs;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Railway (and most PaaS hosts) inject the port to listen on via $PORT rather
// than a fixed config value. Note: appsettings.json's Kestrel:Endpoints config
// (if present) takes priority over UseUrls() - do NOT leave a Kestrel section
// there, or this gets silently overridden back to localhost, which Railway's
// proxy can't reach.
var port = Environment.GetEnvironmentVariable("PORT") ?? "5220";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// DATA_DIR points at a persistent volume in production (uploaded files need
// to survive redeploys/restarts, unlike the rest of the container
// filesystem). Falls back to the existing local-dev location when unset.
// The database itself lives in a separate managed Postgres service (see
// below), not on this volume.
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR");
var uploadsDir = dataDir is not null
    ? Path.Combine(dataDir, "uploads")
    : Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "uploads");

if (dataDir is not null)
    Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(uploadsDir);

// Public landing page + client download, bundled with the app (see the
// app.UseStaticFiles calls below).
var sitePath = Path.Combine(builder.Environment.ContentRootPath, "Site");

// --- Services ---

// DATABASE_URL is a standard Postgres URI (postgresql://user:pass@host:port/db) -
// the format Railway (and most PaaS hosts) inject for their managed Postgres
// add-ons. ASP.NET Core's configuration picks up the env var automatically;
// for local dev, set the same key via `dotnet user-secrets set DATABASE_URL
// "..."` (see DEPLOYMENT.txt for the actual connection string - it's a live
// credential, so it's not committed to the repo).
var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "DATABASE_URL is not configured. Set it as an env var (Railway) or via " +
        "`dotnet user-secrets set DATABASE_URL \"...\"` for local dev - see DEPLOYMENT.txt.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(BuildNpgsqlConnectionString(databaseUrl)));

builder.Services.AddSingleton(new UploadsPathOptions(uploadsDir));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<VoicePresenceService>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<LiveKitTokenService>();
builder.Services.AddScoped<PermissionService>();
// SendMessage/SendDirectMessage anti-spam - see MessageRateLimiter for why
// this can't just be the HTTP rate limiter below (SignalR hub calls don't
// go through the HTTP middleware pipeline at all).
builder.Services.AddSingleton(new MessageRateLimiter(limit: 10, window: TimeSpan.FromSeconds(10)));

builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Login/register: brute-force protection. Partitioned by client IP since
    // there's no authenticated identity yet at this point.
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Invite generation: spam protection. Partitioned by authenticated user
    // rather than IP, since [Authorize] already guarantees an identity here.
    options.AddPolicy("invites", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

// The WPF client ignores CORS entirely (it's not a browser - CORS is only
// enforced client-side by browsers), and the landing page has no JS calling
// this API at all, so this policy currently protects nothing in practice.
// It exists as a safety net for whenever a browser-based client shows up -
// allowlisted to this app's own origins rather than reflecting back
// whatever Origin header a request happens to send. No AllowCredentials():
// auth here is a bearer JWT (Authorization header / SignalR access_token
// query param), never cookies, so credentialed CORS isn't needed.
var allowedOrigins = new[]
{
    "https://www.voiceover-app.hu",
    "https://voiceover-production-c32a.up.railway.app"
};
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader().AllowAnyMethod().WithOrigins(allowedOrigins));
});

var jwtServiceForAuth = new JwtTokenService(builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = jwtServiceForAuth.GetValidationParameters();

        // Allows SignalR to receive the JWT via query string, since browsers/
        // desktop SignalR clients can't set Authorization headers on WS upgrade.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/chat"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// Apply any pending EF Core migrations on startup (creates the schema on a
// fresh Postgres database too, same as EnsureCreated() used to).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseCors();

// Public landing page (Server/Site/) - a static download page for the
// client build, served at the root path. Unlike wwwroot/uploads above,
// this is committed to source control (it's app content, not user data).
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(sitePath) });
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(sitePath) });

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsDir),
    RequestPath = "/uploads"
});
app.UseAuthentication();
app.UseAuthorization();
// After auth: the "invites" policy partitions by the authenticated user,
// which needs HttpContext.User already populated by UseAuthentication above.
app.UseRateLimiter();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();

// Converts a postgres:// URI (the format PaaS hosts inject) into the
// Host=...;Port=...;... form Npgsql's connection string actually expects -
// it doesn't parse the URI scheme directly.
static string BuildNpgsqlConnectionString(string databaseUrl)
{
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = userInfo[0],
        Password = userInfo[1],
        // Prefer silently falls back to plaintext if TLS negotiation fails -
        // this DB holds password hashes and message content, so a downgrade
        // should be a hard failure, not a silent one. Require (not
        // VerifyFull) still skips certificate validation, since Railway's
        // managed Postgres is reached through a proxy hostname that full
        // cert validation could reject.
        SslMode = SslMode.Require
    }.ConnectionString;
}

public record UploadsPathOptions(string Path);
