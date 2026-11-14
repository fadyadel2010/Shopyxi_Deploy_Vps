using System.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PointOfSale.API.Services;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.API.Controllers;

[ApiController]
[Authorize(Roles = "Owner,Admin")]
[Route("api/admin/affiliates/payouts/{requestId:guid}/qa-simulate")]
public sealed class AffiliateQaPayoutController(AffiliateQaPayoutSimulation simulation, ITenantContext tenant) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Execute(Guid requestId, [FromBody] QaPayoutSimulationBody body, CancellationToken ct)
    {
        // The dormant surface is not an alternative manual payment route.
        if (!simulation.IsEnabled) return NotFound(new { code = "AFFILIATE_PAYOUT_QA_SIMULATION_DISABLED" });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) return Unauthorized();
        try
        {
            var result = await simulation.ExecuteAsync(actor, tenant.RequiredTenantId, requestId, body.Acknowledgement, ct);
            return Ok(new { id = requestId, simulated = true, moneyTransferred = false,
                result.WasIdempotentSkip, paymentReference = AffiliateQaPayoutSimulation.Reference(requestId) });
        }
        catch (SecurityException) { return Forbid(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var error = AffiliateFinancialErrorContract.From(ex);
            return StatusCode(error.StatusCode, new { code = error.Code, message = error.Message });
        }
    }
}

public sealed class QaPayoutSimulationBody
{
    public string Acknowledgement { get; set; } = "";
}
