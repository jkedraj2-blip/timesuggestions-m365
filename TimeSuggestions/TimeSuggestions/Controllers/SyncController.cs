using Microsoft.AspNetCore.Mvc;
using TimeSuggestions.Contracts;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(SyncService syncService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SyncResult>> Sync(SyncRequest request, CancellationToken cancellationToken)
    {
        var result = await syncService.SyncAsync(request, DateTime.UtcNow, cancellationToken);
        return Ok(result);
    }
}
