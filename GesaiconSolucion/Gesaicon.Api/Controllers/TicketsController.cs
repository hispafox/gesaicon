using Gesaicon.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Gesaicon.Api.Controllers;

[ApiController]
[Route("api/tickets")]
public class TicketsController : ControllerBase
{
    private readonly GesaiconDbContext _db;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(GesaiconDbContext db, ILogger<TicketsController> logger)
    {
        _db = db;
        _logger = logger;
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
        string? AnalysisFileName
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
                t.Status,
                t.Amount,
                t.CompanyName,
                t.Category,
                t.UploadedAt,
                t.AnalysisFileUrl,
                t.AnalysisFileName
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
                t.Status,
                t.Amount,
                t.CompanyName,
                t.Category,
                t.UploadedAt,
                t.AnalysisFileUrl,
                t.AnalysisFileName
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
}
