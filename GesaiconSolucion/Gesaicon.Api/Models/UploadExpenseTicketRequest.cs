using Microsoft.AspNetCore.Http;

namespace Gesaicon.Api.Models
{
    public class UploadExpenseTicketRequest
    {
        public IFormFile? File { get; set; }
    }
}
