using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PbixMigrator.Services;

var builder = WebApplication.CreateBuilder(args);

// ASP.NET Core reads process environment variables but does not read dotenv
// files. For local development, load only the backend's shared auth settings
// from FASTAPI2/.env when they were not supplied by the process. In
// deployed environments the FASTAPI2 folder is absent and the runtime
// must provide these values through its secret/environment configuration.
LoadLocalAuthEnvironment(builder.Environment.ContentRootPath);

static void LoadLocalAuthEnvironment(string contentRoot)
{
    var envPath = Path.GetFullPath(Path.Combine(contentRoot, "..", "FASTAPI2", ".env"));
    if (!File.Exists(envPath)) return;

    var backendKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "MIGRATION_AUTHORITY", "MIGRATION_AUDIENCE", "MIGRATION_SIGNING_KEY",
        "AUTH_INTERNAL_SERVICE_KEY", "AUTH_SERVICE_URL", "FRONTEND_ORIGINS",
        "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_DEPLOYMENT",
        "B2_API_URL", "MIGRATION_TICKET_SECRET"
    };

    foreach (var rawLine in File.ReadLines(envPath))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
        var separator = line.IndexOf('=');
        if (separator <= 0) continue;

        var key = line[..separator].Trim();
        if (!backendKeys.Contains(key) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) continue;

        var value = line[(separator + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            value = value[1..^1];
        Environment.SetEnvironmentVariable(key, value);
    }
}

// .NET sends "Expect: 100-continue" by default on POST requests with a body.
// Some REST APIs (Snowflake's among them) sit behind a proxy/load balancer
// that doesn't handle that handshake and just resets the TCP connection,
// which surfaces as "IOException: ...forcibly closed by the remote host...
// -> SocketException". This is a process-wide safety net; SnowflakeService
// also sets it per-client for clarity and to not depend on ServicePointManager
// behavior across .NET versions.
System.Net.ServicePointManager.Expect100Continue = false;

// ── Console logging: everything printed with a timestamp so backend and
//    frontend logs can be correlated by eye when debugging the migration
//    pipeline end-to-end. ──────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Increase multipart / body size for large PBIX files (up to 500 MB)
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 500_000_000;
    o.ValueLengthLimit         = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 500_000_000;
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var authAuthority = Environment.GetEnvironmentVariable("MIGRATION_AUTHORITY");
var authAudience = Environment.GetEnvironmentVariable("MIGRATION_AUDIENCE");
var signingKey = Environment.GetEnvironmentVariable("MIGRATION_SIGNING_KEY");
if (string.IsNullOrWhiteSpace(authAuthority) || string.IsNullOrWhiteSpace(authAudience))
    throw new InvalidOperationException("MIGRATION_AUTHORITY and MIGRATION_AUDIENCE must be configured in every environment; the migration API does not allow unauthenticated requests.");
if (builder.Environment.IsProduction())
{
    var openAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")) ||
        !Uri.TryCreate(openAiEndpoint, UriKind.Absolute, out var parsedOpenAiEndpoint) || parsedOpenAiEndpoint.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT, and an HTTPS AZURE_OPENAI_ENDPOINT must be configured in Production.");
    var b2Url = Environment.GetEnvironmentVariable("B2_API_URL");
    var b2Secret = Environment.GetEnvironmentVariable("MIGRATION_TICKET_SECRET");
    if (string.IsNullOrWhiteSpace(b2Url) || string.IsNullOrWhiteSpace(b2Secret) || Encoding.UTF8.GetByteCount(b2Secret) < 32)
        throw new InvalidOperationException("B2_API_URL and a 32-byte MIGRATION_TICKET_SECRET must be configured in Production.");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
        throw new InvalidOperationException("MIGRATION_SIGNING_KEY must contain at least 32 bytes.");
    // Keep JWT claim names stable: session ownership reads `sub` and
    // falls back to `oid` in MigrationController.
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateIssuer = true, ValidIssuer = authAuthority, ValidateAudience = true, ValidAudience = authAudience,
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
        NameClaimType = "sub", ClockSkew = TimeSpan.FromSeconds(30)
    };
});
builder.Services.AddAuthorization();

// _httpFactory.CreateClient() (no name) resolves to the "" (default) named
// client. HttpClient's built-in default is a 100-second timeout, which was
// silently killing long schema-selection runs (Snowflake queries + Fabric
// Lakehouse table loads for every selected view, all on this one client)
// well before the loop was actually done. Removed so any number of
// tables/views can load without an artificial cutoff.
builder.Services.AddHttpClient(string.Empty, c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
});

// App services
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<B2MigrationBridge>();
builder.Services.AddScoped<SnowflakeService>();
builder.Services.AddScoped<PbixService>();
builder.Services.AddScoped<PowerBIService>();
builder.Services.AddScoped<TabularModelService>();
builder.Services.AddScoped<AzureOpenAIService>();   // ← required for [FromServices] in controller
builder.Services.AddScoped<FabricSnowflakeXmlaMigrator>();   // ← standalone legacy-strip/Snowflake-mapping utility
builder.Services.AddScoped<FabricLakehouseService>();         // ← v19: Snowflake → Fabric Lakehouse → Power BI ingestion path

// CORS Setup
var configuredOrigins = (Environment.GetEnvironmentVariable("FRONTEND_ORIGINS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (builder.Environment.IsProduction() && configuredOrigins.Length == 0)
    throw new InvalidOperationException("FRONTEND_ORIGINS must list the allowed browser origins in Production.");
var frontendOrigins = configuredOrigins
    .Concat(builder.Environment.IsDevelopment()
        ? new[] { "http://localhost:8080", "http://127.0.0.1:8080", "http://localhost:3000", "http://127.0.0.1:3000", "http://localhost:5173", "http://127.0.0.1:5173" }
        : Array.Empty<string>())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p =>
        p.WithOrigins(frontendOrigins)
         .AllowAnyHeader()
         .AllowAnyMethod()
         .WithExposedHeaders("Content-Disposition")));

var app = builder.Build();

app.UseCors();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();

// ── Print EVERY request/response that hits the API, with timing ───────────
app.Use(async (ctx, next) =>
{
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HTTP");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    // Query strings can contain OAuth codes or other transient credentials.
    log.LogInformation("→ {method} {path}", ctx.Request.Method, ctx.Request.Path);
    try
    {
        await next();
        sw.Stop();
        log.LogInformation("← {status} {method} {path} ({ms}ms)",
            ctx.Response.StatusCode, ctx.Request.Method, ctx.Request.Path, sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        sw.Stop();
        log.LogError("Unhandled request failure {method} {path} ({ms}ms, {type})",
            ctx.Request.Method, ctx.Request.Path, sw.ElapsedMilliseconds, ex.GetType().Name);
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error." });
        }
    }
});

app.MapControllers().RequireAuthorization();
app.Run();
