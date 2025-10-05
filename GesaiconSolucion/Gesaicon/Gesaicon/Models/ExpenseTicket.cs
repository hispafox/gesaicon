namespace Gesaicon.Models
{
    public class ExpenseTicket
    {
        public int Id { get; set; }
        public string? FileName { get; set; }
        public string? FileUrl { get; set; }
        public decimal? Amount { get; set; }
        public string? CompanyName { get; set; }
        public string? Category { get; set; }
        public DateTime UploadedAt { get; set; }
        public string? Status { get; set; } // Pending, Processing, Completed, Error
        public string? UserId { get; set; } // Optional: for multi-user support
    }
}
