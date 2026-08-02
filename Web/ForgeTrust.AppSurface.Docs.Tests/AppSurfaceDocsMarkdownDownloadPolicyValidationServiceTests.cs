using FakeItEasy;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsMarkdownDownloadPolicyValidationServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldSkipPolicyResolutionWhenMarkdownDownloadIsDisabled()
    {
        var service = new AppSurfaceDocsMarkdownDownloadPolicyValidationService(
            new AppSurfaceDocsOptions(),
            new ServiceCollection().BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipPolicyResolutionWhenMarkdownDownloadOptionsAreNull()
    {
        var service = new AppSurfaceDocsMarkdownDownloadPolicyValidationService(
            new AppSurfaceDocsOptions { MarkdownDownload = null! },
            new ServiceCollection().BuildServiceProvider());

        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsMarkdownDownloadPolicyValidationService(null!, services));
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsMarkdownDownloadPolicyValidationService(new AppSurfaceDocsOptions(), null!));
    }

    [Fact]
    public async Task StartAsync_ShouldRejectAnUnknownEnabledPolicy()
    {
        var policyProvider = A.Fake<IAuthorizationPolicyProvider>();
        A.CallTo(() => policyProvider.GetPolicyAsync("DocsReader")).Returns((AuthorizationPolicy?)null);
        using var services = new ServiceCollection()
            .AddSingleton(policyProvider)
            .BuildServiceProvider();
        var service = new AppSurfaceDocsMarkdownDownloadPolicyValidationService(
            CreateEnabledOptions(),
            services);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));

        Assert.Contains("DocsReader", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_ShouldRejectAnEnabledPolicyWhenAuthorizationServicesAreMissing()
    {
        var service = new AppSurfaceDocsMarkdownDownloadPolicyValidationService(
            CreateEnabledOptions(),
            new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_ShouldRejectAnEnabledBlankPolicyAsADefenseInDepthGuard()
    {
        var options = CreateEnabledOptions();
        options.MarkdownDownload.AuthorizationPolicy = " ";
        var service = new AppSurfaceDocsMarkdownDownloadPolicyValidationService(
            options,
            new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_ShouldAllowARegisteredEnabledPolicy()
    {
        var policyProvider = A.Fake<IAuthorizationPolicyProvider>();
        A.CallTo(() => policyProvider.GetPolicyAsync("DocsReader"))
            .Returns(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        using var services = new ServiceCollection()
            .AddSingleton(policyProvider)
            .BuildServiceProvider();
        var service = new AppSurfaceDocsMarkdownDownloadPolicyValidationService(
            CreateEnabledOptions(),
            services);

        await service.StartAsync(CancellationToken.None);
    }

    private static AppSurfaceDocsOptions CreateEnabledOptions()
    {
        return new AppSurfaceDocsOptions
        {
            MarkdownDownload = new AppSurfaceDocsMarkdownDownloadOptions
            {
                Enabled = true,
                AuthorizationPolicy = "DocsReader"
            }
        };
    }
}
