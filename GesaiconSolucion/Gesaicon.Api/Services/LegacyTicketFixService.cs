using Gesaicon.Api.Data;
using Gesaicon.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Gesaicon.Api.Services;

/// <summary>
/// Servicio que automáticamente corrige tickets legacy al iniciar la aplicación.
/// Se ejecuta una sola vez al arranque y migra/corrige tickets sin RelativePath o con empresa incorrecta.
/// </summary>
public class LegacyTicketFixService : IHostedService
{
    private readonly ILogger<LegacyTicketFixService> _logger;
    private readonly IServiceProvider _sp;
    private readonly IHostEnvironment _env;
    private readonly string _defaultCompanySlug;
    private readonly bool _forceReprocessAll;

    public LegacyTicketFixService(
        ILogger<LegacyTicketFixService> logger,
        IServiceProvider sp,
        IHostEnvironment env,
        IOptions<FileIngestionOptions> options)
    {
        _logger = logger;
        _sp = sp;
        _env = env;
        _defaultCompanySlug = options.Value.DefaultCompanySlug;
        _forceReprocessAll = options.Value.ForceReprocessAll;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // No ejecutar en ambiente de testing
        if (_env.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[LegacyFix] Saltando corrección automática en ambiente Testing");
            return;
        }

        try
        {
            await FixLegacyTicketsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LegacyFix] Error durante la corrección automática de tickets legacy");
        }

        return;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task FixLegacyTicketsAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
        var strategy = scope.ServiceProvider.GetRequiredService<IBusinessFilePathStrategy>();

        var uploadsRoot = Path.Combine(_env.ContentRootPath, "Uploads");

        // Determinar qué tickets procesar según configuración
        IQueryable<Models.ExpenseTicket> query = db.ExpenseTickets;
        
        if (_forceReprocessAll)
        {
            // Modo FORZADO: Reprocesar TODOS los tickets (útil para corregir migraciones fallidas)
            _logger.LogWarning("[LegacyFix] MODO FORZADO ACTIVADO: reprocesando TODOS los tickets");
            // No aplicar filtro, tomar todos
        }
        else
        {
            // Modo NORMAL: Solo tickets que necesitan corrección
            // 1. Sin RelativePath (legacy puro) 
            // 2. Con CompanySlug null o "default" (necesita inferirse desde CompanyName)
            query = query.Where(t => t.RelativePath == null || t.RelativePath == "" || 
                                    t.CompanySlug == null || t.CompanySlug == "default");
        }
        
        var ticketsToFix = await query.ToListAsync(ct);

        if (!ticketsToFix.Any())
        {
            _logger.LogInformation("[LegacyFix] No hay tickets que procesar");
            return;
        }

        _logger.LogInformation("[LegacyFix] Iniciando corrección de {Count} tickets (ForceReprocessAll={Force})", 
            ticketsToFix.Count, _forceReprocessAll);

        int corrected = 0, errors = 0, skipped = 0;

