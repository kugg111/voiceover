using DiscordClone.Server.Auth;
using DiscordClone.Server.Data;
using DiscordClone.Server.Hubs;
using DiscordClone.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Railway (and most PaaS hosts) inject the port to listen on via $PORT rather
// than a fixed config value. Note: appsettings.json's Kestrel:Endpoints config
// (if present) takes priority over UseUrls() - do NOT leave a Kestrel section
// there, or this gets silently overridden back to localhost, which Railway's
// proxy can't reach.
var port = Environment.GetEnvironmentVariable("PORT") ?? "5220";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// DATA_DIR points at a persistent volume in production (the SQLite db and
// uploaded files both need to survive redeploys/restarts, unlike the rest of
// the container filesystem). Falls back to the existing local-dev locations
// when unset.
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR");
var dbPath = dataDir is not null ? Path.Combine(dataDir, "discordclone.db") : "discordclone.db";
var uploadsDir = dataDir is not null
    ? Path.Combine(dataDir, "uploads")
    : Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "uploads");

if (dataDir is not null)
    Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(uploadsDir);

// --- Services ---

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

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

// Apply any pending EF Core migrations on startup (creates the SQLite DB on
// first run too, same as EnsureCreated() used to).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseCors();
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

public record UploadsPathOptions(string Path);
