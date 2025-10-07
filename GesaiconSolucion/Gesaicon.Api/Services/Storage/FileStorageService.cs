using System.Security.Cryptography;

namespace Gesaicon.Api.Services.Storage;

public sealed class FileStorageService : IFileStorageService
{
    private readonly string _uploadsRoot;
    private readonly IBusinessFilePathStrategy _pathStrategy;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(
        IHostEnvironment env,
        IBusinessFilePathStrategy pathStrategy,
        ILogger<FileStorageService> logger)
    {
        _uploadsRoot = Path.Combine(env.ContentRootPath, "Uploads");
        _pathStrategy = pathStrategy;
        _logger = logger;
        Directory.CreateDirectory(_uploadsRoot);
    }

    public async Task<StoredFileInfo> SaveTicketFileAsync(
        Stream content,
        string companySlug,
        int year,
        int month,
        string ticketId,
        string originalFileName,
        CancellationToken ct = default)
    {
        if (content.Length == 0)
            throw new InvalidOperationException("Archivo vacío.");

        var (dirRel, fileName) = _pathStrategy.Generate(
            companySlug, year, month, ticketId, originalFileName);

        var fullDir = Path.Combine(_uploadsRoot, dirRel);
        Directory.CreateDirectory(fullDir);

        var fullPath = Path.Combine(fullDir, fileName);

        if (File.Exists(fullPath))
            throw new InvalidOperationException($"Ya existe un archivo para el ticket {ticketId} en {dirRel}");

        string hash;
        long size;
        
        await using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            using var sha = SHA256.Create();
            var buffer = new byte[64 * 1024];
            int read;
            size = 0;
            
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.TransformBlock(buffer, 0, read, null, 0);
                size += read;
            }
            
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            hash = Convert.ToHexString(sha.Hash!);
        }

        var relativePath = Path.Combine(dirRel, fileName).Replace('\\', '/');
        
        _logger.LogInformation("[Storage] Guardado {TicketId} en {RelPath}, {Size} bytes, hash={Hash}",
            ticketId, relativePath, size, hash);

        return new StoredFileInfo(ticketId, relativePath, originalFileName, size, hash);
    }

    public string GetPhysicalPath(string relativePath)
    {
        return Path.Combine(_uploadsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public bool FileExists(string relativePath)
    {
        var physical = GetPhysicalPath(relativePath);
        return File.Exists(physical);
    }
}
