using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services.CommerceHub;

namespace PointOfSale.Tests.CommerceHub;

public class P520GDevelopmentRefundResponseTests
{
    [Theory]
    [InlineData(409, "FINANCIAL_RECONCILIATION_UNHEALTHY")]
    [InlineData(403, "REFUND_FORBIDDEN")]
    public async Task Domain_safety_rejections_retain_status_and_code(int status, string code)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");
        var execution = new Mock<IRefundExecutionService>(MockBehavior.Strict);
        var id = Guid.NewGuid();
        execution.Setup(e => e.ExecuteDevelopmentOnlineBeforePayoutAsync(id, "run-key", "synthetic only",
                null, null, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CommerceHubCheckoutException(code, "Safety guard rejected execution.", status));
        var controller = new DevelopmentCommerceHubController(environment.Object, null!, null!, execution.Object,
            null!, null!, null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = Assert.IsType<ObjectResult>(await controller.ExecuteM1dOnlineBeforePayout(id,
            new DevelopmentOnlineRefundExecutionRequest
            { AdministratorConfirmation = true, IdempotencyKey = "run-key", Reason = "synthetic only" }, CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(code, result.Value!.GetType().GetProperty("Code")!.GetValue(result.Value));
        execution.VerifyAll();
    }
}
