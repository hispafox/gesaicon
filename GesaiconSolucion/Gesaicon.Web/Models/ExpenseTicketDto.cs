namespace Gesaicon.Web.Models;

public record ExpenseTicketDto(
    int Id,
    Guid PublicId,
    string? CompanySlug,
    int? ExpenseYear,
    int? ExpenseMonth,
    DateTime? PurchaseDate,
    string? FileName,
    string? FileUrl,
    string? RelativePath,
    long FileSizeBytes,
    string Status,
    decimal? Amount,
    string? CompanyName,
    string? Category,
    DateTime UploadedAt,
    string? AnalysisFileUrl,
    string? AnalysisFileName,
    string? LastErrorMessage
)
{
    /// <summary>
    /// Indica si el archivo está en estructura legacy (sin RelativePath)
    /// </summary>
    public bool IsLegacy => string.IsNullOrWhiteSpace(RelativePath);
    
    /// <summary>
    /// Obtiene la ruta visible para el usuario
    /// </summary>
    public string DisplayPath => IsLegacy 
        ? $"?? Legacy: Uploads/{FileName}" 
        : $"?? {RelativePath}";
    
    /// <summary>
    /// Ruta corta para UI compacta
    /// </summary>
    public string ShortPath => IsLegacy 
        ? "Legacy" 
        : $"{CompanySlug}/{ExpenseYear}/{ExpenseMonth:00}";
}

public record PagedResult<T>(int Total, List<T> Items);
