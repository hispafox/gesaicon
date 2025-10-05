namespace Gesaicon.Api.Models
{
    public class ChatCompletionRequestDto
    {
        public string? Prompt { get; set; }
        public string? Model { get; set; }
        public decimal? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public decimal? TopP { get; set; }
    }

    public class ChatCompletionResponseDto
    {
        public string? Content { get; set; }
        public string? Model { get; set; }
        public string? Raw { get; set; }
    }

    public class GroqChatApiResponse
    {
        public string? id { get; set; }
        public string? @object { get; set; }
        public long created { get; set; }
        public string? model { get; set; }
        public List<GroqChatChoice>? choices { get; set; }
    }

    public class GroqChatChoice
    {
        public int index { get; set; }
        public GroqChatMessage? message { get; set; }
        public GroqChatDelta? delta { get; set; }
        public string? finish_reason { get; set; }
    }

    public class GroqChatMessage
    {
        public string? role { get; set; }
        public string? content { get; set; }
    }

    public class GroqChatDelta
    {
        public string? role { get; set; }
        public string? content { get; set; }
    }
}
