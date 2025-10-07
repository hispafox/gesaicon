namespace Gesaicon.Api.Services.Storage;

public sealed record StoredFileInfo(
    string TicketId,
    string RelativePath,
    string OriginalFileName,
    long SizeBytes,
    string Hash);

public interface IFileStorageService
{
    /// <summary>
    /// Guarda un archivo para un ticket con estructura empresas/año/mes
    /// </summary>
    Task<StoredFileInfo> SaveTicketFileAsync(
        Stream content,
        string companySlug,
        int year,
        int month,
        string ticketId,
        string originalFileName,
        CancellationToken ct = default);

    /// <summary>
    /// Obtiene la ruta física completa dado un RelativePath
    /// </summary>
    string GetPhysicalPath(string relativePath);

    /// <summary>
    /// Verifica si existe un archivo dado su RelativePath
    /// </summary>
    bool FileExists(string relativePath);
}
