using Gesaicon.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Microsoft.Extensions.FileProviders; // agregado

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

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
builder.Services.AddDbContext<GesaiconDbContext>((sp, options) =>
{
    var env = sp.GetRequiredService<IHostEnvironment>();
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
    options.EnableDetailedErrors();
    if (env.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
    }
    // Logging de EF Core a Serilog
    options.LogTo(message => Serilog.Log.ForContext("EFCore", true).Information(message),
                  Microsoft.Extensions.Logging.LogLevel.Information);
});

// HttpClient
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseSerilogRequestLogging(); // middleware de request logging

// Middleware global de excepciones para capturar errores que rompen swagger
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Excepción no controlada en la request {Path}", context.Request.Path);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Internal Server Error", detail = ex.Message });
    }
});

// Servir carpeta Uploads como archivos estáticos
var uploadsPath = Path.Combine(app.Environment.ContentRootPath, "Uploads");
if (!Directory.Exists(uploadsPath)) Directory.CreateDirectory(uploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/Uploads"
});

// Swagger siempre habilitado
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Gesaicon API v1");
    c.RoutePrefix = "swagger"; // URL: /swagger
});

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

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
