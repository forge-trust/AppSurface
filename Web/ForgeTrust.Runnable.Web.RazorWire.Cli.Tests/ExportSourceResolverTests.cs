using CliFx.Exceptions;
using FakeItEasy;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.Runnable.Web.RazorWire.Cli.Tests;

public class ExportSourceResolverTests
{
    private readonly ILoggerFactory _loggerFactory = A.Fake<ILoggerFactory>();

    [Fact]
    public void BuildEffectiveAppArgs_Should_Inject_Ephemeral_Urls_When_Absent()
    {
        var result = ExportSourceResolver.BuildEffectiveAppArgs(["--foo", "bar"]);

        Assert.Equal(["--foo", "bar", "--urls", "http://127.0.0.1:0"], result);
    }

    [Fact]
    public void BuildEffectiveAppArgs_Should_Not_Inject_When_Urls_Already_Present()
    {
        var result = ExportSourceResolver.BuildEffectiveAppArgs(["--urls", "http://127.0.0.1:1234"]);

        Assert.Equal(["--urls", "http://127.0.0.1:1234"], result);
    }

    [Fact]
    public void BuildEffectiveAppArgs_Should_Not_Inject_When_Urls_Uses_Equals_Syntax()
    {
        var result = ExportSourceResolver.BuildEffectiveAppArgs(["--urls=http://127.0.0.1:1234"]);

        Assert.Equal(["--urls=http://127.0.0.1:1234"], result);
    }

    [Fact]
    public void TryParseListeningBaseUrl_Should_Parse_Normalized_BaseUrl()
    {
        var ok = ExportSourceResolver.TryParseListeningBaseUrl(
            "Now listening on: http://127.0.0.1:54321",
            out var baseUrl);

        Assert.True(ok);
        Assert.Equal("http://127.0.0.1:54321", baseUrl);
    }

    [Fact]
    public void TryParseListeningBaseUrl_Should_Preserve_Ipv6_Authority()
    {
        var ok = ExportSourceResolver.TryParseListeningBaseUrl(
            "Now listening on: http://[::1]:54321",
            out var baseUrl);

        Assert.True(ok);
        Assert.Equal("http://[::1]:54321", baseUrl);
    }

    [Fact]
    public void TryParseListeningBaseUrl_Should_Return_False_For_NonListening_Line()
    {
        var ok = ExportSourceResolver.TryParseListeningBaseUrl(
            "Starting application on port 5000",
            out var baseUrl);

        Assert.False(ok);
        Assert.True(string.IsNullOrEmpty(baseUrl));
    }

