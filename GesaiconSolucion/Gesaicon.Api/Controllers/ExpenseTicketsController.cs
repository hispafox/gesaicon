using Microsoft.AspNetCore.Mvc;
using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gesaicon.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ExpenseTicketsController : ControllerBase
    {
        private readonly GesaiconDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config; // reservado si se necesita en el futuro
        private readonly ILogger<ExpenseTicketsController> _logger;

        public ExpenseTicketsController(GesaiconDbContext context, IWebHostEnvironment env, IConfiguration config, ILogger<ExpenseTicketsController> logger)
        {
            _context = context;
            _env = env;
            _config = config;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<ExpenseTicket>>> GetAll()
        {
            _logger.LogInformation("[Tickets] Listando tickets de gastos");
            var list = await _context.ExpenseTickets
                .OrderByDescending(t => t.UploadedAt)
                .AsNoTracking()
                .ToListAsync();
            _logger.LogInformation("[Tickets] {Count} tickets devueltos", list.Count);
            return Ok(list);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            _logger.LogInformation("[Tickets] Solicitud de eliminación para ticket {Id}", id);
            var ticket = await _context.ExpenseTickets.FindAsync(id);
            if (ticket == null)
            {
                _logger.LogWarning("[Tickets] Ticket {Id} no encontrado", id);
                return NotFound();
            }

            if (!string.IsNullOrEmpty(ticket.FileUrl))
            {
                try
                {
                    var uploadsRoot = Path.Combine(_env.ContentRootPath, "Uploads");
                    var safeRelative = ticket.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(_env.ContentRootPath, safeRelative));
                    if (!fullPath.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[Tickets] Ruta fuera de Uploads bloqueada: {Path}", fullPath);
                    }
                    else if (System.IO.File.Exists(fullPath))
                    {
                        System.IO.File.Delete(fullPath);
                        _logger.LogInformation("[Tickets] Archivo {Path} eliminado", fullPath);
                    }
                    else
                    {
                        _logger.LogWarning("[Tickets] Archivo físico {Path} no existe", fullPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Tickets] Error eliminando archivo adjunto del ticket {Id}", id);
                }
            }

            _context.ExpenseTickets.Remove(ticket);
            await _context.SaveChangesAsync();
            _logger.LogInformation("[Tickets] Ticket {Id} eliminado", id);
            return NoContent();
        }

        [HttpPut("reprocess/{id}")]
        public async Task<IActionResult> Reprocess(int id)
        {
            _logger.LogInformation("[Tickets] Reproceso marcado para ticket {Id}", id);
            var ticket = await _context.ExpenseTickets.FindAsync(id);
            if (ticket == null)
            {
                _logger.LogWarning("[Tickets] Ticket {Id} no encontrado para reprocesar", id);
                return NotFound();
            }

            // Verificar archivo
            var physicalPath = string.IsNullOrWhiteSpace(ticket.FileUrl) ? null : Path.Combine(_env.ContentRootPath, ticket.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (physicalPath == null || !System.IO.File.Exists(physicalPath))
            {
                _logger.LogWarning("[Tickets] Archivo para ticket {Id} no existe en disco", id);
                return BadRequest("Archivo no encontrado");
            }

            // Limpiar datos de análisis previos y marcar pendiente
            ticket.Status = "PendingAnalysis";
            ticket.Amount = null;
            ticket.CompanyName = null;
            ticket.Category = null;
            ticket.AnalysisJson = null;
            ticket.AnalysisMarkdown = null;
            await _context.SaveChangesAsync();
            _logger.LogInformation("[Tickets] Ticket {Id} marcado PendingAnalysis. Ejecutar /api/ReceiptAnalysis/analyze?ticketId={Id}", id);
            return Ok(ticket);
        }

        [HttpPost("upload")]
        [ProducesResponseType(typeof(ExpenseTicket), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Upload([FromForm] UploadExpenseTicketRequest request)
        {
            var file = request.File;
            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("[Tickets] Upload sin archivo o tamaño 0");
                return BadRequest("No file uploaded.");
            }

            var uploads = Path.Combine(_env.ContentRootPath, "Uploads");
            if (!Directory.Exists(uploads))
            {
                Directory.CreateDirectory(uploads);
                _logger.LogInformation("[Tickets] Directorio Uploads creado en {Dir}", uploads);
            }

            var fileName = Guid.NewGuid() + Path.GetExtension(file.FileName);
            var filePath = Path.Combine(uploads, fileName);

            try
            {
                await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await file.CopyToAsync(stream);
                _logger.LogInformation("[Tickets] Archivo guardado en {Path} ({Size} bytes)", filePath, file.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Tickets] Error guardando archivo {Path}", filePath);
                return StatusCode(500, "Error guardando archivo");
            }

            var ticket = new ExpenseTicket
            {
                FileName = fileName,
                FileUrl = $"/Uploads/{fileName}",
                UploadedAt = DateTime.UtcNow,
                Status = "PendingAnalysis"
            };

            _context.ExpenseTickets.Add(ticket);
            await _context.SaveChangesAsync();
            _logger.LogInformation("[Tickets] Ticket {Id} creado (PendingAnalysis). Ejecutar /api/ReceiptAnalysis/analyze?ticketId={Id} para analizar", ticket.Id);
            return Ok(ticket);
        }

        [HttpGet("{id}/analysis")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAnalysis(int id)
        {
            var ticket = await _context.ExpenseTickets.FindAsync(id);
            if (ticket == null)
            {
                _logger.LogWarning("[Tickets] Análisis solicitado para ticket inexistente {Id}", id);
                return NotFound();
            }
            return Ok(new
            {
                TicketId = ticket.Id,
                ticket.Amount,
                Company = ticket.CompanyName,
                Category = ticket.Category,
                ticket.Status,
                ticket.AnalysisJson,
                ticket.AnalysisMarkdown
            });
        }
    }
}
