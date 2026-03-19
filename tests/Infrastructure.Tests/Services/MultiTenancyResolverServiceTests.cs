using FakeItEasy;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Infrastructure.Tests.Services;

public class MultiTenancyResolverServiceTests
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISystemContext _systemContext;
    private readonly MultiTenancyResolverService _sut;

    public MultiTenancyResolverServiceTests()
    {
        _httpContextAccessor = A.Fake<IHttpContextAccessor>();
        _systemContext = A.Fake<ISystemContext>();
        _sut = new MultiTenancyResolverService(_httpContextAccessor, _systemContext);
    }

    [Fact]
    public void GetTenantRepository_NoHttpContext_ReturnsSystemTenant()
    {
        // Arrange
        A.CallTo(() => _httpContextAccessor.HttpContext).Returns(null);
        var systemRepo = A.Fake<ITenantRepository>();
        A.CallTo(() => _systemContext.GetSystemTenantRepository()).Returns(systemRepo);

        // Act
        var result = _sut.GetTenantRepository();

        // Assert
        Assert.Same(systemRepo, result);
    }

    [Fact]
    public void GetTenantRepository_HttpContextWithTenantInItems_ReturnsTenantRepository()
    {
        // Arrange
        var tenantRepo = A.Fake<ITenantRepository>();
        var httpContext = new DefaultHttpContext();
        httpContext.Items[InfrastructureCommon.TenantRepositoryName] = tenantRepo;
        A.CallTo(() => _httpContextAccessor.HttpContext).Returns(httpContext);

        // Act
        var result = _sut.GetTenantRepository();

        // Assert
        Assert.Same(tenantRepo, result);
    }

    [Fact]
    public void GetTenantRepository_HttpContextWithTenantIdRouteButNoRepoInItems_ThrowsTenantNotFoundException()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues = new RouteValueDictionary
        {
            { InfrastructureCommon.TenantIdRoute, "sbeg" }
        };
        A.CallTo(() => _httpContextAccessor.HttpContext).Returns(httpContext);

        // Act & Assert
        Assert.Throws<TenantNotFoundException>(() => _sut.GetTenantRepository());
    }

    [Fact]
    public void GetTenantRepository_HttpContextWithNoTenantIdRouteAndNoRepoInItems_ReturnsSystemTenant()
    {
        // Arrange — OIDC endpoints like /.well-known/openid-configuration have no tenantId in route
        var httpContext = new DefaultHttpContext();
        A.CallTo(() => _httpContextAccessor.HttpContext).Returns(httpContext);
        var systemRepo = A.Fake<ITenantRepository>();
        A.CallTo(() => _systemContext.GetSystemTenantRepository()).Returns(systemRepo);

        // Act
        var result = _sut.GetTenantRepository();

        // Assert
        Assert.Same(systemRepo, result);
    }

    [Fact]
    public void GetTenantId_NoTenantIdInItems_ThrowsTenantNotFoundException()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        A.CallTo(() => _httpContextAccessor.HttpContext).Returns(httpContext);

        // Act & Assert
        Assert.Throws<TenantNotFoundException>(() => _sut.GetTenantId());
    }

    [Fact]
    public void GetTenantId_WithTenantIdInItems_ReturnsTenantId()
    {
        // Arrange
        var httpContext = new DefaultHttpContext();
        httpContext.Items[InfrastructureCommon.TenantIdName] = "sbeg";
        A.CallTo(() => _httpContextAccessor.HttpContext).Returns(httpContext);

        // Act
        var result = _sut.GetTenantId();

        // Assert
        Assert.Equal("sbeg", result);
    }
}
