using Gesaicon.Api.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Gesaicon.Api.Controllers;

[ApiController]
[Route("api/diagnostics")] 
public class DiagnosticsController : ControllerBase
{
    private readonly BackgroundStatusStore _store;
    public DiagnosticsController(BackgroundStatusStore store) => _store = store;

    [HttpGet("background-status")] 
    public IActionResult GetBackgroundStatus()
    {
        var list = _store.Snapshot().Select(s => new {
            s.ServiceName,
            s.LastMessage,
            LastUpdateUtc = s.LastUpdateUtc,
            s.Processed,
            s.Errors,
            s.Running
        });
        return Ok(list);
    }
}
