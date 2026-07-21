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
using NpgsqlTypes;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.PostgreSQL;
using Serilog.Sinks.PostgreSQL.ColumnWriters;

var builder = WebApplication.CreateBuilder(args);

// DATABASE_URL is a standard Postgres URI (postgresql://user:pass@host:port/db) -
// the format Railway (and most PaaS hosts) inject for their managed Postgres
// add-ons. ASP.NET Core's configuration picks up the env var automatically;
// for local dev, set the same key via `dotnet user-secrets set DATABASE_URL
// "..."` (see DEPLOYMENT.txt for the actual connection string - it's a live
// credential, so it's not committed to the repo). Computed up front (rather
// than down in the --- Services --- section below, where it used to live)
// since the Serilog Postgres sink needs it too.
var databaseUrl = builder.Configuration["DATABASE_URL"]
    ?? throw new InvalidOperationException(
        "DATABASE_URL is not configured. Set it as an env var (Railway) or via " +
        "`dotnet user-secrets set DATABASE_URL \"...\"` for local dev - see DEPLOYMENT.txt.");
var npgsqlConnectionString = BuildNpgsqlConnectionString(databaseUrl);

// Two sinks: Console (one compact JSON object per line - CLEF format - so
// Railway's log viewer/export gets actual queryable fields instead of plain
// text) and Postgres (the same database everything else already lives in -
// no extra infra, and it's what makes these actually queryable/alertable
// rather than just prettier console output). No file sink - Railway's
// container filesystem doesn't persist across restarts anyway, so a log
// file there would just disappear.
var logColumns = new Dictionary<string, ColumnWriterBase>
{
    { "message", new RenderedMessageColumnWriter() },
    { "message_template", new MessageTemplateColumnWriter() },
    { "level", new LevelColumnWriter(true, NpgsqlDbType.Varchar) },
    { "timestamp", new TimestampColumnWriter() },
    { "exception", new ExceptionColumnWriter() },
    // Everything enriched onto the event (UserId, RequestPath, StatusCode,
    // Elapsed, RemoteIp, ...) as one JSONB blob - Postgres can query into it
    // directly (e.g. `properties->>'UserId'`) without a fixed column per
    // enricher.
    { "properties", new LogEventSerializedColumnWriter(NpgsqlDbType.Jsonb) }
};

builder.Host.UseSerilog((context, config) => config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.PostgreSQL(
        connectionString: npgsqlConnectionString,
        tableName: "logs",
        columnOptions: logColumns,
        needAutoCreateTable: true,
        // Defaults to unbounded (int.MaxValue) otherwise - if the Postgres
        // sink ever stalls (network blip, lock contention), the in-process
        // log queue would grow without bound instead of just dropping the
        // oldest events past this cap.
        queueLimit: 10_000));

// Railway (and most PaaS hosts) inject the port to listen on via $PORT rather
// than a fixed config value. Note: appsettings.json's Kestrel:Endpoints config
// (if present) takes priority over UseUrls() - do NOT leave a Kestrel section
// there, or this gets silently overridden back to localhost, which Railway's
// proxy can't reach.
var port = Environment.GetEnvironmentVariable("PORT") ?? "5220";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Uploaded files (avatars/icons/attachments) now live in the StoredFiles
// table, not on disk - see UploadController and StoredFile. DATA_DIR/
// uploadsDir is kept only to locate whatever's left on the old Railway
// volume for AdminService's one-time migrate-uploads endpoint to import;
// once that's run in production this can be deleted along with the volume
// itself. Falls back to the pre-migration local-dev location when unset.
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR");
var uploadsDir = dataDir is not null
    ? Path.Combine(dataDir, "uploads")
    : Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "uploads");

// Public landing page + client download, bundled with the app (see the
// app.UseStaticFiles calls below).
var sitePath = Path.Combine(builder.Environment.ContentRootPath, "Site");

// --- Services ---

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(npgsqlConnectionString));

builder.Services.AddSingleton(new UploadsPathOptions(uploadsDir));
builder.Services.AddSingleton<JwtTokenService>();
// VoicePresenceService, PresenceService, CallSignalingService,
// PresenceAudienceCache, UserAvatarCache below, plus MessageRateLimiter/
// SlowModeLimiter/CallRateLimiter further down, all hold in-process state
// with no shared store behind them - correct for this app's current single
// Railway instance, but a second replica would see a split view of
// presence/voice rosters and an effectively-doubled rate-limit budget
// (each instance enforces its own copy independently). Same caveat applies
// to AddSignalR() below - its groups/Clients.User() only reach connections
// on the same process. Scaling to 2+ replicas needs a SignalR backplane
// (e.g. Redis) and moving this state to a shared store first; out of scope
// while this runs as a single instance.
builder.Services.AddSingleton<VoicePresenceService>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<UserAvatarCache>();
builder.Services.AddSingleton<CallSignalingService>();
builder.Services.AddSingleton<PresenceAudienceCache>();
builder.Services.AddSingleton<LiveKitTokenService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<ModerationLogService>();
builder.Services.AddScoped<ServerDeletionService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<TwoFactorService>();
// SendMessage/SendDirectMessage anti-spam - see MessageRateLimiter for why
// this can't just be the HTTP rate limiter below (SignalR hub calls don't
// go through the HTTP middleware pipeline at all).
builder.Services.AddSingleton(new MessageRateLimiter(limit: 10, window: TimeSpan.FromSeconds(10)));
// Per-channel slow-mode - same reasoning as MessageRateLimiter above.
builder.Services.AddSingleton<SlowModeLimiter>();
// InitiateCall anti-spam - a much tighter budget than messages since a ring
// is far more disruptive than a chat message (sound + popup on the callee's
// screen, not just an unread badge).
builder.Services.AddSingleton(new CallRateLimiter(limit: 5, window: TimeSpan.FromMinutes(1)));
// Periodic purge of expired RefreshTokens/Invites - see CleanupService.
builder.Services.AddHostedService<CleanupService>();

