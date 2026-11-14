using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using PointOfSale.API.Attributes;
using PointOfSale.API.Configuration;
using PointOfSale.API.Development.CommerceHub;
using PointOfSale.API.DTOs.CommerceHub;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.API.Services.Payments.Paymob;
using PointOfSale.API.SuperAdmin.Models;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Controllers;

[DevelopmentOnlyController, AllowAnonymousTenant, ApiController, ApiExplorerSettings(IgnoreApi = true)]
[Route("api/dev/commercehub/p520f/{run}")]
[Authorize(Policy = SuperAdminSecurityConstants.PolicyName)]
public sealed class DevelopmentP520FController(P520FRunClock clock, ApplicationDbContext db) : ControllerBase
{
    [HttpGet("clock")]
    public IActionResult Clock(string run) => clock.Enabled && clock.RunId == run ? Ok(clock.Provenance()) : NotFound();
    public sealed record AdvanceRequest(DateTime ExpectedUtc, DateTime TargetUtc);
    [HttpPost("orders/{orderId:guid}/resume-clock")]
    public async Task<IActionResult> Resume(string run, Guid orderId, CancellationToken ct)
    {
        if (!clock.Enabled || clock.RunId != run) return NotFound();
        var order = await db.CommerceHubMarketplaceOrders.AsNoTracking().Include(o => o.SubOrders)
            .Include(o => o.Customer).SingleOrDefaultAsync(o => o.Id == orderId && !o.IsDeleted, ct);
        if (order == null || order.Notes?.StartsWith(run + " | ", StringComparison.Ordinal) != true ||
            order.Customer.Email?.StartsWith("p520d-20261028-e2e01-", StringComparison.OrdinalIgnoreCase) != true ||
            order.SubOrders.Any(s => !s.StoreName.StartsWith("P520D-20261028-E2E01-", StringComparison.Ordinal))) return Forbid();
        var enrollments = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == orderId.ToString() &&
            a.ActionType == "P520F_CLOCK_ORDER_ENROLLED" && a.Source == "DevelopmentAcceptance" && a.SuccessFlag).ToListAsync(ct);
        var advances = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == run && a.ActionType == "P520F_CLOCK_ADVANCED" &&
            a.Source == "DevelopmentAcceptance" && a.SuccessFlag).OrderByDescending(a => a.LogId).ToListAsync(ct);
        if (enrollments.Count != 1 || advances.Count == 0) return Conflict(new { code = "P520F_AUDITED_ENROLLMENT_REQUIRED" });
        try
        {
            using var enrolled = JsonDocument.Parse(enrollments[0].AfterDataJson!);
            using var advanced = JsonDocument.Parse(advances[0].AfterDataJson!);
            foreach (var root in new[] { enrolled.RootElement, advanced.RootElement })
                if (root.GetProperty("RunId").GetString() != run || !root.GetProperty("IsSimulatedTime").GetBoolean() ||
                    root.GetProperty("ExternalMoneyMovementConfirmed").GetBoolean() ||
                    !root.GetProperty("EnrolledOrderIds").EnumerateArray().Any(v => v.GetGuid() == orderId))
                    return Conflict(new { code = "P520F_AUDITED_ENROLLMENT_INVALID" });
            var target = advanced.RootElement.GetProperty("VirtualUtc").GetDateTime();
            db.AuditLogs.Add(new AuditLog { ActionType = "P520F_CLOCK_RESUMED", EntityType = "CommerceHubMarketplaceOrder", EntityId = orderId.ToString(),
                UserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), Source = "DevelopmentAcceptance", SuccessFlag = true,
                Description = "Same run restored from original checkout enrollment and monotonic audited virtual time; no timestamp rewrite.",
                AfterDataJson = JsonSerializer.Serialize(new { RunId = run, OrderId = orderId, VirtualUtc = target, IsSimulatedTime = true,
                    ExternalMoneyMovementConfirmed = false, EnrollmentLogId = enrollments[0].LogId, AdvanceLogId = advances[0].LogId }) });
            if (target < clock.VirtualUtc) return Conflict(new { code = "P520F_CLOCK_REWIND_FORBIDDEN" });
            await db.SaveChangesAsync(ct);clock.RestoreAuditedEnrollment(run, order, target);return Ok(clock.Provenance());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return Conflict(new { code = "P520F_AUDITED_ENROLLMENT_INVALID" }); }
    }
    [HttpPost("clock/advance")]
    public async Task<IActionResult> Advance(string run, AdvanceRequest request, CancellationToken ct)
    {
        if (!clock.Enabled || clock.RunId != run) return NotFound();
        try
        {
            var result = await clock.AdvanceAuditedAsync(run, request.ExpectedUtc, request.TargetUtc, async evidence => {
            db.AuditLogs.Add(new AuditLog { ActionType = "P520F_CLOCK_ADVANCED", EntityType = "DevelopmentClock", EntityId = run,
                UserId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), Source = "DevelopmentAcceptance", SuccessFlag = true,
                Description = "Virtual time advanced monotonically; persisted business timestamps were not rewritten.",
                BeforeDataJson = JsonSerializer.Serialize(request), AfterDataJson = JsonSerializer.Serialize(evidence) });
            await db.SaveChangesAsync(ct); }, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return Conflict(new { code = "P520F_CLOCK_ADVANCE_REJECTED", ex.Message }); }
    }
    [HttpPost("vendors/{vendorId:guid}/payout/{step}")]
    public async Task<IActionResult> Payout(string run, Guid vendorId, string step, CommerceHubCreatePayoutRequestDto request,
        [FromServices] ICommerceHubPayoutService service, [FromServices] IPaymobSensitiveArtifactProtector protector,
        [FromServices] IOptions<RefundSettings> refunds, CancellationToken ct)
    {
        if (!clock.Enabled || clock.RunId != run) return NotFound();
        try { return Ok(new { payout = await ((CommerceHubPayoutService)service).StepP520FAsync(run, step, vendorId, request,
            User.FindFirstValue(ClaimTypes.NameIdentifier)!, protector, refunds.Value, ct), Clock = clock.Provenance(),
            EvidenceType = "DevelopmentSyntheticVendorSettlement", IsDevelopmentSimulation = true, ExternalMoneyMovementConfirmed = false }); }
        catch (CommerceHubCheckoutException ex) { return StatusCode(ex.StatusCode, new { ex.Code, ex.Message }); }
    }
}
