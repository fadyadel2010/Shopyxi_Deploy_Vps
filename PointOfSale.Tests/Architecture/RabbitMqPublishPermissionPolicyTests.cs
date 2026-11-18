using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PointOfSale.Tests.Architecture;

/// <summary>
/// Regression guard for the production defect where the <c>shopixy-app</c>
/// RabbitMQ user was allowed to publish <c>PointOfSale.API.Events:Cart*</c>
/// events but not the other API-owned integration events. The MassTransit EF
/// outbox is ordered, so one denied exchange (CheckoutStartedEvent) blocked the
/// entire outbox and delayed every product projection.
///
/// The committed policy must admit every event exchange this application can
/// publish. The policy is intentionally kept as deployment configuration, not
/// as source code, because RabbitMQ permissions are broker state.
/// </summary>
public sealed class RabbitMqPublishPermissionPolicyTests
{
    private static readonly string[] PreviouslyDeniedApiEvents =
    {
        "PointOfSale.API.Events:CheckoutStartedEvent",
        "PointOfSale.API.Events:CheckoutFailedEvent",
        "PointOfSale.API.Events:CheckoutOrderCreatedEvent",
        "PointOfSale.API.Events:PriceRecalculationEvent",
        "PointOfSale.API.Events:InventoryReservedEvent",
        "PointOfSale.API.Events:InventoryReservationReleasedEvent",
        "PointOfSale.API.Events:ManualPaymentSubmittedEvent",
        "PointOfSale.API.Events:ManualPaymentConfirmedEvent",
        "PointOfSale.API.Events:ManualPaymentRejectedEvent",
        "PointOfSale.API.Events:PaymentStatusChangedEvent"
    };

    [Fact]
    public void PolicyAdmitsEveryPreviouslyDeniedApiEventExchange()
    {
        var policy = LoadPolicy();
        var regex = CompilePolicy(policy.Write);

        var denied = PreviouslyDeniedApiEvents
            .Where(exchange => !regex.IsMatch(exchange))
            .ToArray();

        Assert.True(
            denied.Length == 0,
            "The RabbitMQ publish policy still denies: " + string.Join(", ", denied));
    }

    [Fact]
    public void PolicyAdmitsEveryPublishableEventExchangeDiscoveredFromAssemblies()
    {
        var policy = LoadPolicy();
        var regex = CompilePolicy(policy.Write);

        var exchanges = EnumerateEventExchanges();
        Assert.NotEmpty(exchanges);

        var denied = exchanges
            .Where(exchange => !regex.IsMatch(exchange))
            .OrderBy(exchange => exchange, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            denied.Length == 0,
            "The RabbitMQ publish policy denies event exchanges: " + string.Join(", ", denied));
    }

    [Fact]
    public void PolicyConfigureWriteAndReadAllAdmitTheSameEventSurface()
    {
        var policy = LoadPolicy();
        var exchanges = EnumerateEventExchanges();

        foreach (var (permission, pattern) in new[]
                 {
                     ("configure", policy.Configure),
                     ("write", policy.Write),
                     ("read", policy.Read)
                 })
        {
            var regex = CompilePolicy(pattern);
            var denied = exchanges.Where(exchange => !regex.IsMatch(exchange)).ToArray();
            Assert.True(
                denied.Length == 0,
                $"The RabbitMQ {permission} policy denies: {string.Join(", ", denied)}");
        }
    }

    [Fact]
    public void PolicyDoesNotBroadenToArbitraryExchanges()
    {
        var policy = LoadPolicy();
        var regex = CompilePolicy(policy.Write);

        Assert.False(regex.IsMatch("evil-exchange"));
        Assert.False(regex.IsMatch("PointOfSale.API.Events:notAnEvent"));
        Assert.False(regex.IsMatch("PointOfSale.API.Events:CheckoutStartedEventSuffix"));
        Assert.False(regex.IsMatch("PointOfSale.Core.Events:notAnEvent"));
    }

    private static RabbitMqPermissionPolicy LoadPolicy()
    {
        var path = FindRepositoryFile(
            Path.Combine("deploy", "rabbitmq", "shopixy-app-permissions.json"));
        var json = File.ReadAllText(path);
        var policy = JsonSerializer.Deserialize<RabbitMqPermissionPolicy>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(policy);
        Assert.Equal("shopixy-app", policy!.User);
        Assert.Equal("shopixy", policy.Vhost);
        Assert.False(string.IsNullOrWhiteSpace(policy.Configure));
        Assert.False(string.IsNullOrWhiteSpace(policy.Write));
        Assert.False(string.IsNullOrWhiteSpace(policy.Read));
        return policy;
    }

    private static Regex CompilePolicy(string erlangPattern)
    {
        // RabbitMQ uses Erlang regular expressions, whose named-capture syntax
        // is (?P<name>...). .NET uses (?<name>...). The rest of this policy is
        // deliberately restricted to constructs both engines support.
        var dotnetPattern = erlangPattern.Replace(
            "(?P<fault>",
            "(?<fault>",
            StringComparison.Ordinal);
        return new Regex(
            dotnetPattern,
            RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));
    }

    private static IReadOnlyList<string> EnumerateEventExchanges()
    {
        var assemblies = new[]
        {
            typeof(PointOfSale.API.Events.CheckoutStartedEvent).Assembly,
            typeof(PointOfSale.Core.Events.IntegrationEvent).Assembly
        };

        return assemblies
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.IsClass)
            .Where(type => !type.IsGenericTypeDefinition)
            .Where(type => IsEventNamespace(type.Namespace))
            .Where(type => IsEventTypeName(type.Name))
            .Select(type => $"{type.Namespace}:{type.Name}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(exchange => exchange, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsEventNamespace(string? ns) =>
        ns != null &&
        (ns.Equals("PointOfSale.API.Events", StringComparison.Ordinal) ||
         ns.StartsWith("PointOfSale.API.Events.", StringComparison.Ordinal) ||
         ns.Equals("PointOfSale.Core.Events", StringComparison.Ordinal) ||
         ns.StartsWith("PointOfSale.Core.Events.", StringComparison.Ordinal));

    private static bool IsEventTypeName(string name) =>
        name.EndsWith("Event", StringComparison.Ordinal) ||
        name.EndsWith("EventBase", StringComparison.Ordinal) ||
        name.EndsWith("DomainEvent", StringComparison.Ordinal);

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }

    private sealed record RabbitMqPermissionPolicy(
        string User,
        string Vhost,
        string Configure,
        string Write,
        string Read);
}
