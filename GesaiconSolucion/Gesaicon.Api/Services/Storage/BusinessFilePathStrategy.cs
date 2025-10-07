namespace Gesaicon.Api.Services.Storage;

public sealed class BusinessFilePathStrategy : IBusinessFilePathStrategy
{
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".txt"
    };

    public (string DirectoryRelative, string FileName) Generate(
        string companySlug,
        int year,
        int month,
        string ticketId,
        string originalFileName)
    {
        var ext = Path.GetExtension(originalFileName);
        ext = string.IsNullOrWhiteSpace(ext) ? string.Empty : ext.ToLowerInvariant();
        
        if (!AllowedExt.Contains(ext))
            throw new InvalidOperationException($"Extensión no permitida: {ext}");

        var dir = Path.Combine(
            "empresas",
            Slug(companySlug),
            year.ToString("0000"),
            month.ToString("00"));

        var safeTicket = SanitizeId(ticketId);
        var fileName = $"{safeTicket}{ext}";

        return (dir, fileName);
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());

    private static string SanitizeId(string id) =>
        new string(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
