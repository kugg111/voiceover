using DiscordClone.Server.Auth;
using DiscordClone.Server.Data;
using DiscordClone.Server.Hubs;
using DiscordClone.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Services ---

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=discordclone.db"));

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

// Auto-create the SQLite DB on first run. Swap for real migrations once the
// schema stabilizes (dotnet ef migrations add InitialCreate).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors();
app.UseStaticFiles(); // serves wwwroot/uploads/* at /uploads/*
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
