using Microsoft.AspNetCore.Mvc;
using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Gesaicon.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ExpenseTicketsController : ControllerBase
    {
        private readonly GesaiconDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config; // reservado
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
            var list = await _context.ExpenseTickets
                .OrderByDescending(t => t.UploadedAt)
                .AsNoTracking()
                .ToListAsync();
            return Ok(list);
        }

        [HttpGet("by-public/{publicId:guid}")]
        public async Task<IActionResult> GetByPublicId(Guid publicId)
        {
            var ticket = await _context.ExpenseTickets.AsNoTracking().FirstOrDefaultAsync(t => t.PublicId == publicId);
            if (ticket == null) return NotFound();
            return Ok(ticket);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var ticket = await _context.ExpenseTickets.FindAsync(id);
            if (ticket == null) return NotFound();

            if (!string.IsNullOrEmpty(ticket.FileUrl))
            {
                try
                {
                    var uploadsRoot = Path.Combine(_env.ContentRootPath, "Uploads");
                    var safeRelative = ticket.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(_env.ContentRootPath, safeRelative));
                    if (fullPath.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
                    {
                        System.IO.File.Delete(fullPath);
                    }
                }
                catch { }
            }

            _context.ExpenseTickets.Remove(ticket);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpPut("reprocess/{id}")]
        public async Task<IActionResult> Reprocess(int id)
        {
            var ticket = await _context.ExpenseTickets.FindAsync(id);
            if (ticket == null) return NotFound();

            var physicalPath = string.IsNullOrWhiteSpace(ticket.FileUrl) ? null : Path.Combine(_env.ContentRootPath, ticket.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (physicalPath == null || !System.IO.File.Exists(physicalPath)) return BadRequest("Archivo no encontrado");

            ticket.Status = "PendingAnalysis";
            ticket.Amount = null;
            ticket.CompanyName = null;
            ticket.Category = null;
            ticket.AnalysisJson = null;
            ticket.AnalysisMarkdown = null;
            await _context.SaveChangesAsync();
            return Ok(ticket);
        }

        [HttpPost("upload")]
        [ProducesResponseType(typeof(ExpenseTicket), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Upload([FromForm] UploadExpenseTicketRequest request)
        {
            var file = request.File;
            if (file == null || file.Length == 0) return BadRequest("No file uploaded.");

            const long maxSizeBytes = 5 * 1024 * 1024; // 5MB
            if (file.Length > maxSizeBytes) return BadRequest("File too large. Max 5MB");

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var ext = Path.GetExtension(file.FileName);
            if (!allowed.Contains(ext)) return BadRequest("Unsupported file type");

            byte[] data;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                data = ms.ToArray();
            }
            string hash;
            using (var sha = SHA256.Create()) hash = Convert.ToHexString(sha.ComputeHash(data));

            var existing = await _context.ExpenseTickets
                .Where(t => t.FileHash == hash)
                .OrderByDescending(t => t.UploadedAt)
                .FirstOrDefaultAsync();
            if (existing != null) return Ok(new { duplicateOf = existing.Id, existing });

            var uploads = Path.Combine(_env.ContentRootPath, "Uploads");
            if (!Directory.Exists(uploads)) Directory.CreateDirectory(uploads);

            var publicId = Guid.NewGuid();
            var fileName = publicId + ext;
            var filePath = Path.Combine(uploads, fileName);
            await System.IO.File.WriteAllBytesAsync(filePath, data);

            var ticket = new ExpenseTicket
            {
                PublicId = publicId,
                FileName = fileName,
                FileUrl = $"/Uploads/{fileName}",
                FileSizeBytes = data.Length,
                FileHash = hash,
                UploadedAt = DateTime.UtcNow,
                Status = "PendingAnalysis"
            };

            _context.ExpenseTickets.Add(ticket);
            await _context.SaveChangesAsync();
            return Ok(ticket);
        }

        [HttpGet("{id}/analysis")]
        public async Task<IActionResult> GetAnalysis(int id)
        {
            var ticket = await _context.ExpenseTickets.FindAsync(id);
            if (ticket == null) return NotFound();
            return Ok(new
            {
                ticket.Id,
                ticket.PublicId,
                ticket.Amount,
                Company = ticket.CompanyName,
                ticket.Category,
                ticket.Status,
                ticket.AnalysisJson,
                ticket.AnalysisMarkdown
            });
        }

        [HttpPost("cleanup/orphans")]
        public async Task<IActionResult> CleanupOrphans()
        {
            var uploads = Path.Combine(_env.ContentRootPath, "Uploads");
            if (!Directory.Exists(uploads)) return Ok(new { deleted = 0 });

            var known = await _context.ExpenseTickets.Select(t => t.FileName!).Where(n => n != null).ToListAsync();
            var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
            int deleted = 0;
            foreach (var file in Directory.EnumerateFiles(uploads))
            {
                var name = Path.GetFileName(file);
                if (!set.Contains(name))
                {
                    try { System.IO.File.Delete(file); deleted++; } catch { }
                }
            }
            return Ok(new { deleted });
        }

        [HttpPost("enqueue/{id}")]
        public async Task<IActionResult> Enqueue(int id, [FromServices] Gesaicon.Api.Services.IReceiptAnalysisQueue queue)
        {
            var ticket = await _context.ExpenseTickets.FindAsync(id);
            if (ticket == null) return NotFound();
            if (ticket.Status == "Completed") return BadRequest("Ya completado");
            await queue.EnqueueAsync(id);
            return Accepted(new { id, ticket.PublicId, status = "Enqueued" });
        }
    }
}
