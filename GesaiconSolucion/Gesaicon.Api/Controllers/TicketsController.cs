using Gesaicon.Api.Data;
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
    private readonly IReceiptAnalysisQueue _queue; // nuevo

    public TicketsController(GesaiconDbContext db, ILogger<TicketsController> logger, IReceiptAnalysisQueue queue) // actualizado
    {
        _db = db;
        _logger = logger;
        _queue = queue;
    }

    public record ExpenseTicketDto(
        int Id,
        Guid PublicId,
        string? FileName,
        string? FileUrl,
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
                t.FileName,
                t.FileUrl,
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
                t.FileName,
                t.FileUrl,
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
    public async Task<IActionResult> GetAnalysisMarkdown(int id)
    {
        var ticket = await _db.ExpenseTickets.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.AnalysisMarkdown, t.AnalysisFileName })
            .FirstOrDefaultAsync();
        if (ticket == null) return NotFound();

        string? md = ticket.AnalysisMarkdown;
        if (string.IsNullOrWhiteSpace(md) && !string.IsNullOrWhiteSpace(ticket.AnalysisFileName))
        {
            try
            {
                var uploads = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
                var path = Path.Combine(uploads, ticket.AnalysisFileName);
                if (System.IO.File.Exists(path))
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
    public async Task<IActionResult> Reprocess(int id, [FromQuery] bool force = false)
    {
        var ticket = await _db.ExpenseTickets.FirstOrDefaultAsync(t => t.Id == id);
        if (ticket == null) return NotFound();

        if (ticket.Status == "Processing" && !force)
            return Conflict(new { message = "Ya se está procesando (use force=true si está atascado)" });

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
        ticket.LastErrorMessage = null; // Limpiar error anterior
        await _db.SaveChangesAsync();

        await _queue.EnqueueAsync(ticket.Id, ticket.RetryCount);

        var dto = new ExpenseTicketDto(
            ticket.Id,
            ticket.PublicId,
            ticket.FileName,
            ticket.FileUrl,
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
        return Ok(dto);
    }
}
