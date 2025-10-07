using Gesaicon.Api.Data;
using Gesaicon.Api.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Gesaicon.Api.Models; // para ExpenseTicket
using Gesaicon.Api.Services; // agregado para IReceiptAnalysisQueue

namespace Gesaicon.Api.Controllers;

[ApiController]
[Route("api/tickets")]
public class TicketsController : ControllerBase
{
    private readonly GesaiconDbContext _db;
    private readonly ILogger<TicketsController> _logger;
    private readonly IReceiptAnalysisQueue _queue;

    public TicketsController(GesaiconDbContext db, ILogger<TicketsController> logger, IReceiptAnalysisQueue queue)
    {
        _db = db;
        _logger = logger;
        _queue = queue;
    }

    public record ExpenseTicketDto(
        int Id,
        Guid PublicId,
        string? CompanySlug,
        int? ExpenseYear,
        int? ExpenseMonth,
        string? FileName,
        string? FileUrl,
        string? RelativePath,
        long FileSizeBytes,
        string Status,
        decimal? Amount,
        string? CompanyName,
        string? Category,
        DateTime UploadedAt,
        string? AnalysisFileUrl,
        string? AnalysisFileName,
        string? LastErrorMessage
    );

    public record PagedResult<T>(int Total, IReadOnlyList<T> Items);

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ExpenseTicketDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string order = "desc")
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);

        var q = _db.ExpenseTickets.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(t => t.Status == status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(t =>
                (t.CompanyName != null && t.CompanyName.Contains(s)) ||
                (t.Category != null && t.Category.Contains(s)) ||
                (t.FileName != null && t.FileName.Contains(s)));
        }

        q = (order.Equals("asc", StringComparison.OrdinalIgnoreCase))
            ? q.OrderBy(t => t.UploadedAt)
            : q.OrderByDescending(t => t.UploadedAt);

        var total = await q.CountAsync();
        var items = await q.Skip(skip).Take(take)
            .Select(t => new ExpenseTicketDto(
                t.Id,
                t.PublicId,
                t.CompanySlug,
                t.ExpenseYear,
                t.ExpenseMonth,
                t.FileName,
                t.FileUrl,
                t.RelativePath,
                t.FileSizeBytes ?? 0L,
                t.Status!,
                t.Amount,
                t.CompanyName,
                t.Category,
                t.UploadedAt,
                t.AnalysisFileUrl,
                t.AnalysisFileName,
                t.LastErrorMessage
            ))
            .ToListAsync();

        return Ok(new PagedResult<ExpenseTicketDto>(total, items));
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ExpenseTicketDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOne(int id)
    {
        var t = await _db.ExpenseTickets.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(t => new ExpenseTicketDto(
                t.Id,
                t.PublicId,
                t.CompanySlug,
                t.ExpenseYear,
                t.ExpenseMonth,
                t.FileName,
                t.FileUrl,
                t.RelativePath,
                t.FileSizeBytes ?? 0L,
                t.Status!,
                t.Amount,
                t.CompanyName,
                t.Category,
                t.UploadedAt,
                t.AnalysisFileUrl,
                t.AnalysisFileName,
                t.LastErrorMessage
            ))
            .FirstOrDefaultAsync();

        if (t == null) return NotFound();
        return Ok(t);
    }

    // Nuevo endpoint: devuelve el markdown del análisis si existe. Si no está en DB intenta leer el archivo físico.
    [HttpGet("{id:int}/analysis")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAnalysisMarkdown(int id, [FromServices] IFileStorageService storage)
    {
        var ticket = await _db.ExpenseTickets.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.AnalysisMarkdown, t.AnalysisFileName, t.RelativePath })
            .FirstOrDefaultAsync();
        if (ticket == null) return NotFound();

        string? md = ticket.AnalysisMarkdown;
        if (string.IsNullOrWhiteSpace(md) && !string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
        {
            try
            {
                string? path = null;
                if (!string.IsNullOrWhiteSpace(ticket.RelativePath))
                {
                    var dir = Path.GetDirectoryName(ticket.RelativePath);
                    var analysisRelPath = Path.Combine(dir!, ticket.AnalysisFileName).Replace('\\', '/');
                    path = storage.GetPhysicalPath(analysisRelPath);
                }
                else
                {
                    // Fallback legacy
                    var uploads = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
                    path = Path.Combine(uploads, ticket.AnalysisFileName);
                }
                
                if (path != null && System.IO.File.Exists(path))
                {
                    md = await System.IO.File.ReadAllTextAsync(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo leer archivo markdown de análisis para ticket {Id}", id);
            }
        }

        if (string.IsNullOrWhiteSpace(md)) return NotFound();
        return Content(md!, "text/markdown", Encoding.UTF8);
    }

    // Endpoint para re-procesar un ticket manualmente (force permite romper estado Processing atascado)
    [HttpPost("{id:int}/reprocess")]
    [ProducesResponseType(typeof(ExpenseTicketDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reprocess(
        int id, 
        [FromQuery] bool force = false,
        [FromQuery] bool migrateIfLegacy = true,
        [FromServices] IFileStorageService? storage = null,
        [FromServices] IBusinessFilePathStrategy? strategy = null,
        [FromServices] IHostEnvironment? env = null,
        [FromServices] IConfiguration? config = null)
    {
        var ticket = await _db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        if (ticket.Status == "Processing" && !force)
            return Conflict(new { message = "Ya se está procesando (use force=true si está atascado)" });

        // Si el ticket es legacy (sin RelativePath) y se solicita migración
        bool wasMigrated = false;
        if (migrateIfLegacy && 
            string.IsNullOrWhiteSpace(ticket.RelativePath) && 
            !string.IsNullOrWhiteSpace(ticket.FileName) &&
            storage != null && 
            strategy != null && 
            env != null &&
            config != null)
        {
            try
            {
                var uploadsRoot = Path.Combine(env.ContentRootPath, "Uploads");
                var oldPath = Path.Combine(uploadsRoot, ticket.FileName);
                
                if (System.IO.File.Exists(oldPath))
                {
                    var defaultSlug = config.GetSection("FileIngestion:DefaultCompanySlug").Get<string>() ?? "default";
                    var year = ticket.ExpenseYear ?? ticket.UploadedAt.Year;
                    var month = ticket.ExpenseMonth ?? ticket.UploadedAt.Month;
                    var companySlug = ticket.CompanySlug ?? defaultSlug;
                    
                    var ticketId = ticket.PublicId.ToString("N");
                    var (dirRel, fileName) = strategy.Generate(companySlug, year, month, ticketId, ticket.FileName);
                    var newRelativePath = Path.Combine(dirRel, fileName).Replace('\\', '/');
                    var newPhysicalPath = Path.Combine(uploadsRoot, dirRel, fileName);
                    
                    Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
                    System.IO.File.Move(oldPath, newPhysicalPath, overwrite: false);
                    
                    ticket.CompanySlug = companySlug;
                    ticket.ExpenseYear = year;
                    ticket.ExpenseMonth = month;
                    ticket.RelativePath = newRelativePath;
                    ticket.FileUrl = $"/Uploads/{newRelativePath}";
                    
                    // Migrar análisis markdown si existe
                    if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
                    {
                        var oldAnalysisPath = Path.Combine(uploadsRoot, ticket.AnalysisFileName);
                        if (System.IO.File.Exists(oldAnalysisPath))
                        {
                            var newAnalysisRelPath = Path.Combine(dirRel, ticket.AnalysisFileName).Replace('\\', '/');
                            var newAnalysisPath = Path.Combine(uploadsRoot, dirRel, ticket.AnalysisFileName);
                            System.IO.File.Move(oldAnalysisPath, newAnalysisPath, overwrite: false);
                            ticket.AnalysisFileUrl = $"/Uploads/{newAnalysisRelPath}";
                        }
                    }
                    
                    wasMigrated = true;
                    _logger.LogInformation("[Reprocess] Ticket {Id} migrado de {Old} a {New}", 
                        ticket.Id, ticket.FileName, newRelativePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Reprocess] No se pudo migrar ticket {Id} legacy", ticket.Id);
                // Continuar con el reprocesamiento aunque falle la migración
            }
        }

        // Reiniciar campos de análisis
        ticket.Amount = null;
        ticket.CompanyName = null;
        ticket.Category = null;
        ticket.AnalysisMarkdown = null;
        ticket.AnalysisJson = null;
        ticket.AnalysisFileName = null;
        ticket.AnalysisFileUrl = null;
        ticket.Status = "PendingAnalysis";
        ticket.RetryCount += 1;
        ticket.LastErrorMessage = null;
        await _db.SaveChangesAsync();

        await _queue.EnqueueAsync(ticket.Id, ticket.RetryCount);

        var dto = new ExpenseTicketDto(
            ticket.Id,
            ticket.PublicId,
            ticket.CompanySlug,
            ticket.ExpenseYear,
            ticket.ExpenseMonth,
            ticket.FileName,
            ticket.FileUrl,
            ticket.RelativePath,
            ticket.FileSizeBytes ?? 0L,
            ticket.Status!,
            ticket.Amount,
            ticket.CompanyName,
            ticket.Category,
            ticket.UploadedAt,
            ticket.AnalysisFileUrl,
            ticket.AnalysisFileName,
            ticket.LastErrorMessage
        );
        
        if (wasMigrated)
        {
            return Ok(new { ticket = dto, migrated = true, message = "Ticket migrado y encolado" });
        }
        
        return Ok(dto);
    }

    // Endpoint para corregir CompanySlug basándose en CompanyName
    [HttpPost("{id:int}/fix-company-slug")]
    [ProducesResponseType(typeof(ExpenseTicketDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FixCompanySlug(
        int id,
        [FromServices] IFileStorageService? storage = null,
        [FromServices] IBusinessFilePathStrategy? strategy = null,
        [FromServices] IHostEnvironment? env = null)
    {
        var ticket = await _db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        // Validar que tiene CompanyName
        if (string.IsNullOrWhiteSpace(ticket.CompanyName))
            return BadRequest(new { error = "El ticket no tiene CompanyName analizado" });

        // Validar que no es legacy (debe tener RelativePath)
        if (string.IsNullOrWhiteSpace(ticket.RelativePath))
            return BadRequest(new { error = "El ticket está en estructura legacy, use /reprocess en su lugar" });

        if (storage == null || strategy == null || env == null)
            return BadRequest(new { error = "Servicios de storage no disponibles" });

        try
        {
            var uploadsRoot = Path.Combine(env.ContentRootPath, "Uploads");
            var oldPath = storage.GetPhysicalPath(ticket.RelativePath);

            if (!System.IO.File.Exists(oldPath))
                return BadRequest(new { error = "Archivo físico no encontrado" });

            // Generar nuevo slug desde CompanyName
            var newSlug = GenerateSlug(ticket.CompanyName);
            
            // Si el slug ya es correcto, no hacer nada
            if (ticket.CompanySlug == newSlug)
                return Ok(new { ticket = MapToDto(ticket), changed = false, message = "CompanySlug ya es correcto" });

            var year = ticket.ExpenseYear ?? ticket.UploadedAt.Year;
            var month = ticket.ExpenseMonth ?? ticket.UploadedAt.Month;
            var ticketId = ticket.PublicId.ToString("N");
            var originalFileName = ticket.FileName ?? Path.GetFileName(ticket.RelativePath);

            var (newDirRel, newFileName) = strategy.Generate(newSlug, year, month, ticketId, originalFileName);
            var newRelativePath = Path.Combine(newDirRel, newFileName).Replace('\\', '/');
            var newPhysicalPath = Path.Combine(uploadsRoot, newDirRel, newFileName);

            // Si la ruta es la misma, solo actualizar el slug en BD
            if (ticket.RelativePath == newRelativePath)
            {
                ticket.CompanySlug = newSlug;
                await _db.SaveChangesAsync();
                return Ok(new { ticket = MapToDto(ticket), changed = true, moved = false, message = "CompanySlug actualizado sin mover archivo" });
            }

            // Mover archivo físicamente
            Directory.CreateDirectory(Path.GetDirectoryName(newPhysicalPath)!);
            System.IO.File.Move(oldPath, newPhysicalPath, overwrite: false);

            ticket.CompanySlug = newSlug;
            ticket.RelativePath = newRelativePath;
            ticket.FileUrl = $"/Uploads/{newRelativePath}";

            // Mover análisis markdown si existe
            if (!string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
            {
                var oldAnalysisPath = Path.Combine(uploadsRoot, Path.GetDirectoryName(ticket.RelativePath)!, ticket.AnalysisFileName);
                if (System.IO.File.Exists(oldAnalysisPath))
                {
                    var newAnalysisRelPath = Path.Combine(newDirRel, ticket.AnalysisFileName).Replace('\\', '/');
                    var newAnalysisPath = Path.Combine(uploadsRoot, newDirRel, ticket.AnalysisFileName);
                    System.IO.File.Move(oldAnalysisPath, newAnalysisPath, overwrite: false);
                    ticket.AnalysisFileUrl = $"/Uploads/{newAnalysisRelPath}";
                }
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("[FixSlug] Ticket {Id} corregido de '{OldSlug}' a '{NewSlug}', ruta: {NewPath}",
                ticket.Id, "default", newSlug, newRelativePath);

            return Ok(new { ticket = MapToDto(ticket), changed = true, moved = true, oldSlug = "default", newSlug, message = "CompanySlug corregido y archivo movido" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FixSlug] Error corrigiendo ticket {Id}", ticket.Id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string GenerateSlug(string companyName)
    {
        if (string.IsNullOrWhiteSpace(companyName)) return "unknown";
        
        // Eliminar caracteres especiales y convertir a lowercase
        var slug = new string(companyName
            .ToLowerInvariant()
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-')
            .ToArray())
            .Replace(' ', '-')
            .Replace("--", "-")
            .Trim('-');
        
        return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
    }

    private ExpenseTicketDto MapToDto(ExpenseTicket ticket) => new(
        ticket.Id,
        ticket.PublicId,
        ticket.CompanySlug,
        ticket.ExpenseYear,
        ticket.ExpenseMonth,
        ticket.FileName,
        ticket.FileUrl,
        ticket.RelativePath,
        ticket.FileSizeBytes ?? 0L,
        ticket.Status!,
        ticket.Amount,
        ticket.CompanyName,
        ticket.Category,
        ticket.UploadedAt,
        ticket.AnalysisFileUrl,
        ticket.AnalysisFileName,
        ticket.LastErrorMessage
    );
}
