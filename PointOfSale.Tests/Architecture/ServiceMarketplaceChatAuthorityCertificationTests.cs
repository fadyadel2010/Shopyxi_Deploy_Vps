using System.Reflection;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Interfaces;

namespace PointOfSale.Tests.Architecture;

public sealed class ServiceMarketplaceChatAuthorityCertificationTests
{
    [Fact]
    public void ChatRuntime_MustDeriveConversationMembershipAndHistoryFromSqlRequestAuthority()
    {
        var chatService = ReadRepoFile("PointOfSale.Infrastructure/Services/ChatService.cs");
        var chatInterface = ReadRepoFile("PointOfSale.Core/Interfaces/IChatService.cs");
        var requestEntity = ReadRepoFile("PointOfSale.Core/Entities/ServiceRequest.cs");

        Assert.Contains("IgnoreQueryFilters()", chatService, StringComparison.Ordinal);
        Assert.Contains("request.TenantId != tenantId", chatService, StringComparison.Ordinal);
        Assert.Contains("request.BuyerUserId == senderUserId", chatService, StringComparison.Ordinal);
        Assert.Contains("request.ProviderUserId == senderUserId", chatService, StringComparison.Ordinal);
        Assert.Contains("request.AllowsInteraction()", chatService, StringComparison.Ordinal);
        Assert.Contains("request.CanViewChatHistory(userId)", chatService, StringComparison.Ordinal);
        Assert.Contains("Task<IEnumerable<ServiceRequestMessage>> GetMessagesAsync(Guid serviceRequestId, Guid userId, Guid tenantId", chatInterface, StringComparison.Ordinal);
        Assert.Contains("CanUserViewConversationAsync", chatInterface, StringComparison.Ordinal);

        var allowsInteractionBody = requestEntity[
            requestEntity.IndexOf("public bool AllowsInteraction()", StringComparison.Ordinal)..
            requestEntity.IndexOf("public bool CanViewChatHistory(Guid userId)", StringComparison.Ordinal)];
        Assert.Contains("Status == ServiceRequestStatus.Accepted", allowsInteractionBody, StringComparison.Ordinal);
        Assert.Contains("Status == ServiceRequestStatus.CompletedByProvider", allowsInteractionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletedByBuyer", allowsInteractionBody, StringComparison.Ordinal);
        Assert.Contains("public bool CanViewChatHistory(Guid userId)", requestEntity, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatRuntime_MustDeriveMessageIdentitySenderAndAttachmentsServerSide()
    {
        var chatService = ReadRepoFile("PointOfSale.Infrastructure/Services/ChatService.cs");
        var messageEntity = ReadRepoFile("PointOfSale.Core/Entities/ServiceRequestMessage.cs");
        var attachmentKey = ReadRepoFile("PointOfSale.Core/Security/MarketplaceChatAttachmentKey.cs");
        var controller = ReadRepoFile("PointOfSale.API/Controllers/MarketplaceController.cs");

        Assert.Contains("ServiceRequestMessage.CreateText", chatService, StringComparison.Ordinal);
        Assert.Contains("request.TenantId", chatService, StringComparison.Ordinal);
        Assert.Contains("senderRole", chatService, StringComparison.Ordinal);
        Assert.Contains("IsServerIssuedAttachmentAsync", chatService, StringComparison.Ordinal);

        Assert.Contains("Id = Guid.NewGuid()", messageEntity, StringComparison.Ordinal);
        Assert.Contains("TryNormalizeTenantScopedObjectKey", messageEntity, StringComparison.Ordinal);
        Assert.Contains("SenderRole = senderRole", messageEntity, StringComparison.Ordinal);
        Assert.Contains("MessageType = MessageType.Text", messageEntity, StringComparison.Ordinal);

        Assert.Contains("BuildStoragePath(Guid tenantId, Guid serviceRequestId, Guid userId)", attachmentKey, StringComparison.Ordinal);
        Assert.Contains("tenants/{tenantId:D}/{StorageSegment}/{serviceRequestId:D}/{userId:D}", attachmentKey, StringComparison.Ordinal);
        Assert.Contains("Uri.TryCreate(normalized, UriKind.Absolute", attachmentKey, StringComparison.Ordinal);
        Assert.Contains("CanUserViewConversationAsync", controller, StringComparison.Ordinal);
        Assert.Contains("CreateDownloadUrlAsync", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatRealtimeBoundary_MustNotBecomeAuthority()
    {
        var hub = ReadRepoFile("PointOfSale.API/Hubs/ChatHub.cs");
        var hubType = Assembly.Load("PointOfSale.API").GetType("PointOfSale.API.Hubs.ChatHub");

        Assert.NotNull(hubType);
        Assert.Empty(hubType!.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(field => field.FieldType.Name.Contains("DbContext", StringComparison.Ordinal)));

        Assert.Contains("CanUserParticipateAsync", hub, StringComparison.Ordinal);
        Assert.Contains("CanUserViewConversationAsync", hub, StringComparison.Ordinal);
        Assert.Contains("SendMessageAsync(serviceRequestId, userId, tenantId", hub, StringComparison.Ordinal);
        Assert.Contains("Clients.Group(groupName).SendAsync", hub, StringComparison.Ordinal);
        Assert.DoesNotContain("Context.Items", hub, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatPersistence_MustKeepMessageRowsTenantScopedAndImmutable()
    {
        var config = ReadRepoFile("PointOfSale.Infrastructure/Data/Configurations/ServiceRequestMessageConfiguration.cs");

        Assert.Contains("builder.ToTable(\"ServiceRequestMessages\")", config, StringComparison.Ordinal);
        Assert.Contains("builder.HasKey(m => m.Id)", config, StringComparison.Ordinal);
        Assert.Contains("HasForeignKey(m => m.ServiceRequestId)", config, StringComparison.Ordinal);
        Assert.Contains("builder.HasIndex(m => m.TenantId)", config, StringComparison.Ordinal);
        Assert.Contains("new { m.ServiceRequestId, m.CreatedAt }", config, StringComparison.Ordinal);

        foreach (var property in typeof(ServiceRequestMessage).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.DeclaringType == typeof(ServiceRequestMessage) && property.Name != nameof(ServiceRequestMessage.ServiceRequest))
            {
                Assert.Null(property.GetSetMethod(nonPublic: false));
            }
        }
    }

    [Fact]
    public async Task ServiceMarketplaceChatCertificationDocumentation_MustRecordS1CAuthorityFreeze()
    {
        // Original selected identity retained. Historical prose cannot certify
        // current behavior. Execute participant/tenant/lifecycle/cursor isolation
        // instead; send concurrency still requires separate PostgreSQL evidence.
        await MarketplaceChatReadAuthorityContract.AssertReadAuthorityAsync();
    }

    private static string ReadRepoFile(string relativePath)
    {
        var root = FindRepoRoot(AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PointOfSale.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
