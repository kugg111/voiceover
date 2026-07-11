using Voiceover.Server.Auth;
using Voiceover.Server.Data;
using Voiceover.Server.Hubs;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
builder.Services.AddScoped<PermissionService>();

builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    // Locked down to the WPF client's needs. Since the client is a desktop
    // app (not a browser), CORS mainly matters if you add a web client later.
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials());
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

// The app is also reachable at its Railway-assigned *.up.railway.app domain
// (Railway needs that to work regardless - health checks, and it's the
// fallback if the custom domain's DNS ever breaks). Browser visits to the
// *website* on that domain get redirected to the canonical custom domain
// instead of serving the same site at two different URLs. Skipped in dev
// (no custom domain locally) and for API/hub/upload paths, since the WPF
// client's requests (login, messages, SignalR, file uploads) stay pinned to
// whichever host it was configured with - see App.HubUrl - and must get a
// real response, not a redirect.
const string CanonicalHost = "voiceover-app.hu";
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var host = context.Request.Host.Host;
        var path = context.Request.Path;
        var isAppRequest = path.StartsWithSegments("/api")
            || path.StartsWithSegments("/hubs")
            || path.StartsWithSegments("/uploads");

        var isCanonicalHost = host.Equals(CanonicalHost, StringComparison.OrdinalIgnoreCase)
            || host.Equals($"www.{CanonicalHost}", StringComparison.OrdinalIgnoreCase);

        if (!isAppRequest && !isCanonicalHost)
        {
            var target = $"https://{CanonicalHost}{path}{context.Request.QueryString}";
            context.Response.Redirect(target, permanent: true);
            return;
        }

        await next();
    });
}

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
        SslMode = SslMode.Prefer
    }.ConnectionString;
}

public record UploadsPathOptions(string Path);
