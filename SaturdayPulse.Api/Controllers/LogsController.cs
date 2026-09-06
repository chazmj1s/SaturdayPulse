using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaturdayPulse.Filters;
using SaturdayPulse.Services;

namespace SaturdayPulse.Controllers
{
    /// <summary>
    /// Read-only access to recent server-side log entries (ServerLogService),
    /// for the Mobile app's Debug Log page to merge alongside its on-device
    /// entries. Deliberately NOT under DeveloperController/[AdminKey] — that
    /// gate is a shared secret meant for AdminBlazor (a process only Charlie
    /// runs), and would have to ship inside the Mobile app binary, which is
    /// decompilable on both iOS and Android. This uses the same real
    /// Auth0-login + IsAdmin check already covering other admin-only mobile
    /// features (see AdminOnlyAttribute), so no secret is embedded in the app.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    [AdminOnly]
    public class LogsController(ServerLogService serverLogService) : ControllerBase
    {
        /// <summary>
        /// GET /api/logs?take=200 — most recent server log entries, newest
        /// first, capped at `take` (default 200).
        /// </summary>
        [HttpGet]
        public IActionResult GetLogs([FromQuery] int take = 200)
        {
            var entries = serverLogService.GetRecent(take);
            return Ok(entries);
        }
    }
}
