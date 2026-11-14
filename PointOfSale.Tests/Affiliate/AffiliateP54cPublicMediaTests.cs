using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Entities.Affiliate;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.MongoDB.Configuration;
using PointOfSale.Infrastructure.MongoDB.Services;

namespace PointOfSale.Tests.Affiliate;

public sealed class AffiliateP54cPublicMediaTests
{
    private const string FileName = "2741f85507354b4b8d379e281d4d03a8.png";

    [Fact]
    public void Route_IsAnonymous_NoStore_AndAcceptsNoTenantOrObjectPath()
    {
        var action = typeof(AffiliatePortalController).GetMethod(nameof(AffiliatePortalController.GetPublicProductMedia))!;
        Assert.NotNull(action.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.True(action.GetCustomAttribute<ResponseCacheAttribute>()!.NoStore);
        Assert.Equal("discover/public-products/{productId:guid}/media/{fileName}", action.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal(new[] { "productId", "fileName", "storage", "ct" }, action.GetParameters().Select(p => p.Name));
        Assert.Null(typeof(UploadController).GetMethod(nameof(UploadController.Upload))!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task AnonymousRead_UsesSqlOwner_NotCallerTenant_AndRegisteredBytes()
    {
        await using var fixture = await Fixture.Create();
        var result = Assert.IsType<FileStreamResult>(await fixture.Read());
        using var content = new MemoryStream();
        await result.FileStream.CopyToAsync(content);
        Assert.Equal(new byte[] { 1, 2, 3 }, content.ToArray());
        Assert.Equal("image/png", result.ContentType);
        fixture.Storage.Verify(s => s.DownloadObjectAsync(
            $"tenants/{fixture.Product.TenantId:D}/products/{fixture.Product.Id:D}/{FileName}", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("deleted")]
    [InlineData("hidden")]
    [InlineData("unsearchable")]
    [InlineData("inactive")]
    [InlineData("policy-disabled")]
    [InlineData("category-disabled")]
    [InlineData("store-suspended")]
    [InlineData("no-price")]
    [InlineData("out-of-stock")]
    [InlineData("qa-store")]
    public async Task IneligibleProduct_NeverDownloads(string reason)
    {
        await using var fixture = await Fixture.Create(reason);
        Assert.IsType<NotFoundResult>(await fixture.Read());
        fixture.VerifyNoDownload();
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("%2e%2e%2fsecret.png")]
    [InlineData("tenants/other/file.png")]
    [InlineData("photo.png")]
    [InlineData("2741f85507354b4b8d379e281d4d03a8.svg")]
    [InlineData("2741f85507354b4b8d379e281d4d03a8.mp4")]
    [InlineData("2741f85507354b4b8d379e281d4d03a8.png\n")]
    public async Task InvalidIdentifier_NeverDownloads(string fileName)
    {
        await using var fixture = await Fixture.Create();
        Assert.IsType<NotFoundResult>(await fixture.Read(fileName));
        fixture.VerifyNoDownload();
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-product")]
    [InlineData("video")]
    public async Task UnregisteredOrCrossStoreMedia_NeverDownloads(string reason)
    {
        await using var fixture = await Fixture.Create();
        var media = await fixture.Db.ProductMedia.IgnoreQueryFilters().SingleAsync();
        if (reason is "wrong-tenant" or "wrong-product")
        {
            fixture.Db.ProductMedia.Remove(media);
            await fixture.Db.SaveChangesAsync();
            media = new ProductMedia { Id = Guid.NewGuid(), TenantId = media.TenantId,
                ProductId = media.ProductId, Url = media.Url, MediaType = "image" };
            if (reason == "wrong-tenant") media.TenantId = Guid.NewGuid();
            if (reason == "wrong-product") media.ProductId = Guid.NewGuid();
            fixture.Db.ProductMedia.Add(media);
        }
        if (reason == "removed") fixture.Db.ProductMedia.Remove(media);
        if (reason == "video") media.MediaType = "video";
        await fixture.Db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await fixture.Read());
        fixture.VerifyNoDownload();
    }

    [Fact]
    public async Task MissingStoredObject_Returns404()
    {
        await using var fixture = await Fixture.Create();
        fixture.Storage.Setup(s => s.DownloadObjectAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());
        Assert.IsType<NotFoundResult>(await fixture.Read());
    }

    [Fact]
    public async Task NewlyRegisteredAndExistingImage_UseSamePublicMemberContractAndStorageOwnership()
    {
        await using var fixture = await Fixture.Create();
        const string fresh = "0123456789abcdef0123456789abcdef.webp";
        fixture.Db.ProductMedia.Add(new ProductMedia { Id = Guid.NewGuid(), ProductId = fixture.Product.Id,
            TenantId = fixture.Product.TenantId, MediaType = "image", Url = $"/api/upload/product-media/{fixture.Product.Id:D}/{fresh}" });
        await fixture.Db.SaveChangesAsync();
        var detail = Assert.IsType<AffiliateProductDetailResponse>(Assert.IsType<OkObjectResult>(
            (await fixture.Controller.GetProductDetail(fixture.Product.Id, default)).Result).Value);
        Assert.Equal(2, detail.Images.Count);
        foreach (var file in new[] { FileName, fresh })
        {
            Assert.Contains($"/api/affiliate/discover/public-products/{fixture.Product.Id:D}/media/{file}", detail.Images);
            var read = Assert.IsType<FileStreamResult>(await fixture.Read(file));
            await read.FileStream.DisposeAsync();
            fixture.Storage.Verify(s => s.DownloadObjectAsync($"tenants/{fixture.Product.TenantId:D}/products/{fixture.Product.Id:D}/{file}", null, It.IsAny<CancellationToken>()), Times.Once);
        }
        fixture.Storage.Setup(s => s.DownloadObjectAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>())).ThrowsAsync(new FileNotFoundException());
        Assert.IsType<NotFoundResult>(await fixture.Read(fresh));
    }

    [Fact]
    public async Task MissingR2Object_Returns404_ButStorageOutagesAreNotHidden()
    {
        await using var fixture = await Fixture.Create();
        fixture.Storage.Setup(s => s.DownloadObjectAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.S3.AmazonS3Exception("missing") { StatusCode = System.Net.HttpStatusCode.NotFound });
        Assert.IsType<NotFoundResult>(await fixture.Read());
        fixture.Storage.Setup(s => s.DownloadObjectAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.S3.AmazonS3Exception("outage") { StatusCode = System.Net.HttpStatusCode.ServiceUnavailable });
        await Assert.ThrowsAsync<Amazon.S3.AmazonS3Exception>(() => fixture.Read());
    }

    [Fact]
    public void UrlMapping_OnlyMapsCanonicalOwnedPaths_AndPreservesPublicCdn()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"/api/affiliate/discover/public-products/{id:D}/media/{FileName}",
            AffiliatePublicProductMedia.ResolveUrl(id, $"/api/upload/product-media/{id:D}/{FileName}"));
        Assert.Equal("https://cdn.example.test/a.png", AffiliatePublicProductMedia.ResolveUrl(id, "https://cdn.example.test/a.png"));
        foreach (var image in new[] { $"/api/upload/product-media/{Guid.NewGuid():D}/{FileName}", "/uploads/arbitrary.png", "//other/path", "javascript:alert(1)", "data:image/png;base64,abc" })
            Assert.Empty(AffiliatePublicProductMedia.ResolveUrl(id, image));
    }

    [Fact]
    public async Task PrimaryReadOnly_PublicDtoIssuesProductScopedMediaUrls()
    {
        await using var db = await AffiliateP53ePrimarySupport.Open("P54C-public-media-readonly");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var config = AffiliateP53ePrimarySupport.Config;
        var mongo = new MongoDbContext(Options.Create(config.GetSection("MongoDB").Get<MongoDbSettings>()!), NullLogger<MongoDbContext>.Instance);
        var resolver = new StorefrontUrlResolver(config, NullLogger<StorefrontUrlResolver>.Instance, Mock.Of<IServiceScopeFactory>());
        var controller = new AffiliatePortalController(db, mongo, null!, null!, null!, resolver, NullLogger<AffiliatePortalController>.Instance);
        var page = Assert.IsType<AffiliatePublicProductDiscoveryResponse>(Assert.IsType<OkObjectResult>(
            (await controller.DiscoverPublicProducts(categorySlug: "electronics")).Result).Value);
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(3, page.Items.Sum(p => p.Images.Count));
        foreach (var product in page.Items)
        {
            Assert.All(product.Images, image => Assert.StartsWith($"/api/affiliate/discover/public-products/{product.ProductId:D}/media/", image));
            var detail = Assert.IsType<AffiliatePublicProductItem>(Assert.IsType<OkObjectResult>(
                (await controller.DiscoverPublicProductDetail(product.ProductId, default)).Result).Value);
            Assert.Equal(product.Images, detail.Images);
        }
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ApplicationDbContext Db { get; init; }
        public required Product Product { get; init; }
        public required AffiliatePortalController Controller { get; init; }
        public Mock<IR2StorageService> Storage { get; } = new();

        public Task<IActionResult> Read(string fileName = FileName) => Controller.GetPublicProductMedia(Product.Id, fileName, Storage.Object, default);
        public void VerifyNoDownload() => Storage.Verify(s => s.DownloadObjectAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static async Task<Fixture> Create(string? reason = null)
        {
            // Unbound context mirrors the public middleware path; no tenant authority is supplied.
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, Mock.Of<ITenantContext>());
            var tenant = new Tenant { Id = reason == "qa-store" ? Guid.Parse("51510000-2026-4022-a000-000000000001") : Guid.NewGuid(),
                Name = "Public store", Slug = "public-store", Status = reason == "store-suspended" ? TenantStatus.Suspended : TenantStatus.Active };
            var category = AffiliateCategory.CreateMainCategory("Electronics", "electronics");
            category.Id = Guid.NewGuid();
            if (reason == "category-disabled") category.Deactivate();
            var product = new Product { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Public image", CategoryId = Guid.Empty,
                PublishStatus = reason == "draft" ? PublishStatus.Draft : PublishStatus.Published,
                IsActive = reason != "inactive", IsDeleted = reason == "deleted", IsOnlineVisible = reason != "hidden",
                IsSearchable = reason != "unsearchable", TrackStock = reason == "out-of-stock" };
            var unit = new PointOfSale.Core.Entities.Unit { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Piece" };
            var policy = AffiliateProductPolicy.Create(tenant.Id, product.Id, 0.1m, categoryId: category.Id);
            if (reason == "policy-disabled") policy.Disable();
            db.AddRange(tenant, category, product, unit,
                new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = unit.Id, IsDefault = true, IsBaseUnit = true, SellingPrice = reason == "no-price" ? 0 : 25, ConversionFactor = 1 },
                policy,
                new ProductMedia { Id = Guid.NewGuid(), TenantId = tenant.Id, ProductId = product.Id, MediaType = "image", Url = $"/api/upload/product-media/{product.Id:D}/{FileName}" });
            await db.SaveChangesAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storefront:UrlTemplate"] = "http://{slug}.localhost:5174" }).Build();
            var resolver = new StorefrontUrlResolver(config, NullLogger<StorefrontUrlResolver>.Instance, Mock.Of<IServiceScopeFactory>());
            var controller = new AffiliatePortalController(db, null!, null!, null!, Mock.Of<ITenantContext>(), resolver, NullLogger<AffiliatePortalController>.Instance)
                { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var fixture = new Fixture { Db = db, Product = product, Controller = controller };
            fixture.Storage.Setup(s => s.DownloadObjectAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new R2DownloadObjectResult(new MemoryStream([1, 2, 3]), "image/png", 3, null));
            return fixture;
        }
    }
}
