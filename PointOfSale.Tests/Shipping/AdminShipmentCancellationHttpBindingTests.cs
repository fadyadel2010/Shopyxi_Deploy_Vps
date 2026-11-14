using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Admin;
using PointOfSale.API.Services.Admin;
using PointOfSale.API.Infrastructure.Mvc;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.MultiTenancy;

namespace PointOfSale.Tests.Shipping;

public class AdminShipmentCancellationHttpBindingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ReasonOnlyBody_BindsThroughRealMvcPipeline(bool forgedActor, bool invalidShipment, bool invalidOperation)
    {
        var tenantId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var shipment = Guid.NewGuid();
        var service = new Mock<IAdminShipmentService>();
        service.Setup(x => x.CancelShipmentAsync(tenantId, shipment,
                It.Is<AdminCancelShipmentRequest>(r => r.Reason == "Synthetic QA cancellation" && r.ActorUserId == actor),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminShipmentCancellationResponse { Outcome = "Confirmed", ShipmentId = shipment });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(service.Object);
        builder.Services.AddScoped<ITenantContext>(_ => { var t = new TenantContext(); t.SetTenantId(tenantId); return t; });
        builder.Services.AddControllers(options => options.Filters.Add(new ApiRequestNormalizationFilter()))
            .AddApplicationPart(typeof(AdminShipmentsController).Assembly);
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, actor.ToString()) }, "test")); await next(); });
        app.MapControllers();
        await app.StartAsync();
        var response = await app.GetTestClient().PostAsJsonAsync($"/api/admin/shipments/{(invalidShipment ? Guid.Empty : shipment)}/cancel",
            new { reason = "Synthetic QA cancellation", actorUserId = forgedActor ? Guid.NewGuid() : Guid.Empty,
                operationId = invalidOperation ? (Guid?)Guid.Empty : null });
        if (invalidShipment || invalidOperation)
        {
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(invalidShipment ? "shipmentId" : "request.OperationId", await response.Content.ReadAsStringAsync());
            service.Verify(x => x.CancelShipmentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<AdminCancelShipmentRequest>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            service.VerifyAll();
        }
    }
}
