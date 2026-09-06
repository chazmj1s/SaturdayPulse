using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using SaturdayPulse;
using SaturdayPulse.Configuration;
using SaturdayPulse.Contracts;
using SaturdayPulse.Data;
using SaturdayPulse.Infrastructure;
using SaturdayPulse.Interfaces;
using SaturdayPulse.Services;
using SaturdayPulse.Swagger;
using SaturdayPulse.Utilities;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<NCAAContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── CFBD HTTP Client ──────────────────────────────────────────────────────────
builder.Services.Configure<CfbdApiSettings>(
    builder.Configuration.GetSection("CfbdApi"));

builder.Services.AddHttpClient("cfbd", (sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<CfbdApiSettings>>().Value;
    client.BaseAddress = new Uri(settings.BaseUrl);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", settings.BearerToken);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});

var testSettings = builder.Configuration.GetSection("CfbdApi").Get<CfbdApiSettings>();
Console.WriteLine($"DEBUG CfbdApi — BaseUrl: '{testSettings?.BaseUrl}' BearerToken empty: {string.IsNullOrEmpty(testSettings?.BearerToken)}");

// ── In-memory cache ───────────────────────────────────────────────────────────
// Backs ProductionGameDataService.GetGameAsync's 60s per-gameId cache — without
// this, IMemoryCache fails to resolve at DI time.
builder.Services.AddMemoryCache();

// ── Auth0 / JWT Bearer ────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://{builder.Configuration["Auth0:Domain"]}/";
        options.Audience = builder.Configuration["Auth0:Audience"];
        options.MapInboundClaims = false;
    });

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ScoreDeltaCalculator>();
builder.Services.AddScoped<MatchupHistoryCalculator>();
builder.Services.AddScoped<TierDiscountCalculator>();
builder.Services.AddScoped<AnchorBlendCalculator>();
builder.Services.AddScoped<IGameDataService, GameDataService>();
builder.Services.AddScoped<GamePredictionService>();
builder.Services.AddScoped<WeeklyRankingsService>();
builder.Services.AddScoped<RollingAverageService>();
builder.Services.AddScoped<ProductionGameDataService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<DeveloperService>();
builder.Services.AddScoped<IAvgScoreDifferentialService, AvgScoreDifferentialService>();
builder.Services.AddScoped<ProjectionAccuracyService>();
builder.Services.AddScoped<ConferenceTierService>();
builder.Services.AddScoped<RosterCapacityService>();
builder.Services.AddScoped<UserProfileService>();


// Content management (About/Privacy/Terms/etc.) — same Scoped lifetime as
// every other *Service above, since it goes through IUnitOfWork the same way.
builder.Services.AddScoped<ContentService>();

// ADDED — K=4 inertia-blending experimental comparison path. Registered the same
// way (Scoped) as the other per-request services above (GamePredictionService,
// RollingAverageService, etc.). Read-only, not wired into any production path.
builder.Services.AddScoped<RatingBlendingService>();
builder.Services.AddScoped<ExperimentalInertiaRatingService>();
builder.Services.AddScoped<RatingComparisonService>();

builder.Services.AddSingleton<ProjectionCacheService>();

// ── Server-side in-memory log (Debug Log support) ──────────────────────────────
// Captures ILogger output from tracked categories (GameScorePollingService,
// etc. — see InMemoryLoggerProvider.TrackedCategoryPrefixes) into a ring
// buffer the Mobile app can pull via LogsController ([Authorize]+[AdminOnly],
// no shared secret). Existing logger.LogX(...) calls in those services are
// unchanged — this only adds a second listener on top of them.
builder.Services.AddSingleton<ServerLogService>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new InMemoryLoggerProvider(sp.GetRequiredService<ServerLogService>()));

// ── Background services ────────────────────────────────────────────────────────
// Polls CFBD for score updates every 5 min, only during today's kickoff-to-
// margin window. See GameScorePollingService remarks for details.
builder.Services.AddHostedService<GameScorePollingService>();


// ── ASP.NET / Swagger ─────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SaturdayPulse API", Version = "v1" });
    c.OperationFilter<XUserIdHeaderFilter>();

});

builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.AddConsole()
        .AddFilter(DbLoggerCategory.Database.Command.Name, LogLevel.Information);
    loggingBuilder.AddDebug();
});

builder.Services.Configure<CustomSettings>(builder.Configuration.GetSection("CustomSettings"));
builder.Services.Configure<MetricsConfiguration>(builder.Configuration.GetSection("MetricsConfiguration"));

builder.Services.AddCors();

// ── App pipeline ──────────────────────────────────────────────────────────────
var app = builder.Build();

// Apply any pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NCAAContext>();
    db.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI(c =>
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "SaturdayPulse API V1"));

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHsts();
app.UseHttpsRedirection();
app.UseRouting();

app.UseCors(policy => policy
    .WithOrigins("http://localhost:4200")
    .AllowAnyMethod()
    .AllowAnyHeader());

app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
