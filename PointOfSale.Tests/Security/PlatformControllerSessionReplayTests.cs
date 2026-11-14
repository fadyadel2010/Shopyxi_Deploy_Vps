using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.Configuration;
using PointOfSale.API.Controllers;
using PointOfSale.API.DTOs.Platform;
using PointOfSale.API.Models;
using PointOfSale.API.Services.Platform;

namespace PointOfSale.Tests.Security;

public sealed class PlatformControllerSessionReplayTests
{
    [Fact]
    public async Task Register_RedisCachedResponseIsNotRegistrationAuthority()
    {
        var registration = RegistrationFailure(
            "SESSION_RESTORATION_FORBIDDEN",
            "Registration session is no longer current. Sign in again.");
        var idempotency = AdvisoryIdempotency(lockAcquired: true, exposeCachedResult: true);
        var controller = CreateController(registration.Object, idempotency.Object);

        var result = await controller.Register(Request());

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var error = Assert.IsType<RegistrationErrorResponse>(conflict.Value);
        Assert.Equal("SESSION_RESTORATION_FORBIDDEN", error.Error);
        Assert.Empty(controller.HttpContext.Response.Headers.SetCookie.ToString());
        registration.Verify(service => service.RegisterTenantAsync(
            It.IsAny<TenantRegistrationRequest>(),
            "req-platform-replay",
            It.IsAny<CancellationToken>()), Times.Once);
        idempotency.Verify(service => service.CheckRequestAsync(It.IsAny<string>()), Times.Never);
        idempotency.Verify(service => service.ReleaseLockAsync("req-platform-replay"), Times.Once);
    }

    [Fact]
    public async Task Register_RedisLockContentionStillConsultsDurableSqlAuthority()
    {
        var registration = RegistrationFailure(
            "IDEMPOTENCY_KEY_REUSED",
            "X-Request-Id was already used for a different registration request.");
        var idempotency = AdvisoryIdempotency(lockAcquired: false, exposeCachedResult: false);
        var controller = CreateController(registration.Object, idempotency.Object);

        var result = await controller.Register(Request());

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var error = Assert.IsType<RegistrationErrorResponse>(conflict.Value);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Error);
        registration.Verify(service => service.RegisterTenantAsync(
            It.IsAny<TenantRegistrationRequest>(),
            "req-platform-replay",
            It.IsAny<CancellationToken>()), Times.Once);
        idempotency.Verify(service => service.ReleaseLockAsync(It.IsAny<string>()), Times.Never);
    }

    private static PlatformController CreateController(
        ITenantRegistrationService registrationService,
        IIdempotencyService idempotencyService)
    {
        var abuse = new Mock<IAbuseDetectionService>();
        abuse
            .Setup(service => service.CheckForAbuseAsync(It.IsAny<string>()))
            .ReturnsAsync(new AbuseCheckResult());
        abuse
            .Setup(service => service.TrackRegistrationAttemptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var controller = new PlatformController(
            registrationService,
            AllowedRateLimit(),
            idempotencyService,
            abuse.Object,
            Mock.Of<IPlatformAuditLogger>(),
            Options.Create(new PlatformSecuritySettings
            {
                RegistrationEnabled = true,
                RegistrationDisabledMessage = "disabled",
                MaxRegistrationsPerIpPerHour = 5,
                MaxSubdomainChecksPerIpPerMinute = 30,
                MaxEmailChecksPerIpPerMinute = 20,
                RateLimitEnabled = true,
                IdempotencyEnabled = true
            }),
            Options.Create(new JwtSettings
            {
                Secret = "platform-session-replay-test-secret-key-1234567890",
                Issuer = "PointOfSale.Tests",
                Audience = "PointOfSale.Tests",
                AccessTokenExpirationMinutes = 15,
                RefreshTokenExpirationDays = 7
            }),
            NullLogger<PlatformController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        controller.HttpContext.Request.Headers["X-Request-Id"] = "req-platform-replay";
        controller.HttpContext.Response.Body = new MemoryStream();
        return controller;
    }

    private static Mock<ITenantRegistrationService> RegistrationFailure(
        string code,
        string message)
    {
        var registration = new Mock<ITenantRegistrationService>();
        registration
            .Setup(service => service.RegisterTenantAsync(
                It.IsAny<TenantRegistrationRequest>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantRegistrationResult
            {
                Success = false,
                Error = new RegistrationErrorResponse
                {
                    Error = code,
                    Message = message
                }
            });
        return registration;
    }

    private static IRateLimitService AllowedRateLimit()
    {
        var rateLimit = new Mock<IRateLimitService>();
        rateLimit
            .Setup(service => service.CheckRateLimitAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .ReturnsAsync(new RateLimitResult
            {
                Allowed = true,
                CurrentCount = 0,
                Limit = 5
            });
        return rateLimit.Object;
    }

    private static Mock<IIdempotencyService> AdvisoryIdempotency(
        bool lockAcquired,
        bool exposeCachedResult)
    {
        var idempotency = new Mock<IIdempotencyService>();
        idempotency
            .Setup(service => service.TryAcquireLockAsync(It.IsAny<string>()))
            .ReturnsAsync(lockAcquired);
        idempotency
            .Setup(service => service.ReleaseLockAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        idempotency
            .Setup(service => service.CheckRequestAsync(It.IsAny<string>()))
            .ReturnsAsync(new IdempotencyResult
            {
                HasCachedResult = exposeCachedResult,
                CachedResultJson = exposeCachedResult ? "{\"stale\":true}" : null
            });
        return idempotency;
    }

    private static TenantRegistrationRequest Request() => new()
    {
        BusinessName = "Replay Tenant",
        Email = "owner@replay.test",
        Password = "P@ssword123!",
        Subdomain = "replay-tenant",
        Country = "EG",
        Currency = "EGP"
    };
}
