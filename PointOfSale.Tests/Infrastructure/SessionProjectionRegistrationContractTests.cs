using System.Reflection;
using MassTransit;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Moq;
using PointOfSale.Infrastructure.Consumers;

namespace PointOfSale.Tests.Infrastructure;

// Registration-only configuration. No bus, broker, worker, MongoDB or SQL starts.
public sealed class SessionProjectionRegistrationContractTests
{
    [Fact]
    public void ModernConsumerDefinitionInstallsOneRetrySpecificationWithoutExternalInfrastructure()
    {
        var definition = new OnlineCustomerSessionProjectionConsumerDefinition();
        var method = typeof(OnlineCustomerSessionProjectionConsumerDefinition).GetMethod("ConfigureConsumer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.NotNull(method);
        Assert.Equal(3, method.GetParameters().Length);
        Assert.Equal(typeof(IRegistrationContext), method.GetParameters()[2].ParameterType);
        Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>());
        var endpoint = new Mock<IReceiveEndpointConfigurator>();
        var consumer = new Mock<IConsumerConfigurator<OnlineCustomerSessionProjectionConsumer>>(MockBehavior.Strict);
        var context = new Mock<IRegistrationContext>(MockBehavior.Strict);
        method.Invoke(definition, [endpoint.Object, consumer.Object, context.Object]);
        // Method identity retained: at definition configuration MassTransit
        // installs retry CONFIGURATION OBSERVERS, not a concrete message pipe.
        // The actual per-message retry pipeline is built later by the broker
        // consumer configuration lifecycle, which this no-bus test does not run.
        var registration = Assert.Single(endpoint.Invocations, i => i.Method.Name == "ConnectConsumerConfigurationObserver");
        var observer = Assert.Single(registration.Arguments);
        Assert.NotNull(observer);
        Assert.Contains("MessageRetryConfigurationObserver", observer.GetType().Name, StringComparison.Ordinal);
        Assert.Equal(4, endpoint.Invocations.Count);
        Assert.Equal(new[] { "ConnectActivityConfigurationObserver", "ConnectConsumerConfigurationObserver",
            "ConnectHandlerConfigurationObserver", "ConnectSagaConfigurationObserver" },
            endpoint.Invocations.Select(i => i.Method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.All(endpoint.Invocations, invocation =>
            Assert.Same(observer, Assert.Single(invocation.Arguments)));
        consumer.VerifyNoOtherCalls(); context.VerifyNoOtherCalls();
    }

    [Fact]
    public void RetryIntervalsAndEndpointIdentityRemainExactlyTheExistingContract()
    {
        var source = PointOfSale.Tests.Architecture.CertificationSource.ReadRepoFile(
            "PointOfSale.Infrastructure/Consumers/OnlineCustomerSessionProjectionConsumer.cs");
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == nameof(OnlineCustomerSessionProjectionConsumerDefinition));
        var method = Assert.Single(declaration.Members.OfType<MethodDeclarationSyntax>());
        var seconds = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression.ToString() == "TimeSpan.FromSeconds")
            .Select(call => Assert.IsType<int>(Assert.IsType<LiteralExpressionSyntax>(Assert.Single(call.ArgumentList.Arguments).Expression).Token.Value))
            .ToArray();
        Assert.Equal(new[] { 1, 5, 15 }, seconds);
        Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "UseMessageRetry");
        Assert.Contains("online-customer-session-projection", declaration.ToString(), StringComparison.Ordinal);
    }
}
