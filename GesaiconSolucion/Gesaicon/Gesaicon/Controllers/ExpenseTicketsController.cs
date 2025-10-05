using Microsoft.AspNetCore.Mvc;
using Gesaicon.Data;
using Gesaicon.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Gesaicon.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ExpenseTicketsController : ControllerBase
    {
        private readonly GesaiconDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;

        public ExpenseTicketsController(GesaiconDbContext context, IWebHostEnvironment env, IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _env = env;
            _config = config;
            _httpClientFactory = httpClientFactory;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload([FromForm] IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            var uploads = Path.Combine(_env.ContentRootPath, "Uploads");
            if (!Directory.Exists(uploads))
                Directory.CreateDirectory(uploads);

            var fileName = Guid.NewGuid() + Path.GetExtension(file.FileName);
            var filePath = Path.Combine(uploads, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var ticket = new ExpenseTicket
            {
                FileName = fileName,
                FileUrl = $"/Uploads/{fileName}",
                UploadedAt = DateTime.UtcNow,
                Status = "Processing"
            };

            _context.ExpenseTickets.Add(ticket);
            await _context.SaveChangesAsync();

            // Integración con GROQ
            try
            {
                var groqApiKey = _config["GROQ:ApiKey"];
                var groqEndpoint = _config["GROQ:Endpoint"] ?? "https://api.groq.com/v1/ocr";
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", groqApiKey);

                using var imageStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var content = new MultipartFormDataContent();
                content.Add(new StreamContent(imageStream), "file", fileName);

                var response = await client.PostAsync(groqEndpoint, content);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    // Suponiendo que la respuesta tiene los campos: amount, company, category
                    var groqResult = JsonSerializer.Deserialize<GroqResult>(json);
                    ticket.Amount = groqResult?.Amount;
                    ticket.CompanyName = groqResult?.Company;
                    ticket.Category = groqResult?.Category;
                    ticket.Status = "Completed";
                }
                else
                {
                    ticket.Status = "Error";
                }
            }
            catch
            {
                ticket.Status = "Error";
            }

            await _context.SaveChangesAsync();
            return Ok(ticket);
        }

        private class GroqResult
        {
            public decimal? Amount { get; set; }
            public string? Company { get; set; }
            public string? Category { get; set; }
        }
    }
}
