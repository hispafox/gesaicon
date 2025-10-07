namespace Gesaicon.Api.Services.Storage;

public interface IBusinessFilePathStrategy
{
    /// <summary>
    /// Genera la ruta relativa y nombre de archivo según el patrón empresas/{slug}/{yyyy}/{MM}/{ticketId}.ext
    /// </summary>
    (string DirectoryRelative, string FileName) Generate(
        string companySlug,
        int year,
        int month,
        string ticketId,
        string originalFileName);
}
