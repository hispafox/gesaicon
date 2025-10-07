using System.ComponentModel.DataAnnotations.Schema;

namespace Gesaicon.Api.Models
{
    public class ExpenseTicket
    {
        public int Id { get; set; }
        public Guid PublicId { get; set; } // nuevo identificador público estable
        
        // Nuevos campos para estructura empresas/año/mes
        public string? CompanySlug { get; set; }
        public int? ExpenseYear { get; set; }
        public int? ExpenseMonth { get; set; }
        
        public string? FileName { get; set; }
        public string? FileUrl { get; set; }
        public string? RelativePath { get; set; } // NUEVO: guarda la ruta relativa completa desde Uploads
        
        public long? FileSizeBytes { get; set; } // tamaño
        public string? FileHash { get; set; } // hash SHA256 para duplicados
        [Column(TypeName="decimal(18,2)")] public decimal? Amount { get; set; }
        public string? CompanyName { get; set; }
        public string? Category { get; set; }
        public DateTime UploadedAt { get; set; }
        public string? Status { get; set; } // Pending, Processing, Completed, Error
        public string? UserId { get; set; } // Optional: multi-user support
        public string? AnalysisMarkdown { get; set; } // análisis completo Markdown
        public string? AnalysisJson { get; set; } // JSON estructurado completo
        public string? AnalysisFileName { get; set; } // nuevo: nombre archivo markdown persistido
        public string? AnalysisFileUrl { get; set; } // nuevo: url pública del archivo markdown
        public int RetryCount { get; set; } // reintentos de análisis automáticos
        public string? LastErrorMessage { get; set; } // último mensaje de error si Status == "Error"
    }
}
