using System.Collections.ObjectModel;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Cli;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class CoverageRunArgumentBindingTests
{
    [Fact]
    public async Task PublicCommandPipeline_ShouldForwardRestoredTestArgumentTokensInOrder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var project = TestPathUtils.PathUnder(temporaryDirectory.Path, "Sample.Tests.csproj");
        await File.WriteAllTextAsync(project, "<Project />");
        var runner = new CapturingProcessRunner();
        using var console = new FakeInMemoryConsole();

        await AppSurfaceCliApp.RunAsync(
            [
                "coverage", "run",
                "--test-project", project,
                "--output", TestPathUtils.PathUnder(temporaryDirectory.Path, "coverage"),
                "--coverage-driver", "collector",
                "--watchdog", "off",
                "--test-argument", "--blame-hang",
                "--test-argument=--blame-hang-timeout",
                "--test-argument", "120s",
                "--test-argument=--help"
            ],
            options =>
            {
                options.CustomRegistrations.Add(services =>
                {
                    services.AddSingleton<IConsole>(console);
                    services.AddSingleton<ICoverageRunProcessRunner>(runner);
                });
            });

        Assert.True(
            runner.TestCommandArguments.Count == 1,
            $"Expected one dotnet test invocation. Output: {console.ReadOutputString()} Error: {console.ReadErrorString()}");
        var testCommand = Assert.Single(runner.TestCommandArguments);
        var forwardedArgumentIndex = Array.IndexOf(testCommand, "--blame-hang");
        Assert.True(forwardedArgumentIndex >= 0, "Expected the literal --blame-hang test argument in dotnet test argv.");
        Assert.Equal(
            new[]
            {
                "--blame-hang", "--blame-hang-timeout", "120s", "--help"
            },
            testCommand.Skip(forwardedArgumentIndex).Take(4));
        Assert.DoesNotContain(testCommand, argument => argument.StartsWith("appsurface-test-argument-", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_ShouldReplaceSplitAndJoinedValuesAndRestoreExactOrder()
    {
        var input = new[]
        {
            "coverage", "run", "--test-project", "sample.csproj",
            "--test-argument", "--blame-hang",
            "--configuration", "Release",
            "--test-argument=--blame-hang-timeout",
            "--test-argument", "120s",
            "--test-argument=--help"
        };

        var normalized = CoverageRunArgumentBinding.Normalize(input);
        var restored = normalized.Arguments
            .Select(argument => normalized.RestoreValues.TryGetValue(argument, out var value) ? value : argument)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "coverage", "run", "--test-project", "sample.csproj",
                "--test-argument", "--blame-hang",
                "--configuration", "Release",
                "--test-argument", "--blame-hang-timeout",
                "--test-argument", "120s",
                "--test-argument", "--help"
            },
            restored);
        Assert.Equal(4, normalized.RestoreValues.Count);
        Assert.All(normalized.RestoreValues.Keys, placeholder => Assert.DoesNotContain(placeholder, input));
    }

    [Fact]
    public void Normalize_ShouldConsumeTestHostSeparatorAsOneLiteralValue()
    {
        var normalized = CoverageRunArgumentBinding.Normalize(
            ["coverage", "run", "--test-argument", "--", "--test-argument=--help"]);

        var restored = normalized.Arguments
            .Select(argument => normalized.RestoreValues.TryGetValue(argument, out var value) ? value : argument)
            .ToArray();

        Assert.Equal(
            ["coverage", "run", "--test-argument", "--", "--test-argument", "--help"],
            restored);
    }

    [Theory]
    [InlineData("other", "run", "--test-argument", "--help")]
    [InlineData("coverage", "", "--test-argument", "--help")]
    [InlineData("coverage", "run", "--test-argument-extra", "value")]
    [InlineData("--help")]
    public void Normalize_ShouldPassUnrelatedRoutesAndOptionsThrough(string first, params string[] remaining)
    {
        var input = new[] { first }.Concat(remaining).ToArray();

        var normalized = CoverageRunArgumentBinding.Normalize(input);

        Assert.Equal(input, normalized.Arguments);
        Assert.Empty(normalized.RestoreValues);
    }

    [Fact]
    public void Normalize_ShouldCarryMissingOrEmptyValuesToCommandValidation()
    {
        string[][] inputs =
        [
            ["coverage", "run", "--test-argument"],
            ["coverage", "run", "--test-argument="],
            ["coverage", "run", "--test-argument", ""]
        ];

        foreach (var normalized in inputs.Select(CoverageRunArgumentBinding.Normalize))
        {
            Assert.Contains(string.Empty, normalized.RestoreValues.Values);
        }

        var exception = Assert.Throws<CliFx.CommandException>(() => AppSurfaceCliApp.RestoreCoverageTestArguments([string.Empty]));
        Assert.Contains("ASCOV101", exception.Message, StringComparison.Ordinal);
        Assert.Contains("--test-argument", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--test-argument")]
    [InlineData("--test-argument=")]
    public async Task PublicCommandPipeline_ShouldReportMissingLiteralValueWithoutLaunchingCoverage(string option)
    {
        using var console = new FakeInMemoryConsole();
        var runner = new CapturingProcessRunner();

        await AppSurfaceCliApp.RunAsync(["coverage", "run", option], options =>
        {
            options.CustomRegistrations.Add(services =>
            {
                services.AddSingleton<IConsole>(console);
                services.AddSingleton<ICoverageRunProcessRunner>(runner);
            });
        });

        Assert.Contains("ASCOV101", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Contains("--test-argument", console.ReadErrorString(), StringComparison.Ordinal);
        Assert.Empty(runner.TestCommandArguments);
    }

    [Fact]
    public void Normalize_ShouldNotMutateInputAndRestoreMapIsReadOnly()
    {
        var input = new[] { "COVERAGE", "RUN", "--test-argument=literal" };
        var original = input.ToArray();

        var normalized = CoverageRunArgumentBinding.Normalize(input);

        Assert.Equal(original, input);
        Assert.IsType<ReadOnlyDictionary<string, string>>(normalized.RestoreValues);
    }

    [Fact]
    public void RestoreCoverageTestArguments_ShouldReturnOriginalArrayWhenNoInvocationMapIsActive()
    {
        var arguments = new[] { "--filter", "Category=Unit" };

        var restored = AppSurfaceCliApp.RestoreCoverageTestArguments(arguments);

        Assert.Same(arguments, restored);
    }

    private sealed class CapturingProcessRunner : ICoverageRunProcessRunner
    {
        public List<string[]> TestCommandArguments { get; } = [];

        public Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            if (request.Arguments.FirstOrDefault() == "msbuild")
            {
                const string capability = """
                    {"Properties":{"TestingPlatformDotnetTestSupport":"false","TargetFramework":"net10.0"},"Items":{"PackageReference":[{"Identity":"coverlet.collector"}]}}
                    """;
                return Task.FromResult(new CoverageRunProcessResult(0, capability, StandardOutput: capability));
            }

            if (request.Arguments.FirstOrDefault() == "test")
            {
                var arguments = request.Arguments.ToArray();
                TestCommandArguments.Add(arguments);
                var resultsIndex = Array.IndexOf(arguments, "--results-directory");
                var rawDirectory = request.Arguments[resultsIndex + 1];
                Directory.CreateDirectory(TestPathUtils.PathUnder(rawDirectory, "coverage"));
                File.WriteAllText(TestPathUtils.PathUnder(rawDirectory, "coverage", "coverage.cobertura.xml"),
                    "<coverage lines-covered=\"8\" lines-valid=\"10\" branches-covered=\"2\" branches-valid=\"4\" />");
                return Task.FromResult(new CoverageRunProcessResult(0, "synthetic test success"));
            }

            return Task.FromResult(new CoverageRunProcessResult(0, string.Empty));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = TestPathUtils.PathUnder(System.IO.Path.GetTempPath(), $"appsurface-argv-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