builder.Services.AddControllers();
// See the in-process-state caveat above the singleton registrations - this
// has no backplane, so it only reaches connections on this one instance.
builder.Services.AddSignalR();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Getting rate-limited is worth knowing about - repeated hits from the
    // same IP/user are exactly the "someone's hammering login" or
    // "something's spamming invites" signal structured logging is for.
    options.OnRejected = (context, _) =>
    {
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting");
        logger.LogWarning("Rate limit exceeded for {Path} from {RemoteIp}",
            context.HttpContext.Request.Path, context.HttpContext.Connection.RemoteIpAddress);
        return ValueTask.CompletedTask;
    };

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

    // Invite redemption: a separate, slightly looser budget than invite
    // creation above - joining is a different action than generating codes,
    // and an 8-char invite code is guessable enough to be worth throttling
    // repeated join attempts against.
    options.AddPolicy("invite-join", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 15,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // User search: each call is a trigram-index scan across every username -
    // cheap individually but worth bounding how often one caller can repeat
    // it, on top of the 2-char query floor in UsersController.Search itself.
    options.AddPolicy("search", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    // Admin dashboard (AdminController): looser than "search" since
    // browsing fires several GETs per click (list -> detail ->
    // channels/members) - this isn't the real access control anyway
    // (AdminService.IsAdminAsync is), just a sanity bound.
    options.AddPolicy("admin", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
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
        // desktop SignalR clients can't set Authorization headers on WS
        // upgrade. Also covers /uploads for the same reason a bearer header
        // isn't always practical there - see AvatarImageCache/
        // AttachmentImageCache client-side, which attach it as a header, but
        // this query-string fallback exists in case that ever isn't
        // possible for a given caller.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hubs/chat") || path.StartsWithSegments("/uploads")))
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

// First in the pipeline so it wraps every request's full duration,
// including whatever runs after it - the actual log line is written once
// the response is ready, by which point UseAuthentication (further down)
// has already populated HttpContext.User regardless of where in the
// pipeline this sits.
app.UseSerilogRequestLogging(options =>
{
    // Without this, every single request - including routine polling like
    // "GET /api/servers/1/channels responded 200" - logs at Information,
    // the same level as everything else, and drowns out what's actually
    // worth seeing in both the console and the logs table. Routine
    // successful requests drop to Debug (below the MinimumLevel.Information
    // floor above, so they're filtered out entirely, not just hidden by a
    // UI toggle); anything that actually went wrong still surfaces.
    options.GetLevel = (httpContext, _, ex) => ex is not null || httpContext.Response.StatusCode >= 500
        ? LogEventLevel.Error
        : httpContext.Response.StatusCode >= 400
            ? LogEventLevel.Warning
            : LogEventLevel.Debug;

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is not null) diagnosticContext.Set("UserId", userId);
    };
});

app.UseCors();

// Defense-in-depth response headers. nosniff is the highest-value one here
// since it also hardens the /uploads static route (paired with the
// magic-byte content validation in UploadController) against a browser
// trying to execute an uploaded file as script/HTML based on a sniffed
// content type. No CSP: Server/Site/'s landing page hasn't been audited for
// inline <script>/<style> usage a strict policy would need to account for.
// No UseHttpsRedirection - Kestrel listens on plain HTTP behind Railway's
// TLS-terminating proxy (see UseUrls above), so a redirect middleware here
// would just loop; HSTS is still worth advertising as a header for direct
// requests that do reach this origin over HTTPS.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

// Public landing page (Server/Site/) - a static download page for the
// client build, served at the root path. Unlike wwwroot/uploads above,
// this is committed to source control (it's app content, not user data).
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(sitePath) });
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(sitePath) });

app.UseAuthentication();
app.UseAuthorization();

// /uploads (avatars/icons/attachments) used to be a PhysicalFileProvider
// static-file route gated by a manual middleware check here; it's now just
// UploadController.GetFile, an ordinary [Authorize] MVC action reading from
// the StoredFiles table - see UploadController for the "must be a logged-in
// app user, not per-file ownership" reasoning that gate used to document.

// After auth: the "invites" policy partitions by the authenticated user,
// which needs HttpContext.User already populated by UseAuthentication above.
app.UseRateLimiter();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
// Anonymous, unauthenticated - Railway (or any orchestrator) needs to be
// able to probe this without a token to detect "process is up but wedged."
app.MapHealthChecks("/health").AllowAnonymous();

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
