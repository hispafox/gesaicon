using Microsoft.AspNetCore.Http;

namespace Gesaicon.Api.Models
{
    public class ReceiptAnalysisRequest
    {
        public IFormFile File { get; set; } = default!; // requerido
        public decimal? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public decimal? TopP { get; set; }
        public bool Stream { get; set; }
        public bool JsonMode { get; set; }
        public string? Model { get; set; }
        public string? ExtraPrompt { get; set; }
    }
}
