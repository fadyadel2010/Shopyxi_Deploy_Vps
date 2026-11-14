using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services;
using PointOfSale.API.Services.CommerceHub;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Tests.Controllers;

public sealed class ProductMediaUploadContractTests
{
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 0, 0, 0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0];
    private static readonly byte[] Webp = [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 0];

    [Fact]
    public void ProductMediaContent_AllowsAnonymousStorefrontReads()
    {
        var action = typeof(UploadController).GetMethod(nameof(UploadController.GetProductMediaContent));

        Assert.NotNull(action);
        Assert.NotNull(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).SingleOrDefault());
    }

    [Theory]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.webp", "image/webp")]
    public async Task Upload_WithoutPublicStorageUrl_ReturnsPersistableProductMediaUrl(
        string filename, string mime)
    {
        await using var db = CreateDb();
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var storedName = $"{Guid.NewGuid():N}{Path.GetExtension(filename)}";
        var storage = new Mock<IR2StorageService>();
        storage.Setup(service => service.UploadFileAsync(
                It.IsAny<Stream>(), mime, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new R2UploadResult(null, storedName, "unused"));
        var controller = CreateController(db, tenant, storage);

        var result = await controller.Upload(new UploadController.UploadRequest
        {
            File = FormFile(filename, mime, filename.EndsWith("jpg") ? Jpeg : filename.EndsWith("png") ? Png : Webp),
            EntityType = "product", EntityId = product.ToString("D")
        }, CancellationToken.None);

        var response = Assert.IsType<UploadController.UploadResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal($"/api/upload/product-media/{product:D}/{storedName}", response.Url);
        Assert.Equal(mime, response.ContentType);
        storage.Verify(service => service.UploadFileAsync(
            It.IsAny<Stream>(), mime, $"tenants/{tenant:D}/products/{product:D}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("photo.gif", "image/gif")]
    [InlineData("photo.jpg", "image/png")]
    [InlineData("photo.png", "image/png")]
    public async Task Upload_RejectsInvalidExtensionMimeOrSignature(string filename, string mime)
    {
        await using var db = CreateDb();
        var storage = new Mock<IR2StorageService>();
        var result = await CreateController(db, Guid.NewGuid(), storage).Upload(new UploadController.UploadRequest
        {
            File = FormFile(filename, mime, Jpeg), EntityType = "product", EntityId = Guid.NewGuid().ToString()
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        storage.Verify(service => service.UploadFileAsync(
            It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Content_ReadsPersistedBytesOnlyForOwnedRegisteredMedia()
    {
        var tenant = Guid.NewGuid();
        await using var db = CreateDb(tenant);
        var productId = Guid.NewGuid();
        var filename = $"{Guid.NewGuid():N}.jpg";
        var url = $"/api/upload/product-media/{productId:D}/{filename}";
        var storage = new Mock<IR2StorageService>();
        storage.Setup(service => service.DownloadObjectAsync(
                $"tenants/{tenant:D}/products/{productId:D}/{filename}", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new R2DownloadObjectResult(new MemoryStream(Jpeg), "image/jpeg", Jpeg.Length, null));
        var controller = CreateController(db, tenant, storage);
        Assert.IsType<NotFoundResult>(await controller.GetProductMediaContent(productId, filename, CancellationToken.None));

        db.Products.Add(new Product { Id = productId, TenantId = tenant, Name = "Laptop", CategoryId = Guid.NewGuid() });
        db.ProductMedia.Add(new ProductMedia
        {
            Id = Guid.NewGuid(), TenantId = tenant, ProductId = productId, Url = url,
            MediaType = "image", CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var file = Assert.IsType<FileStreamResult>(await controller.GetProductMediaContent(productId, filename, CancellationToken.None));
        Assert.Equal("image/jpeg", file.ContentType);
        using var copy = new MemoryStream();
        await file.FileStream.CopyToAsync(copy);
        Assert.Equal(Jpeg, copy.ToArray());
        Assert.IsType<NotFoundResult>(await CreateController(db, Guid.NewGuid(), storage)
            .GetProductMediaContent(productId, filename, CancellationToken.None));
    }

    [Theory]
    [InlineData("live", true)]
    [InlineData("draft", false)]
    [InlineData("inactive-variant", false)]
    [InlineData("inactive-tenant", false)]
    [InlineData("inactive-category", false)]
    [InlineData("suppressed", false)]
    [InlineData("unlisted", false)]
    public async Task AnonymousMedia_RequiresCurrentPublicMarketplaceLineage(string state, bool allowed)
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var fileName = $"{Guid.NewGuid():N}.jpg";
        var listing = CommerceHubListing.Create(tenantId, productId, variantId, categoryId, null);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Media vendor", Currency = "EGP",
            IsActive = state != "inactive-tenant", Status = TenantStatus.Active });
        db.Products.Add(new Product { Id = productId, TenantId = tenantId, Name = "Public product",
            IsActive = true, IsOnlineVisible = true,
            PublishStatus = state == "draft" ? PublishStatus.Draft : PublishStatus.Published });
        db.ProductVariants.Add(new ProductVariant { Id = variantId, TenantId = tenantId,
            ProductId = productId, IsActive = state != "inactive-variant" });
        db.CommerceHubCategories.Add(new CommerceHubCategory { Id = categoryId, Name = "Public category",
            Slug = "media-category", IsActive = state != "inactive-category" });
        if (state != "unlisted") db.CommerceHubListings.Add(listing);
        db.ProductMedia.Add(new ProductMedia { Id = Guid.NewGuid(), TenantId = tenantId,
            ProductId = productId, Url = $"/api/upload/product-media/{productId:D}/{fileName}" });
        await db.SaveChangesAsync();

        var visibility = new Mock<ICommerceHubPublicVendorVisibilityService>();
        visibility.Setup(service => service.FilterVisibleListingIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Guid> ids, CancellationToken _) => state == "suppressed" ? [] : ids.ToHashSet());
        var storage = new Mock<IR2StorageService>();
        storage.Setup(service => service.DownloadObjectAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new R2DownloadObjectResult(new MemoryStream(Jpeg), "image/jpeg", Jpeg.Length, null));
        var controller = CreateController(db, null, storage, visibility.Object);
        var result = await controller.GetProductMediaContent(productId, fileName, CancellationToken.None);
        if (allowed) Assert.IsType<FileStreamResult>(result);
        else Assert.IsType<NotFoundResult>(result);
        storage.Verify(service => service.DownloadObjectAsync(
            $"tenants/{tenantId:D}/products/{productId:D}/{fileName}", null, It.IsAny<CancellationToken>()),
            allowed ? Times.Once() : Times.Never());
    }

    private static UploadController CreateController(ApplicationDbContext db, Guid? tenantId, Mock<IR2StorageService> storage,
        ICommerceHubPublicVendorVisibilityService? visibility = null)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(context => context.TenantId).Returns(tenantId);
        tenant.SetupGet(context => context.RequiredTenantId).Returns(tenantId ?? Guid.Empty);
        return new UploadController(storage.Object, new FileValidationService(), tenant.Object,
            NullLogger<UploadController>.Instance, db, visibility ?? Mock.Of<ICommerceHubPublicVendorVisibilityService>());
    }

    private static IFormFile FormFile(string name, string mime, byte[] bytes) => new FormFile(
        new MemoryStream(bytes), 0, bytes.Length, "file", name)
    {
        Headers = new HeaderDictionary(), ContentType = mime
    };

    private static ApplicationDbContext CreateDb(Guid? tenantId = null)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(context => context.TenantId).Returns(tenantId);
        tenant.SetupGet(context => context.RequiredTenantId).Returns(tenantId ?? Guid.Empty);
        return new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            tenant.Object);
    }
}
