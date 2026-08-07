using Microsoft.AspNetCore.Mvc;
using TimeSuggestions.Contracts;
using TimeSuggestions.Services;

namespace TimeSuggestions.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(SyncService syncService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SyncReport>> Sync(SyncRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var report = await syncService.SyncAsync(request, DateTime.UtcNow, cancellationToken);
            return Ok(report);
        }
        catch (SyncConflictException exception)
        {
            // Konflikt unikalności przetrwał ponowienie — dwie synchronizacje naraz.
            return Conflict(new { message = exception.Message });
        }
    }
}
