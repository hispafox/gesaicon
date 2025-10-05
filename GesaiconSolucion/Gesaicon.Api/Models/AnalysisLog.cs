using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Gesaicon.Api.Models
{
    public class AnalysisLog
    {
        [Key]
        public long Id { get; set; }
        public int? TicketId { get; set; }
        [MaxLength(32)] public string Operation { get; set; } = "Single"; // Single|Advanced|Batch
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public long? DurationMs { get; set; }
        public bool Success { get; set; }
        [MaxLength(256)] public string? ErrorMessage { get; set; }
        [MaxLength(128)] public string? Model { get; set; }
        public int Attempt { get; set; }
        [Column(TypeName="decimal(18,2)")] public decimal? Amount { get; set; }
        public string? Company { get; set; }
        public string? Category { get; set; }
        [MaxLength(128)] public string? FileHashSnapshot { get; set; }
        [MaxLength(128)] public string? Endpoint { get; set; }
        public string? JsonUsageRaw { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
    }
}
