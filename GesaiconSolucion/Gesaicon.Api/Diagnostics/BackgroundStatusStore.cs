using System.Collections.Concurrent;

namespace Gesaicon.Api.Diagnostics;

public sealed class BackgroundStatusStore
{
    private readonly ConcurrentDictionary<string, BackgroundServiceStatus> _map = new();

    public IReadOnlyCollection<BackgroundServiceStatus> Snapshot()
        => _map.Values.OrderBy(s => s.ServiceName).ToList();

    public void Update(string serviceName, string message, int? processedDelta = null, int? errorDelta = null, bool? running = null)
    {
        var now = DateTime.UtcNow;
        _map.AddOrUpdate(serviceName,
            _ => new BackgroundServiceStatus(serviceName)
            {
                LastMessage = message,
                LastUpdateUtc = now,
                Processed = processedDelta ?? 0,
                Errors = errorDelta ?? 0,
                Running = running ?? true
            },
            (_, existing) =>
            {
                if (processedDelta.HasValue) existing.Processed += processedDelta.Value;
                if (errorDelta.HasValue) existing.Errors += errorDelta.Value;
                existing.LastMessage = message;
                existing.LastUpdateUtc = now;
                if (running.HasValue) existing.Running = running.Value;
                return existing;
            });
    }
}

public sealed class BackgroundServiceStatus
{
    public BackgroundServiceStatus(string serviceName) => ServiceName = serviceName;
    public string ServiceName { get; }
    public string LastMessage { get; set; } = string.Empty;
    public DateTime LastUpdateUtc { get; set; } = DateTime.MinValue;
    public int Processed { get; set; }
    public int Errors { get; set; }
    public bool Running { get; set; }
}