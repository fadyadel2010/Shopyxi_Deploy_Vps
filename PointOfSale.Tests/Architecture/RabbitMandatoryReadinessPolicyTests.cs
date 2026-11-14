namespace PointOfSale.Tests.Architecture;

public sealed class RabbitMandatoryReadinessPolicyTests
{
    [Fact]
    public void ReadinessRequiresRabbitAndInitializedBusWithoutChangingOptionalStatusPolicy()
    {
        var source = CertificationSource.ReadRepoFile("PointOfSale.API/Program.cs");
        var ready = source[source.IndexOf("app.MapHealthChecks(\"/health/ready\"", StringComparison.Ordinal)..];
        ready = ready[..ready.IndexOf("}).AllowAnonymous();", StringComparison.Ordinal)];
        Assert.Contains("TryGetValue(\"rabbitmq\"", ready);
        Assert.Contains("TryGetValue(\"masstransit-bus\"", ready);
        Assert.Contains("if (!rabbitReady || !busReady)", ready);
        Assert.Contains("StatusCodes.Status503ServiceUnavailable", ready);
        Assert.DoesNotContain("ResultStatusCodes", ready);
        var live = source[source.IndexOf("app.MapHealthChecks(\"/health/live\"", StringComparison.Ordinal)..];
        live = live[..live.IndexOf("}).AllowAnonymous();", StringComparison.Ordinal)];
        Assert.Contains("Predicate = _ => false", live);
    }
}