    [Fact]
    public void Constructor_Should_Throw_For_Null_Dependencies()
    {
        var processFactory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var httpFactory = new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK));
        var razorWireExecutor = A.Fake<ICommandExecutor>();

        Assert.Throws<ArgumentNullException>(() => new ExportSourceResolver(null!, processFactory, httpFactory));
        Assert.Throws<ArgumentNullException>(() => new ExportSourceResolver(_loggerFactory, null!, httpFactory, razorWireExecutor));
        Assert.Throws<ArgumentNullException>(() => new ExportSourceResolver(_loggerFactory, processFactory, null!, razorWireExecutor));
        Assert.Throws<ArgumentNullException>(() => new ExportSourceResolver(_loggerFactory, processFactory, httpFactory, null!));
    }

    [Fact]
    public async Task ResolveAsync_Should_Start_Process_And_Return_Resolved_Url()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        resolver.ListeningUrlTimeout = TimeSpan.FromSeconds(1);
        resolver.AppReadyTimeout = TimeSpan.FromSeconds(1);
        resolver.AppReadyPollInterval = TimeSpan.FromMilliseconds(10);

        var request = new ExportSourceRequest(
            ExportSourceKind.Dll,
            "/tmp/app.dll",
            null,
            ["--foo", "bar"],
            false);

        fakeProcess.OnStart = () =>
        {
            fakeProcess.EmitOutput("Now listening on: http://127.0.0.1:5010");
        };

        await using var result = await resolver.ResolveAsync(request);

        Assert.Equal("http://127.0.0.1:5010", result.BaseUrl);
        Assert.True(fakeProcess.Started);
        Assert.False(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Return_Url_Source_Without_Starting_Process()
    {
        var createCallCount = 0;
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ =>
        {
            createCallCount++;
            return fakeProcess;
        });
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(
            ExportSourceKind.Url,
            "http://localhost:5233",
            null,
            [],
            false);

        await using var result = await resolver.ResolveAsync(request);

        Assert.Equal("http://localhost:5233", result.BaseUrl);
        Assert.Equal(0, createCallCount);
        Assert.False(fakeProcess.Started);
    }

    [Fact]
    public async Task ResolveAsync_Should_Throw_CommandException_When_Url_Source_Is_Unreachable()
    {
        var createCallCount = 0;
        var factory = new FakeTargetAppProcessFactory(_ =>
        {
            createCallCount++;
            return new FakeTargetAppProcess();
        });
        var resolver = CreateResolver(factory, new ThrowingHttpClientFactory());
        resolver.AppReadyTimeout = TimeSpan.FromMilliseconds(100);

        var request = new ExportSourceRequest(
            ExportSourceKind.Url,
            "http://localhost:5233",
            null,
            [],
            false);

        var ex = await Assert.ThrowsAsync<CommandException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("--url target 'http://localhost:5233'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reachable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not reachable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, createCallCount);
    }

    [Fact]
    public async Task ResolveAsync_Should_Throw_CommandException_When_Url_Source_Times_Out()
    {
        var createCallCount = 0;
        var factory = new FakeTargetAppProcessFactory(_ =>
        {
            createCallCount++;
            return new FakeTargetAppProcess();
        });
        var resolver = CreateResolver(factory, new NeverCompletesHttpClientFactory());
        resolver.AppReadyTimeout = TimeSpan.FromMilliseconds(120);

        var request = new ExportSourceRequest(
            ExportSourceKind.Url,
            "http://localhost:5233",
            null,
            [],
            false);

        var ex = await Assert.ThrowsAsync<CommandException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("Timed out while connecting to --url target", ex.Message, StringComparison.Ordinal);
        Assert.Contains("running and reachable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, createCallCount);
    }

    [Fact]
    public async Task ResolveAsync_Should_Throw_CommandException_When_HttpClient_Timeout_Fires_First_For_Url_Source()
    {
        var createCallCount = 0;
        var factory = new FakeTargetAppProcessFactory(_ =>
        {
            createCallCount++;
            return new FakeTargetAppProcess();
        });
        var resolver = CreateResolver(factory, new ClientTimeoutHttpClientFactory(TimeSpan.FromMilliseconds(50)));
        resolver.AppReadyTimeout = TimeSpan.FromSeconds(1);

        var request = new ExportSourceRequest(
            ExportSourceKind.Url,
            "http://localhost:5233",
            null,
            [],
            false);

        var ex = await Assert.ThrowsAsync<CommandException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("Timed out while connecting to --url target", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.05 seconds", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, createCallCount);
    }

    [Fact]
    public async Task ResolveAsync_Should_Dispose_Process_And_Throw_When_Url_Timeouts()
    {
        var fakeProcess = new FakeTargetAppProcess();
        fakeProcess.OnStart = () => fakeProcess.EmitOutput("boot log line");
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        resolver.ListeningUrlTimeout = TimeSpan.FromMilliseconds(100);

        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("boot log line", ex.Message);
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Dispose_Process_And_Throw_When_Process_Exits_Early()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        resolver.ListeningUrlTimeout = TimeSpan.FromSeconds(1);

        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);
        fakeProcess.OnStart = () =>
        {
            fakeProcess.EmitError("failed to bind");
            fakeProcess.TriggerExit();
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("failed to bind", ex.Message);
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Surface_Strict_RazorDocs_Harvest_Summary_When_Process_Exits_Early()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        resolver.ListeningUrlTimeout = TimeSpan.FromSeconds(1);

        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);
        fakeProcess.OnStart = () =>
        {
            fakeProcess.EmitError("Harvester MarkdownHarvester failed at /tmp/private-repo");
            fakeProcess.EmitError(
                "RazorDocs strict harvest failed because every configured harvester failed, timed out, or canceled. Diagnostics: razordocs.harvest.all_failed (Critical): All RazorDocs harvesters failed.");
            fakeProcess.TriggerExit();
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("RazorDocs strict harvest failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("razordocs.harvest.all_failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Harvester MarkdownHarvester failed at /tmp/private-repo", ex.Message, StringComparison.Ordinal);
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Dispose_Process_When_Readiness_Times_Out()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(factory, new ThrowingHttpClientFactory());
        resolver.ListeningUrlTimeout = TimeSpan.FromSeconds(1);
        resolver.AppReadyTimeout = TimeSpan.FromMilliseconds(120);
        resolver.AppReadyPollInterval = TimeSpan.FromMilliseconds(20);

        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);
        fakeProcess.OnStart = () => fakeProcess.EmitOutput("Now listening on: http://127.0.0.1:5050");

        await Assert.ThrowsAsync<TimeoutException>(async () => await resolver.ResolveAsync(request));
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Dispose_Process_When_Start_Throws()
    {
        var fakeProcess = new FakeTargetAppProcess
        {
            OnStart = () => throw new InvalidOperationException("start failed")
        };

        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync(request));

        Assert.Equal("start failed", ex.Message);
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Propagate_External_Cancellation()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(factory, new ThrowingHttpClientFactory());
        resolver.ListeningUrlTimeout = TimeSpan.FromSeconds(1);
        resolver.AppReadyTimeout = TimeSpan.FromSeconds(10);
        resolver.AppReadyPollInterval = TimeSpan.FromMilliseconds(20);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);
        fakeProcess.OnStart = () => fakeProcess.EmitOutput("Now listening on: http://127.0.0.1:5050");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await resolver.ResolveAsync(request, cts.Token));
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Treat_404_As_Ready()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.NotFound)));

        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/site.dll", null, [], false);
        fakeProcess.OnStart = () => fakeProcess.EmitOutput("Now listening on: http://127.0.0.1:5050");

        await using var result = await resolver.ResolveAsync(request);
        Assert.Equal("http://127.0.0.1:5050", result.BaseUrl);
    }

    [Fact]
    public void BuildProcessLaunchSpec_Should_Throw_For_Project_Mode()
    {
        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(ExportSourceKind.Project, "/tmp/site.csproj", null, ["--flag"], false);

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.BuildProcessLaunchSpec(request));
        Assert.Contains("resolved to a DLL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveLaunchRequestAsync_Should_Resolve_Project_To_Published_Dll_When_NoBuild()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "MySite.csproj");
        var dllDir = Path.Combine(tempDir.FullPath, "bin", "Release", "net10.0", "publish");
        Directory.CreateDirectory(dllDir);
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        var expectedDllPath = Path.Combine(dllDir, "MySite.dll");
        await File.WriteAllBytesAsync(expectedDllPath, [1, 2, 3]);

        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(ExportSourceKind.Project, projectPath, null, [], true);

        var resolved = await resolver.ResolveLaunchRequestAsync(request);

        Assert.Equal(ExportSourceKind.Dll, resolved.SourceKind);
        Assert.Equal(ExportSourceResolver.NormalizePathForMacOS(expectedDllPath), resolved.SourceValue);
    }

    [Fact]
    public async Task ResolveLaunchRequestAsync_Should_Respect_Custom_AssemblyName_When_NoBuild()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "MySite.csproj");
        var dllDir = Path.Combine(tempDir.FullPath, "bin", "Release", "net10.0", "publish");
        Directory.CreateDirectory(dllDir);
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <AssemblyName>CustomSite</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        var expectedDllPath = Path.Combine(dllDir, "CustomSite.dll");
        await File.WriteAllBytesAsync(expectedDllPath, [1, 2, 3]);

        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(ExportSourceKind.Project, projectPath, null, [], true);

        var resolved = await resolver.ResolveLaunchRequestAsync(request);

        Assert.Equal(ExportSourceKind.Dll, resolved.SourceKind);
        Assert.Equal(ExportSourceResolver.NormalizePathForMacOS(expectedDllPath), resolved.SourceValue);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResolveLaunchRequestAsync_Should_Publish_Project_When_NoBuild_Is_False()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "MySite.csproj");
        var programPath = Path.Combine(tempDir.FullPath, "Program.cs");

        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(programPath, "System.Console.WriteLine(\"hello\");");

        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var mockRazorWireExecutor = A.Fake<ICommandExecutor>();
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)),
            mockRazorWireExecutor);
        var request = new ExportSourceRequest(ExportSourceKind.Project, projectPath, null, [], false);

        var expectedPath = Path.GetFullPath(Path.Combine(tempDir.FullPath, "bin", "runnable-export", "MySite.dll"));
        IReadOnlyList<string>? capturedPublishArgs = null;
        A.CallTo(() => mockRazorWireExecutor.ExecuteCommandAsync(
                A<string>._,
                A<IReadOnlyList<string>>._,
                A<string>._,
                A<CancellationToken>._))
            .ReturnsLazily((string _, IReadOnlyList<string> args, string _, CancellationToken _) =>
            {
                if (args.Contains("-getProperty:AssemblyName"))
                {
                    return new ProcessResult(0, "MySite", string.Empty);
                }

                if (args.Count > 0 && args[0] == "publish")
                {
                    capturedPublishArgs = args.ToArray();
                    Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
                    File.WriteAllText(expectedPath, string.Empty);
                }

                return new ProcessResult(0, string.Empty, string.Empty);
            });

        var resolved = await resolver.ResolveLaunchRequestAsync(request);

        Assert.Equal(ExportSourceKind.Dll, resolved.SourceKind);
        Assert.True(File.Exists(resolved.SourceValue));
        Assert.Equal(ExportSourceResolver.NormalizePathForMacOS(expectedPath), resolved.SourceValue);
        Assert.NotNull(capturedPublishArgs);
        Assert.Equal("publish", capturedPublishArgs[0]);
        Assert.Contains(projectPath, capturedPublishArgs);
        Assert.Contains("--disable-build-servers", capturedPublishArgs);
        Assert.Contains("-c", capturedPublishArgs);
        Assert.Contains("Release", capturedPublishArgs);
        Assert.Contains("-o", capturedPublishArgs);
        Assert.Contains(Path.Combine(tempDir.FullPath, "bin", "runnable-export"), capturedPublishArgs);
    }

    [Fact]
    public async Task ResolveLaunchRequestAsync_Should_Throw_When_Build_Fails()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "Missing.csproj");

        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var mockExecutor = A.Fake<ICommandExecutor>();
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)),
            mockExecutor);

        A.CallTo(() => mockExecutor.ExecuteCommandAsync(
                A<string>._,
                A<IReadOnlyList<string>>._,
                A<string>._,
                A<CancellationToken>._))
            .ReturnsLazily((string _, IReadOnlyList<string> args, string _, CancellationToken _) =>
            {
                if (args.Count > 0 && args[0] == "publish")
                {
                    return new ProcessResult(1, string.Empty, "publish error");
                }

                return new ProcessResult(0, "Missing", string.Empty);
            });

        var request = new ExportSourceRequest(ExportSourceKind.Project, projectPath, null, [], false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveLaunchRequestAsync(request));
        Assert.Contains("dotnet publish", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsMultiTargetProject_Should_Return_True_When_TargetFrameworks_Exists()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "MySite.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(ExportSourceResolver.IsMultiTargetProject(projectPath));
    }

    [Fact]
    public void IsMultiTargetProject_Should_Return_False_When_TargetFrameworks_Is_Missing()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "MySite.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(ExportSourceResolver.IsMultiTargetProject(projectPath));
    }

    [Fact]
    public async Task ResolveLaunchRequestAsync_Should_Throw_When_MultiTarget_Project_And_Framework_Missing()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "MySite.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(ExportSourceKind.Project, projectPath, null, [], false);

        var ex = await Assert.ThrowsAsync<CommandException>(
            () => resolver.ResolveLaunchRequestAsync(request));
        Assert.Contains("multi-target project", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--framework", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResolveLaunchRequestAsync_Should_Inject_Framework_Arg_When_Framework_Specified()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "MySite.csproj");
        var programPath = Path.Combine(tempDir.FullPath, "Program.cs");

        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(programPath, "System.Console.WriteLine(\"hello\");");

        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var mockRazorWireExecutor = A.Fake<ICommandExecutor>();
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)),
            mockRazorWireExecutor);
        var request = new ExportSourceRequest(ExportSourceKind.Project, projectPath, "net10.0", [], false);

        var expectedPath = Path.Combine(tempDir.FullPath, "bin", "runnable-export", "MySite.dll");
        IReadOnlyList<string>? capturedPublishArgs = null;
        A.CallTo(() => mockRazorWireExecutor.ExecuteCommandAsync(
                A<string>._,
                A<IReadOnlyList<string>>._,
                A<string>._,
                A<CancellationToken>._))
            .ReturnsLazily((string _, IReadOnlyList<string> args, string _, CancellationToken _) =>
            {
                if (args.Contains("-getProperty:AssemblyName"))
                {
                    return new ProcessResult(0, "MySite", string.Empty);
                }

                if (args.Count > 0 && args[0] == "publish")
                {
                    capturedPublishArgs = args.ToArray();
                    Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
                    File.WriteAllText(expectedPath, string.Empty);
                }

                return new ProcessResult(0, string.Empty, string.Empty);
            });

        var resolved = await resolver.ResolveLaunchRequestAsync(request);

        Assert.Equal(ExportSourceKind.Dll, resolved.SourceKind);
        Assert.True(File.Exists(resolved.SourceValue));
        var normalizedExpected = ExportSourceResolver.NormalizePathForMacOS(
            Path.Combine(tempDir.FullPath, "bin", "runnable-export", "MySite.dll"));
        Assert.Equal(normalizedExpected, resolved.SourceValue);
        Assert.NotNull(capturedPublishArgs);
        Assert.Contains("--disable-build-servers", capturedPublishArgs);
        Assert.Contains("-f", capturedPublishArgs);
        Assert.Contains("net10.0", capturedPublishArgs);
    }

    [Fact]
    public void ResolveBuiltDllPath_Should_Honor_Requested_Framework_When_Provided()
    {
        using var tempDir = new TempDirectory();

        var net9Dir = Path.Combine(tempDir.FullPath, "bin", "Release", "net9.0", "publish");
        Directory.CreateDirectory(net9Dir);
        File.WriteAllBytes(Path.Combine(net9Dir, "MySite.dll"), [1, 2, 3]);

        var net10Dir = Path.Combine(tempDir.FullPath, "bin", "Release", "net10.0", "publish");
        Directory.CreateDirectory(net10Dir);
        File.WriteAllBytes(Path.Combine(net10Dir, "MySite.dll"), [4, 5, 6]);

        // When requestedFramework is net9.0, it should find the net9.0 one instead of falling back to the highest (net10.0).
        var resolved = ExportSourceResolver.ResolveBuiltDllPath(tempDir.FullPath, "MySite", null, "net9.0");

        Assert.Equal(ExportSourceResolver.NormalizePathForMacOS(Path.Combine(net9Dir, "MySite.dll")), resolved);
    }

    [Fact]
    public void ResolveBuiltDllPath_Should_Throw_When_Release_Directory_Does_Not_Exist()
    {
        using var tempDir = new TempDirectory();

        var ex = Assert.Throws<FileNotFoundException>(
            () => ExportSourceResolver.ResolveBuiltDllPath(tempDir.FullPath, "MySite"));
        Assert.Contains("could not find any publish output", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBuiltDllPath_Should_Prefer_Explicit_Publish_Directory_When_Provided()
    {
        using var tempDir = new TempDirectory();
        var explicitPublishDir = Path.Combine(tempDir.FullPath, "custom-output");
        Directory.CreateDirectory(explicitPublishDir);
        File.WriteAllBytes(Path.Combine(explicitPublishDir, "MySite.dll"), [1, 2, 3]);

        var resolved = ExportSourceResolver.ResolveBuiltDllPath(tempDir.FullPath, "MySite", explicitPublishDir);

        Assert.Equal(ExportSourceResolver.NormalizePathForMacOS(Path.Combine(explicitPublishDir, "MySite.dll")), resolved);
    }

    [Fact]
    public void ResolveBuiltDllPath_Should_Throw_When_Explicit_Publish_Directory_Does_Not_Contain_Dll()
    {
        using var tempDir = new TempDirectory();
        var explicitPublishDir = Path.Combine(tempDir.FullPath, "custom-output");
        Directory.CreateDirectory(explicitPublishDir);

        var staleDllDir = Path.Combine(tempDir.FullPath, "bin", "Release", "net10.0", "publish");
        Directory.CreateDirectory(staleDllDir);
        File.WriteAllBytes(Path.Combine(staleDllDir, "MySite.dll"), [1, 2, 3]);

        var ex = Assert.Throws<FileNotFoundException>(
            () => ExportSourceResolver.ResolveBuiltDllPath(tempDir.FullPath, "MySite", explicitPublishDir));
        Assert.Contains("Could not locate published DLL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBuiltDllPath_Should_Throw_When_No_Dll_Is_Found()
    {
        using var tempDir = new TempDirectory();
        var releaseDir = Path.Combine(tempDir.FullPath, "bin", "Release", "net10.0", "publish");
        Directory.CreateDirectory(releaseDir);

        var ex = Assert.Throws<FileNotFoundException>(
            () => ExportSourceResolver.ResolveBuiltDllPath(tempDir.FullPath, "MySite"));
        Assert.Contains("Could not locate published DLL", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveBuiltDllPath_Should_Select_Highest_Target_Framework_When_Multiple_Are_Detected()
    {
        using var tempDir = new TempDirectory();
        var net9 = Path.Combine(tempDir.FullPath, "bin", "Release", "net9.0", "publish");
        var net10 = Path.Combine(tempDir.FullPath, "bin", "Release", "net10.0", "publish");
        Directory.CreateDirectory(net9);
        Directory.CreateDirectory(net10);
        File.WriteAllBytes(Path.Combine(net9, "MySite.dll"), [1]);
        File.WriteAllBytes(Path.Combine(net10, "MySite.dll"), [2]);

        var resolved = ExportSourceResolver.ResolveBuiltDllPath(tempDir.FullPath, "MySite");
        Assert.Equal(ExportSourceResolver.NormalizePathForMacOS(Path.Combine(net10, "MySite.dll")), resolved);
    }

    [Fact]
    public void ResolveBuiltDllPath_Should_Prefer_Release_Publish_Over_Debug_Publish_When_Both_Exist()
    {
        using var tempDir = new TempDirectory();
        var releaseDir = Path.Combine(tempDir.FullPath, "bin", "Release", "net10.0", "publish");
        var debugDir = Path.Combine(tempDir.FullPath, "bin", "Debug", "net10.0", "publish");
        Directory.CreateDirectory(releaseDir);
        Directory.CreateDirectory(debugDir);

        var releaseDllPath = Path.Combine(releaseDir, "MySite.dll");
        var debugDllPath = Path.Combine(debugDir, "MySite.dll");
        File.WriteAllBytes(releaseDllPath, [1]);
        File.WriteAllBytes(debugDllPath, [2]);
        File.SetLastWriteTimeUtc(releaseDllPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(debugDllPath, DateTime.UtcNow);

        var resolved = ExportSourceResolver.ResolveBuiltDllPath(tempDir.FullPath, "MySite");

        Assert.Equal(ExportSourceResolver.NormalizePathForMacOS(releaseDllPath), resolved);
    }

    [Fact]
    public void TryGetFrameworkSegment_Should_Return_Framework_For_Supported_Tfms()
    {
        var releaseDir = Path.Combine(Path.GetTempPath(), "resolver-framework-tests", "bin", "Release");

        Assert.Equal(
            "net10.0",
            ExportSourceResolver.TryGetFrameworkSegment(releaseDir, Path.Combine(releaseDir, "net10.0", "MySite.dll")));
        Assert.Equal(
            "net9",
            ExportSourceResolver.TryGetFrameworkSegment(releaseDir, Path.Combine(releaseDir, "net9", "MySite.dll")));
        Assert.Equal(
            "netcoreapp3.1",
            ExportSourceResolver.TryGetFrameworkSegment(releaseDir, Path.Combine(releaseDir, "netcoreapp3.1", "MySite.dll")));
        Assert.Equal(
            "netstandard2.0",
            ExportSourceResolver.TryGetFrameworkSegment(releaseDir, Path.Combine(releaseDir, "netstandard2.0", "MySite.dll")));
        Assert.Equal(
            "net10.0-windows",
            ExportSourceResolver.TryGetFrameworkSegment(releaseDir, Path.Combine(releaseDir, "net10.0-windows", "MySite.dll")));
    }

    [Fact]
    public void TryGetFrameworkSegment_Should_Return_Null_For_Path_Outside_ReleaseDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "resolver-framework-tests");
        var releaseDir = Path.Combine(root, "bin", "Release");
        var outsidePath = Path.Combine(root, "bin", "Debug", "net10.0", "MySite.dll");

        var segment = ExportSourceResolver.TryGetFrameworkSegment(releaseDir, outsidePath);
        Assert.Null(segment);
    }

    [Fact]
    public void TryGetFrameworkSegment_Should_Return_Null_For_NonFramework_Segment()
    {
        var releaseDir = Path.Combine(Path.GetTempPath(), "resolver-framework-tests", "bin", "Release");
        var path = Path.Combine(releaseDir, "publish", "MySite.dll");

        var segment = ExportSourceResolver.TryGetFrameworkSegment(releaseDir, path);
        Assert.Null(segment);
    }

    [Fact]
    public void ResolvePreferredFramework_Should_Select_Highest_Framework()
    {
        var releaseDir = Path.Combine(Path.GetTempPath(), "resolver-framework-tests", "bin", "Release");
        var candidates = new[]
        {
            Path.Combine(releaseDir, "net9.0", "MySite.dll"),
            Path.Combine(releaseDir, "net10.0", "MySite.dll"),
            Path.Combine(releaseDir, "netcoreapp3.1", "MySite.dll")
        };

        var selected = ExportSourceResolver.ResolvePreferredFramework(candidates, releaseDir);
        Assert.Equal("net10.0", selected);
    }

    [Fact]
    public void ResolvePreferredFramework_Should_Return_Null_When_No_Framework_Segments_Found()
    {
        var releaseDir = Path.Combine(Path.GetTempPath(), "resolver-framework-tests", "bin", "Release");
        var candidates = new[]
        {
            Path.Combine(releaseDir, "publish", "MySite.dll"),
            Path.Combine(releaseDir, "artifacts", "MySite.dll")
        };

        var selected = ExportSourceResolver.ResolvePreferredFramework(candidates, releaseDir);
        Assert.Null(selected);
    }

    [Fact]
    public void ParseFrameworkVersion_Should_Handle_Valid_And_Invalid_Inputs()
    {
        Assert.Equal(new Version(10, 0), ExportSourceResolver.ParseFrameworkVersion("net10.0"));
        Assert.Equal(new Version(9, 0), ExportSourceResolver.ParseFrameworkVersion("net9"));
        Assert.Equal(new Version(10, 0), ExportSourceResolver.ParseFrameworkVersion("net10.0-windows"));
        Assert.Equal(new Version(3, 1), ExportSourceResolver.ParseFrameworkVersion("netcoreapp3.1"));
        Assert.Equal(new Version(2, 0), ExportSourceResolver.ParseFrameworkVersion("netstandard2.0"));
        Assert.Equal(new Version(0, 0), ExportSourceResolver.ParseFrameworkVersion("publish"));
    }

    [Fact]
    public void ParseFrameworkVersion_Should_Retain_Major_When_Minor_Overflows()
    {
        Assert.Equal(new Version(10, 0), ExportSourceResolver.ParseFrameworkVersion("net10.999999999999999999999999"));
    }

    [Fact]
    public void TryResolveAssemblyNameFromXml_Should_Parse_AssemblyName_When_Present()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "Site.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <AssemblyName>CustomAssembly</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        var resolved = ExportSourceResolver.TryResolveAssemblyNameFromXml(projectPath, "Site");
        Assert.Equal("CustomAssembly", resolved);
    }

    [Fact]
    public void TryResolveAssemblyNameFromXml_Should_Fallback_When_AssemblyName_Is_Missing()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "Site.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");

        var resolved = ExportSourceResolver.TryResolveAssemblyNameFromXml(projectPath, "Site");
        Assert.Equal("Site", resolved);
    }

    [Fact]
    public void TryResolveAssemblyNameFromXml_Should_Fallback_On_Invalid_Xml()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "Site.csproj");
        File.WriteAllText(projectPath, "<Project><PropertyGroup><AssemblyName>oops");

        var resolved = ExportSourceResolver.TryResolveAssemblyNameFromXml(projectPath, "Site");
        Assert.Equal("Site", resolved);
    }

    [Fact]
    public void TryResolveAssemblyNameFromXml_Should_Fallback_On_Missing_Or_Invalid_Path()
    {
        using var tempDir = new TempDirectory();
        var missingPath = Path.Combine(tempDir.FullPath, "missing.csproj");

        Assert.Equal("Site", ExportSourceResolver.TryResolveAssemblyNameFromXml(missingPath, "Site"));
        Assert.Equal("Site", ExportSourceResolver.TryResolveAssemblyNameFromXml(tempDir.FullPath, "Site"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TryResolveAssemblyName_Should_Succeed_To_Find_AssemblyName_In_Imported_File()
    {
        using var tempDir = new TempDirectory();
        var propsPath = Path.Combine(tempDir.FullPath, "Identity.props");
        var projectPath = Path.Combine(tempDir.FullPath, "Main.csproj");

        File.WriteAllText(propsPath,
            """
            <Project>
              <PropertyGroup>
                <AssemblyName>MyRealName</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{propsPath}" />
            </Project>
            """);

        // With the new implementation, this should succeed because it uses MSBuild evaluation.
        var mockRazorWireExecutor = A.Fake<ICommandExecutor>();
        var resolver = CreateResolver(A.Fake<ITargetAppProcessFactory>(), A.Fake<IHttpClientFactory>(), mockRazorWireExecutor);

        string? capturedFileName = null;
        IReadOnlyList<string>? capturedArgs = null;
        A.CallTo(() => mockRazorWireExecutor.ExecuteCommandAsync(
                A<string>._,
                A<IReadOnlyList<string>>._,
                A<string>._,
                A<CancellationToken>._))
            .Invokes((string fileName, IReadOnlyList<string> args, string _, CancellationToken _) =>
            {
                capturedFileName = fileName;
                capturedArgs = args;
            })
            .Returns(new ProcessResult(0, "MyRealName", string.Empty));

        var resolved = await resolver.TryResolveAssemblyNameAsync(projectPath, "Main", "net10.0", CancellationToken.None);

        Assert.Equal("MyRealName", resolved);
        Assert.Equal("dotnet", capturedFileName);
        Assert.NotNull(capturedArgs);
        Assert.Equal("msbuild", capturedArgs[0]);
        Assert.Equal(projectPath, capturedArgs[1]);
        Assert.Contains("-getProperty:AssemblyName", capturedArgs);
        Assert.Contains("-nologo", capturedArgs);
        Assert.Contains("-nodeReuse:false", capturedArgs);
        Assert.Contains("-p:Configuration=Release", capturedArgs);
        Assert.Contains("-p:TargetFramework=net10.0", capturedArgs);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task TryResolveAssemblyName_Should_Invoke_Msbuild_With_Release_Configuration_And_TargetFramework()
    {
        using var tempDir = new TempDirectory();
        var projectPath = Path.Combine(tempDir.FullPath, "Main.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var mockRazorWireExecutor = A.Fake<ICommandExecutor>();
        var resolver = CreateResolver(
            A.Fake<ITargetAppProcessFactory>(),
            A.Fake<IHttpClientFactory>(),
            mockRazorWireExecutor);

        string? capturedFileName = null;
        IReadOnlyList<string>? capturedArgs = null;
        A.CallTo(() => mockRazorWireExecutor.ExecuteCommandAsync(
                A<string>._,
                A<IReadOnlyList<string>>._,
                A<string>._,
                A<CancellationToken>._))
            .Invokes((string fileName, IReadOnlyList<string> args, string _, CancellationToken _) =>
            {
                capturedFileName = fileName;
                capturedArgs = args;
            })
            .Returns(new ProcessResult(0, "ReleaseName", string.Empty));

        var resolved = await resolver.TryResolveAssemblyNameAsync(projectPath, "Main", "net10.0", CancellationToken.None);

        Assert.Equal("ReleaseName", resolved);
        Assert.Equal("dotnet", capturedFileName);
        Assert.NotNull(capturedArgs);
        Assert.Equal("msbuild", capturedArgs[0]);
        Assert.Equal(projectPath, capturedArgs[1]);
        Assert.Contains("-getProperty:AssemblyName", capturedArgs);
        Assert.Contains("-nologo", capturedArgs);
        Assert.Contains("-nodeReuse:false", capturedArgs);
        Assert.Contains("-p:Configuration=Release", capturedArgs);
        Assert.Contains("-p:TargetFramework=net10.0", capturedArgs);
    }

    [Fact]
    public void IsRefAssemblyPath_Should_Handle_Both_Path_Separators()
    {
        Assert.True(ExportSourceResolver.IsRefAssemblyPath("/tmp/bin/Release/net10.0/ref/MySite.dll"));
        Assert.True(ExportSourceResolver.IsRefAssemblyPath(@"C:\tmp\bin\Release\net10.0\ref\MySite.dll"));
        Assert.False(ExportSourceResolver.IsRefAssemblyPath("/tmp/bin/Release/net10.0/MySite.dll"));
    }

    [Fact]
    public void BuildProcessLaunchSpec_Should_Not_Inject_Ephemeral_Urls_When_User_Supplied_Urls()
    {
        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(
            ExportSourceKind.Dll,
            "/tmp/site.dll",
            null,
            ["--urls", "http://127.0.0.1:6001"],
            false);

        var spec = resolver.BuildProcessLaunchSpec(request);
        var args = spec.Arguments.ToList();

        Assert.Equal(1, args.Count(a => a == "--urls"));
        Assert.DoesNotContain("http://127.0.0.1:0", args);
    }

    [Fact]
    public async Task ResolveAsync_Should_Throw_TimeoutException_When_GetAsync_Is_Canceled_By_ReadyTimeout()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(factory, new NeverCompletesHttpClientFactory());
        resolver.ListeningUrlTimeout = TimeSpan.FromSeconds(1);
        resolver.AppReadyTimeout = TimeSpan.FromMilliseconds(120);
        resolver.AppReadyPollInterval = TimeSpan.FromMilliseconds(20);

        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);
        fakeProcess.OnStart = () => fakeProcess.EmitOutput("Now listening on: http://127.0.0.1:5050");

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("did not become ready", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public async Task ResolveAsync_Should_Throw_When_Process_Exits_During_Readiness()
    {
        var fakeProcess = new FakeTargetAppProcess();
        var factory = new FakeTargetAppProcessFactory(_ => fakeProcess);
        var resolver = CreateResolver(factory, new ThrowingHttpClientFactory());
        resolver.ListeningUrlTimeout = TimeSpan.FromSeconds(1);
        resolver.AppReadyTimeout = TimeSpan.FromSeconds(1);
        resolver.AppReadyPollInterval = TimeSpan.FromMilliseconds(20);

        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/app.dll", null, [], false);
        fakeProcess.OnStart = () =>
        {
            fakeProcess.EmitOutput("Now listening on: http://127.0.0.1:5050");
            fakeProcess.EmitError("fatal startup crash");
            fakeProcess.TriggerExit();
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync(request));

        Assert.Contains("before it became ready", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fatal startup crash", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fakeProcess.Disposed);
    }

    [Fact]
    public void BuildProcessLaunchSpec_Should_Use_Dll_Path_For_Dll_Mode()
    {
        var factory = new FakeTargetAppProcessFactory(_ => new FakeTargetAppProcess());
        var resolver = CreateResolver(
            factory,
            new TestHttpHelpers.Factory(TestHttpHelpers.FixedStatus(System.Net.HttpStatusCode.OK)));
        var request = new ExportSourceRequest(ExportSourceKind.Dll, "/tmp/site.dll", null, ["--flag"], false);

        var spec = resolver.BuildProcessLaunchSpec(request);

        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal("/tmp/site.dll", spec.Arguments[0]);
        Assert.DoesNotContain("run", spec.Arguments);
        Assert.DoesNotContain("Release", spec.Arguments);
        Assert.DoesNotContain("--no-launch-profile", spec.Arguments);
        Assert.Contains("--flag", spec.Arguments);
        Assert.Equal("Production", spec.EnvironmentOverrides["DOTNET_ENVIRONMENT"]);
        Assert.Equal("Production", spec.EnvironmentOverrides["ASPNETCORE_ENVIRONMENT"]);
    }

    private ExportSourceResolver CreateResolver(
        ITargetAppProcessFactory processFactory,
        IHttpClientFactory clientFactory,
        ICommandExecutor? razorWireExecutor = null)
    {
        return new ExportSourceResolver(
            _loggerFactory,
            processFactory,
            clientFactory,
            razorWireExecutor ?? A.Fake<ICommandExecutor>());
    }

    private sealed class FakeTargetAppProcessFactory : ITargetAppProcessFactory
    {
        private readonly Func<ProcessLaunchSpec, ITargetAppProcess> _factory;

        public FakeTargetAppProcessFactory(Func<ProcessLaunchSpec, ITargetAppProcess> factory)
        {
            _factory = factory;
        }

        public ITargetAppProcess Create(ProcessLaunchSpec spec) => _factory(spec);
    }

    private sealed class FakeTargetAppProcess : ITargetAppProcess
    {
        public event Action<string>? OutputLineReceived;
        public event Action<string>? ErrorLineReceived;
        public event Action? Exited;

        public bool HasExited { get; private set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public Action? OnStart { get; set; }

        public void Start()
        {
            Started = true;
            OnStart?.Invoke();
        }

        public void EmitOutput(string line) => OutputLineReceived?.Invoke(line);
        public void EmitError(string line) => ErrorLineReceived?.Invoke(line);

        public void TriggerExit()
        {
            HasExited = true;
            Exited?.Invoke();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            HasExited = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new ThrowingHandler());
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("not reachable");
        }
    }

    private sealed class NeverCompletesHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new NeverCompletesHandler());
        }
    }

    private sealed class ClientTimeoutHttpClientFactory(TimeSpan timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new NeverCompletesHandler())
            {
                Timeout = timeout
            };
        }
    }

    private sealed class NeverCompletesHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string FullPath { get; }

        public TempDirectory()
        {
            FullPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(FullPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, true);
            }
        }
    }

}
