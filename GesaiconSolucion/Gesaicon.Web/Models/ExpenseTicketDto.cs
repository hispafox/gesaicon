namespace Gesaicon.Web.Models;

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
    string? AnalysisFileName,
    string? LastErrorMessage
);

public record PagedResult<T>(int Total, List<T> Items);
