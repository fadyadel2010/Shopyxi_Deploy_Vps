using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using PointOfSale.API.Development.CommerceHub;
using PointOfSale.API.Configuration;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;

namespace PointOfSale.Tests.CommerceHub;

public class P520FRunClockTests
{
    private const string Run = "P520F-20261028-TERMINAL01";
    [Theory]
    [InlineData("Development", Run, "ALLOW-SYNTHETIC-TIME:" + Run, true)]
    [InlineData("Production", Run, "ALLOW-SYNTHETIC-TIME:" + Run, false)]
    [InlineData("Staging", Run, "ALLOW-SYNTHETIC-TIME:" + Run, false)]
    [InlineData(null, Run, "ALLOW-SYNTHETIC-TIME:" + Run, false)]
    [InlineData("Development", Run, null, false)]
    [InlineData("Development", Run, "true", false)]
    [InlineData("Development", Run, "ALLOW-SYNTHETIC-TIME:P520F-20261028-OTHER123", false)]
    [InlineData("Development", "P520E-20261028-TERMINAL01", "ALLOW-SYNTHETIC-TIME:P520E-20261028-TERMINAL01", false)]
    public void Admission_is_exact_and_fail_closed(string? environment, string? run, string? optIn, bool expected) =>
        Assert.Equal(expected, P520FRunClock.IsAdmitted(environment, run, optIn));

    [Fact]
    public void Clock_only_enrolls_new_checkout_with_exact_header_notes_and_vendor()
    {
        var clock = Create(out var http);
        var order = Order();
        Assert.False(clock.Enroll(order, ["P520D-20261028-E2E01-V2"], "p520d-20261028-e2e01-c1@example.test"));
        http.HttpContext!.Request.Headers["X-CommerceHub-Acceptance-Run"] = Run;
        order.Notes = "foreign";
        Assert.False(clock.Enroll(order, ["P520D-20261028-E2E01-V2"], "p520d-20261028-e2e01-c1@example.test"));
        order.Notes = Run + " | synthetic clock from checkout";
        Assert.Throws<InvalidOperationException>(() => clock.Enroll(order, ["unrelated-vendor"], "p520d-20261028-e2e01-c1@example.test"));
        Assert.Throws<InvalidOperationException>(() => clock.Enroll(order, ["P520D-20261028-E2E01-V2"], "foreign@example.com"));
        Assert.True(clock.Enroll(order, ["P520D-20261028-E2E01-V2"], "p520d-20261028-e2e01-c1@example.test"));
        Assert.Throws<InvalidOperationException>(() => clock.Enroll(order, ["P520D-20261028-E2E01-V2"], "p520d-20261028-e2e01-c1@example.test"));
        Assert.True(clock.Owns(order.Id));
        Assert.False(clock.Owns(Guid.NewGuid()));
    }

    [Fact]
    public void Delay_elapses_monotonically_but_refund_and_payout_windows_do_not_overlap()
    {
        var clock = Create(out var http);http.HttpContext!.Request.Headers["X-CommerceHub-Acceptance-Run"] = Run;
        var order = Order();Assert.True(clock.Enroll(order, ["P520D-20261028-E2E01-V2"], "p520d-20261028-e2e01-c1@example.test"));
        var delivered = clock.ForOrder(order.Id);var available = delivered.AddDays(14);
        Assert.True(clock.ForSubOrder(order.SubOrders.Single().Id) < available);
        clock.Advance(Run, delivered, available.AddTicks(-10));
        Assert.True(clock.ForOrder(order.Id) < available);
        Assert.True(CustomerRefundRequestPolicy.IsWithinRefundWindow(delivered, new RefundSettings { RefundWindowDays = 7 }, clock.ForOrder(order.Id)));
        clock.Advance(Run, clock.VirtualUtc, available);
        Assert.Equal(available, clock.ForOrder(order.Id));
        Assert.True(CustomerRefundRequestPolicy.IsWithinRefundWindow(delivered, new RefundSettings { RefundWindowDays = 7 }, clock.ForOrder(order.Id)));
        clock.Advance(Run, clock.VirtualUtc, available.AddTicks(10));
        Assert.False(CustomerRefundRequestPolicy.IsWithinRefundWindow(delivered, new RefundSettings { RefundWindowDays = 7 }, clock.ForOrder(order.Id)));
        Assert.Throws<InvalidOperationException>(() => clock.Advance(Run, clock.VirtualUtc, delivered));
        Assert.Throws<InvalidOperationException>(() => clock.Advance("other", clock.VirtualUtc, available.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => clock.Advance(Run, delivered, available.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => clock.Advance(Run, clock.VirtualUtc, available.AddDays(31)));
    }
    private static CommerceHubMarketplaceOrder Order() => new() { Id = Guid.NewGuid(), Currency = "EGP", TotalAmount = 100,
        Notes = Run + " | synthetic clock from checkout", SubOrders = [new() { Id = Guid.NewGuid() }] };
    [Fact]
    public async Task Audit_failure_does_not_advance_and_process_restart_does_not_reenroll_history()
    {
        var clock = Create(out var http);http.HttpContext!.Request.Headers["X-CommerceHub-Acceptance-Run"] = Run;
        var order = Order();clock.Enroll(order, ["P520D-20261028-E2E01-V2"], "p520d-20261028-e2e01-c1@example.test");
        var before = clock.VirtualUtc;
        await Assert.ThrowsAsync<IOException>(() => clock.AdvanceAuditedAsync(Run, before, before.AddDays(7), _ => throw new IOException("audit unavailable"), default));
        Assert.Equal(before, clock.VirtualUtc);
        var restarted = Create(out _);
        Assert.False(restarted.Owns(order.Id));
        Assert.Equal("P520F_CLOCK_EXECUTION_SCOPE_REQUIRED", Assert.Throws<CommerceHubCheckoutException>(() => restarted.EnsureExecutionScope(order)).Code);
    }
    private static P520FRunClock Create(out HttpContextAccessor http)
    {
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();env.SetupGet(e => e.EnvironmentName).Returns("Development");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["CommerceHub:DevelopmentAcceptance:Clock:RunId"] = Run,
            ["CommerceHub:DevelopmentAcceptance:Clock:OptIn"] = "ALLOW-SYNTHETIC-TIME:" + Run }).Build();
        http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        return new P520FRunClock(env.Object, config, http);
    }
    [Fact]
    public void Configuration_reload_cannot_rebind_an_existing_clock_to_another_run()
    {
        var env = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();env.SetupGet(e => e.EnvironmentName).Returns("Development");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["CommerceHub:DevelopmentAcceptance:Clock:RunId"] = Run,
            ["CommerceHub:DevelopmentAcceptance:Clock:OptIn"] = "ALLOW-SYNTHETIC-TIME:" + Run }).Build();
        var clock = new P520FRunClock(env.Object, config, new HttpContextAccessor());Assert.True(clock.Enabled);
        config["CommerceHub:DevelopmentAcceptance:Clock:RunId"] = "P520F-20261028-FOREIGN01";
        config["CommerceHub:DevelopmentAcceptance:Clock:OptIn"] = "ALLOW-SYNTHETIC-TIME:P520F-20261028-FOREIGN01";
        Assert.Equal(Run, clock.RunId);Assert.False(clock.Enabled);
    }
}
