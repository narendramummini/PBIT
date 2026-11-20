using Microsoft.AspNetCore.Http.Features;
using PbixMigrator.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat  = "HH:mm:ss.fff ";
    o.SingleLine       = true;
    o.IncludeScopes    = true;
});
builder.Logging.SetMinimumLevel(LogLevel.Debug);

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
builder.Services.AddScoped<SnowflakeService>();
builder.Services.AddScoped<PbixService>();
builder.Services.AddScoped<PowerBIService>();
builder.Services.AddScoped<TabularModelService>();
builder.Services.AddScoped<AzureOpenAIService>();   // ← required for [FromServices] in controller
builder.Services.AddScoped<FabricSnowflakeXmlaMigrator>();   // ← standalone legacy-strip/Snowflake-mapping utility
builder.Services.AddScoped<FabricLakehouseService>();         // ← v19: Snowflake → Fabric Lakehouse → Power BI ingestion path

// CORS Setup
builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
         .AllowAnyHeader()
         .AllowAnyMethod()
         .WithExposedHeaders("Content-Disposition")));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

// ── Print EVERY request/response that hits the API, with timing ───────────
app.Use(async (ctx, next) =>
{
    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HTTP");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    log.LogInformation("→ {method} {path}{query}", ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString);
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
        log.LogError(ex, "✘ UNHANDLED EXCEPTION {method} {path} ({ms}ms)",
            ctx.Request.Method, ctx.Request.Path, sw.ElapsedMilliseconds);
        throw;
    }
});

app.MapControllers();
app.Run();