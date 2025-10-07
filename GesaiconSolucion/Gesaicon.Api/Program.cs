using Gesaicon.Api.Data;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Microsoft.Extensions.FileProviders;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Gesaicon.Api.Services;
using Gesaicon.Api.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddSingleton<BackgroundStatusStore>();

// Rate Limiting policy (10 req/min por IP para endpoints protegidos)
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("receipt-analysis", context =>
    {
        // No limitar preflight OPTIONS para evitar romper CORS
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter("preflight");
        }
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
});

// CORS para permitir Blazor WASM (puertos reales según launchSettings)
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("AllowBlazor", p => p
        .WithOrigins(
            "https://localhost:7110",
            "http://localhost:5259"
        )
        .AllowAnyHeader()
        .AllowAnyMethod()
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
    );
});

// Prompt provider (lee archivo externo si existe)
builder.Services.AddSingleton<IAnalysisPromptProvider>(sp =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<AnalysisPromptProvider>>();
    return new AnalysisPromptProvider(env.ContentRootPath, config, logger);
});

// Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Gesaicon API",
        Version = "v1",
        Description = "API Gesaicon"
    });
});

// DbContext
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var useSqlServer = !string.IsNullOrEmpty(connectionString) && 
                   builder.Environment.EnvironmentName != "Testing";

builder.Services.AddDbContext<GesaiconDbContext>(options =>
{
    if (useSqlServer)
    {
        options.UseSqlServer(connectionString);
        options.EnableDetailedErrors();
        if (builder.Environment.IsDevelopment())
        {
            options.EnableSensitiveDataLogging();
        }
        options.LogTo(message => Serilog.Log.ForContext("EFCore", true).Information(message),
                      Microsoft.Extensions.Logging.LogLevel.Information);
    }
    else
    {
        // Use InMemory database for testing
        options.UseInMemoryDatabase("TestDb");
    }
});

// File storage infrastructure
builder.Services.AddSingleton<IBusinessFilePathStrategy, BusinessFilePathStrategy>();
builder.Services.AddSingleton<IFileStorageService, FileStorageService>();

// HttpClient
builder.Services.AddHttpClient();

// Background queue & scheduled batch
builder.Services.AddSingleton<ReceiptAnalysisQueueService>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ReceiptAnalysisQueueService>());
builder.Services.AddSingleton<IReceiptAnalysisQueue>(sp => sp.GetRequiredService<ReceiptAnalysisQueueService>()); // corregido

// Scheduled batch analysis service
builder.Services.AddHostedService<ScheduledBatchAnalysisService>();

// File ingestion options + hosted service
builder.Services.Configure<FileIngestionOptions>(builder.Configuration.GetSection("FileIngestion"));
if (builder.Configuration.GetSection("FileIngestion").GetValue<bool>("Enabled"))
{
    builder.Services.AddHostedService<FolderIngestionService>();
}

// Legacy fix DESPUÉS de la ingesta para que encuentre tickets placeholder
//builder.Services.AddHostedService<LegacyTicketFixService>();

// Servicio de reparación de rutas - ejecuta una vez al inicio
//builder.Services.AddHostedService<TicketPathRepairService>();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        Log.Error(ex, "Excepción no controlada en la request {Path}", context.Request.Path);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Internal Server Error", detail = ex.Message });
    }
});

// MOVER CORS ANTES DEL RATE LIMITER
app.UseCors("AllowBlazor");

// No aplicar rate limiter en ambiente de Testing
if (!app.Environment.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
{
    app.UseRateLimiter();
}

var uploadsPath = Path.Combine(app.Environment.ContentRootPath, "Uploads");
if (!Directory.Exists(uploadsPath)) Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/Uploads"
});

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Gesaicon API v1");
    c.RoutePrefix = "swagger";
});

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers().RequireRateLimiting("receipt-analysis");

// Herramienta de migración (ejecutar con: dotnet run -- migrate-uploads [--dry-run] [--company-slug nombre] [--force-company])
if (args.Length > 0 && args[0] == "migrate-uploads")
{
    var dryRun = args.Contains("--dry-run");
    var forceCompany = args.Contains("--force-company");
    var companySlug = "default";
    var slugIndex = Array.IndexOf(args, "--company-slug");
    if (slugIndex >= 0 && slugIndex + 1 < args.Length)
    {
        companySlug = args[slugIndex + 1];
    }
    
    using var scope = app.Services.CreateScope();
    await Gesaicon.Api.Tools.MigrateUploadsToNewStructure.RunAsync(
        scope.ServiceProvider, dryRun, companySlug, forceCompany, CancellationToken.None);
    return;
}

try
{
    Log.Information("Iniciando la aplicacion web");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "La aplicacion termino inesperadamente");
}
finally
{
    Log.CloseAndFlush();
}

// Make Program class accessible for integration tests
public partial class Program { }
