using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class AppSurfaceDocsAliasOwnershipStateTests
{
    [Fact]
    public void SelectBeforeBindingDoesNotFreezeAnEmptyOwnerTable()
    {
        var state = CreateState();
        var context = Context("/docs/a/stable/guide", "/docs/a/stable/guide");
        Assert.Null(state.Select(context));
        Assert.Null(context.GetEndpoint());

        using var services = new ServiceCollection().BuildServiceProvider();
        var owner = Owner("/docs/a");
        state.Bind(new TestEndpointRouteBuilder(services, new DefaultEndpointDataSource(owner)));

        Assert.NotNull(state.Select(context));
        Assert.Same(owner, context.GetEndpoint());
    }

    [Fact]
    public void EmptyBoundTableLeavesRequestsUnownedAndRejectsRebinding()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var state = CreateState();
        var endpoints = new TestEndpointRouteBuilder(services, new DefaultEndpointDataSource([]));
        state.Bind(endpoints);
        var context = Context("/docs/a/stable/guide", "/docs/a/stable/guide");

        Assert.Null(state.Select(context));
        Assert.Null(context.GetEndpoint());
        Assert.Throws<InvalidOperationException>(() => state.Bind(endpoints));
    }

    [Fact]
    public async Task ConcurrentFirstSelectionsUseMatchingRootsAndOriginalEndpoints()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var owners = new[] { Owner("/docs/a"), Owner("/other/a") };
        var source = new RecordingEndpointDataSource(owners);
        var endpoints = new TestEndpointRouteBuilder(services, source);
        var state = CreateState();
        state.Bind(endpoints);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selections = Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var root = index % 2 == 0 ? "/docs/a" : "/other/a";
            var safe = index % 3 != 0;
            var context = Context(root + (safe ? "/stable/guide" : "/stable/../guide"),
                safe ? root + "/stable/guide" : root.Replace("/a", "") + "/guide");
            var request = state.Select(context);

            Assert.NotNull(request);
            Assert.Equal(root, request.NamespaceRoot);
            Assert.Equal(safe, request.IsSafe);
            Assert.Same(owners[index % 2], context.GetEndpoint());
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(selections);
        Assert.Equal(1, source.ReadCount);
        Assert.Throws<InvalidOperationException>(() => state.Bind(endpoints));

        endpoints.DataSources.Clear();
        var later = Context("/other/a/stable/guide", "/other/a/stable/guide");
        Assert.NotNull(state.Select(later));
        Assert.Same(owners[1], later.GetEndpoint());
        var unrelated = Context("/docs/apple", "/docs/apple");
        Assert.Null(state.Select(unrelated));
        Assert.Null(unrelated.GetEndpoint());
        Assert.Equal(1, source.ReadCount);
    }

    private static AppSurfaceDocsAliasOwnershipState CreateState() => new(NullLogger<AppSurfaceDocsAliasOwnershipState>.Instance);

    private static Endpoint Owner(string root) => new(_ => Task.CompletedTask,
        new EndpointMetadataCollection(new AppSurfaceDocsAliasOwnershipMetadata(root)), root);

    private static DefaultHttpContext Context(string raw, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = raw;
        return context;
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider services, EndpointDataSource source) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = [source];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private sealed class RecordingEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        private int _readCount;
        internal int ReadCount => Volatile.Read(ref _readCount);

        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                Interlocked.Increment(ref _readCount);
                return endpoints;
            }
        }

        public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() =>
            Microsoft.Extensions.FileProviders.NullChangeToken.Singleton;
    }
}