        foreach (var ticket in ticketsToFix)
        {
            try
            {
                // IMPORTANTE: Extraer fecha REAL del ticket desde AnalysisJson si existe
                int year;
                int month;
                
                if (TryExtractDateFromAnalysis(ticket.AnalysisJson, out var ticketDate))
                {
                    year = ticketDate.Year;
                    month = ticketDate.Month;
                    _logger.LogDebug("[LegacyFix] Ticket {Id}: fecha extraída del análisis {Date}", 
                        ticket.Id, ticketDate.ToString("yyyy-MM-dd"));
                }
                else
                {
                    // Fallback: usar ExpenseYear/Month si existen, sino UploadedAt
                    year = ticket.ExpenseYear ?? ticket.UploadedAt.Year;
                    month = ticket.ExpenseMonth ?? ticket.UploadedAt.Month;
                    _logger.LogDebug("[LegacyFix] Ticket {Id}: usando fecha de BD/Upload {Year}-{Month:D2}", 
                        ticket.Id, year, month);
                }

                // IMPORTANTE: Usar CompanyName de la BD si existe, sino usar default
                string companySlug;
                if (!string.IsNullOrWhiteSpace(ticket.CompanyName))
                {
                    // Convertir CompanyName a slug (lowercase, sin espacios, sin caracteres especiales)
                    companySlug = ToSlug(ticket.CompanyName);
                    _logger.LogDebug("[LegacyFix] Ticket {Id}: usando empresa de BD '{Company}' -> slug '{Slug}'", 
                        ticket.Id, ticket.CompanyName, companySlug);
                }
                else
                {
                    companySlug = _defaultCompanySlug;
                    _logger.LogWarning("[LegacyFix] Ticket {Id}: sin empresa en BD, usando default '{Slug}'", 
                        ticket.Id, companySlug);
                }

                // Determinar ruta del archivo actual
                string? currentPath = null;
                if (!string.IsNullOrWhiteSpace(ticket.RelativePath))
                {
                    // Ticket ya migrado parcialmente
                    currentPath = Path.Combine(uploadsRoot, ticket.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                }
                else if (!string.IsNullOrWhiteSpace(ticket.FileName))
                {
                    // Ticket legacy puro
                    currentPath = Path.Combine(uploadsRoot, ticket.FileName);
                }

                if (currentPath == null || !File.Exists(currentPath))
                {
                    _logger.LogWarning("[LegacyFix] Ticket {Id}: archivo no encontrado en {Path}", ticket.Id, currentPath);
                    skipped++;
                    continue;
                }

                var ticketId = ticket.PublicId.ToString("N");
                var originalFileName = ticket.FileName ?? Path.GetFileName(currentPath);
                
                // Generar ruta nueva usando companySlug, year y month correctos
                var (dirRel, fileName) = strategy.Generate(companySlug, year, month, ticketId, originalFileName);
                var newRelativePath = Path.Combine(dirRel, fileName).Replace(Path.DirectorySeparatorChar, '/');
                var newPhysicalPath = Path.Combine(uploadsRoot, dirRel, fileName);

                // Si ya está en la ubicación correcta, solo actualizar BD
                if (Path.GetFullPath(currentPath).Equals(Path.GetFullPath(newPhysicalPath), StringComparison.OrdinalIgnoreCase))
                {
                    var needsUpdate = ticket.CompanySlug != companySlug ||
                                     ticket.ExpenseYear != year ||
                                     ticket.ExpenseMonth != month ||
                                     ticket.RelativePath != newRelativePath;

                    if (needsUpdate)
                    {
                        ticket.CompanySlug = companySlug;
                        ticket.ExpenseYear = year;
                        ticket.ExpenseMonth = month;
                        ticket.RelativePath = newRelativePath;
                        ticket.FileUrl = $"/Uploads/{newRelativePath}";
                        corrected++;
                        _logger.LogDebug("[LegacyFix] Ticket {Id}: BD actualizada sin mover archivo", ticket.Id);
                    }
                    else
                    {
                        skipped++;
                    }
                    continue;
                }

                // Mover archivo físicamente
                Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
                
                // Protección contra duplicados
                if (File.Exists(newPhysicalPath))
                {
                    var uniqueName = $"{ticketId}_{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(fileName)}";
                    fileName = uniqueName;
                    newRelativePath = Path.Combine(dirRel, fileName).Replace(Path.DirectorySeparatorChar, '/');
                    newPhysicalPath = Path.Combine(uploadsRoot, dirRel, fileName);
                }

                File.Move(currentPath, newPhysicalPath, overwrite: false);

                // Actualizar ticket
                ticket.CompanySlug = companySlug;
                ticket.ExpenseYear = year;
                ticket.ExpenseMonth = month;
                ticket.RelativePath = newRelativePath;
                ticket.FileUrl = $"/Uploads/{newRelativePath}";

                // Migrar archivo de análisis si existe
                if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
                {
                    string? oldAnalysisPath = null;
                    
                    if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileUrl))
                    {
                        var relPath = ticket.AnalysisFileUrl.Replace("/Uploads/", "").Replace('/', Path.DirectorySeparatorChar);
                        oldAnalysisPath = Path.Combine(uploadsRoot, relPath);
                    }
                    else
                    {
                        oldAnalysisPath = Path.Combine(uploadsRoot, ticket.AnalysisFileName);
                    }

                    if (File.Exists(oldAnalysisPath))
                    {
                        var newAnalysisRelPath = Path.Combine(dirRel, ticket.AnalysisFileName).Replace(Path.DirectorySeparatorChar, '/');
                        var newAnalysisPath = Path.Combine(uploadsRoot, dirRel, ticket.AnalysisFileName);
                        
                        if (!Path.GetFullPath(oldAnalysisPath).Equals(Path.GetFullPath(newAnalysisPath), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Move(oldAnalysisPath, newAnalysisPath, overwrite: false);
                            ticket.AnalysisFileUrl = $"/Uploads/{newAnalysisRelPath}";
                        }
                    }
                }

                corrected++;
                _logger.LogInformation("[LegacyFix] Ticket {Id} ({Company}) ? empresas/{Slug}/{Year:D4}/{Month:D2}/", 
                    ticket.Id, ticket.CompanyName ?? "sin empresa", companySlug, year, month);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LegacyFix] Error corrigiendo ticket {Id}", ticket.Id);
                errors++;
            }
        }

        if (corrected > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[LegacyFix] Corrección completada: {Corrected} corregidos, {Skipped} omitidos, {Errors} errores", 
                corrected, skipped, errors);
        }
        else
        {
            _logger.LogInformation("[LegacyFix] No se realizaron cambios");
        }
    }

    /// <summary>
    /// Convierte un nombre de empresa a slug válido para rutas de archivo.
    /// Ejemplos: "McDonald's" -> "mcdonalds", "E.Leclerc" -> "eleclerc", "Lidl Supermercados S.A.U" -> "lidl-supermercados-sau"
    /// </summary>
    private static string ToSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "unknown";

        // Lowercase
        text = text.ToLowerInvariant();

        // Remover acentos
        text = System.Text.RegularExpressions.Regex.Replace(
            text.Normalize(System.Text.NormalizationForm.FormD),
            @"[\p{Mn}]",
            string.Empty
        );

        // Reemplazar caracteres no alfanuméricos por guiones
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^a-z0-9\s-]", "");

        // Reemplazar espacios múltiples por uno solo
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        // Reemplazar espacios por guiones
        text = text.Replace(' ', '-');

        // Remover guiones múltiples
        text = System.Text.RegularExpressions.Regex.Replace(text, @"-+", "-");

        // Remover guiones al inicio y final
        text = text.Trim('-');

        // Limitar longitud
        if (text.Length > 50)
            text = text.Substring(0, 50).TrimEnd('-');

        return string.IsNullOrEmpty(text) ? "unknown" : text;
    }

    /// <summary>
    /// Intenta extraer la fecha real del ticket desde el AnalysisJson.
    /// Busca campos como "date", "ticketDate", "fecha", etc.
    /// </summary>
    private static bool TryExtractDateFromAnalysis(string? analysisJson, out DateTime ticketDate)
    {
        ticketDate = DateTime.MinValue;
        
        if (string.IsNullOrWhiteSpace(analysisJson))
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(analysisJson);
            var root = doc.RootElement;

            // Buscar campos de fecha comunes
            string[] dateFields = { "date", "ticketDate", "fecha", "Date", "TicketDate", "Fecha", "transactionDate" };
            
            foreach (var field in dateFields)
            {
                if (root.TryGetProperty(field, out var dateElement))
                {
                    var dateStr = dateElement.GetString();
                    if (!string.IsNullOrWhiteSpace(dateStr) && DateTime.TryParse(dateStr, out ticketDate))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Si falla el parsing, continuar con el fallback
        }

        return false;
    }
}
