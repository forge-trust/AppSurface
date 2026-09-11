using FakeItEasy;
using ForgeTrust.AppSurface.Caching;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace ForgeTrust.AppSurface.Docs.Tests;

public sealed class PythonDocHarvesterTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateTempSubdirectory("appsurface-docs-python-").FullName;

    [Fact]
    public async Task HarvestAsync_ReturnsNoDocs_WhenPythonHarvestingIsDisabled()
    {
        await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    \"\"\"Run.\"\"\"\n");
        var options = CreateEnabledOptions("worker.py");
        options.Harvest.Python.Enabled = false;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_EmitsGuidanceAndDoesNotReadSources_WhenPythonIncludeIsMissing()
    {
        await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    \"\"\"Run.\"\"\"\n");
        var harvester = CreateHarvester(new AppSurfaceDocsOptions());

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));
        Assert.Equal(DocHarvestDiagnosticCodes.PythonMissingInclude, diagnostic.Code);
        Assert.Contains("IncludeGlobs", diagnostic.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ReportsUnavailableNativeParserAsAStrictHealthError()
    {
        await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    \"\"\"Run.\"\"\"\n");
        var options = CreateEnabledOptions("worker.py");
        options.Harvest.Python.StrictHealth = true;
        var harvester = new PythonDocHarvester(
            options,
            NullLogger<PythonDocHarvester>.Instance,
            new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance),
            static () => throw new DllNotFoundException("Tree-sitter native asset is unavailable."));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));
        Assert.Equal(DocHarvestDiagnosticCodes.PythonParserUnavailable, diagnostic.Code);
        Assert.Equal(DocHarvestDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains(nameof(DllNotFoundException), diagnostic.Cause, StringComparison.Ordinal);
        Assert.True(((IDocHarvesterHealthParticipation)harvester).ParticipatesInStrictHealth);
    }

    [Fact]
    public async Task HarvestAsync_ReportsUnavailableNativeParserWhenFixedPreflightReturnsNull()
    {
        var options = CreateEnabledOptions("worker.py");
        var harvester = new PythonDocHarvester(
            options,
            NullLogger<PythonDocHarvester>.Instance,
            new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance),
            static () => new Language("Python"),
            static (_, _) => null);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));
        Assert.Equal(DocHarvestDiagnosticCodes.PythonParserUnavailable, diagnostic.Code);
        Assert.Contains("could not parse the fixed preflight source", diagnostic.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_DoesNotOverwriteNewerDiagnosticsWhenAnEarlierParserRunFinishesLate()
    {
        var firstParseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstParse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateEnabledOptions("*.py");
        var pathPolicy = new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance);
        var harvester = new PythonDocHarvester(
            options,
            NullLogger<PythonDocHarvester>.Instance,
            pathPolicy,
            static () => new Language("Python"),
            (parser, source) =>
            {
                if (source.Contains("first parser failure", StringComparison.Ordinal))
                {
                    firstParseStarted.TrySetResult();
                    releaseFirstParse.Task.GetAwaiter().GetResult();
                    throw new InvalidOperationException("first parser failure");
                }

                if (source.Contains("second parser failure", StringComparison.Ordinal))
                {
                    throw new NotSupportedException("second parser failure");
                }

                return parser.Parse(source);
            });
        var firstPath = await WriteAsync(
            "first.py",
            """"
            __all__ = ["run"]

            def run():
                """first parser failure"""
            """");
        var secondPath = await WriteAsync(
            "second.py",
            """"
            __all__ = ["run"]

            def run():
                """second parser failure"""
            """");
        // A small source read may complete synchronously, so keep the deliberately blocked parser
        // off the test's control flow to ensure the release in finally is always reachable.
        var firstRun = Task.Run(() => harvester.HarvestAsync(
            new DocHarvestContext(_testRoot, new ListedCandidatePathPolicy(firstPath))));

        try
        {
            await firstParseStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var secondDocs = await harvester.HarvestAsync(
                new DocHarvestContext(_testRoot, new ListedCandidatePathPolicy(secondPath)));

            Assert.Empty(secondDocs);
            Assert.Contains(
                GetDiagnostics(harvester),
                diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonParseFailed
                              && diagnostic.Cause.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
        }
        finally
        {
            releaseFirstParse.TrySetResult();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Contains(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonParseFailed
                          && diagnostic.Cause.Contains(nameof(NotSupportedException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task HarvestAsync_PublishesLiteralExportsAndTheirDocumentedMethods_WithoutExecutingPython()
    {
        await WriteAsync(
            "sidecar/worker.py",
            """"
            """Worker module.

                A static Python sidecar.
            """
            __all__ = ["Worker", "deliver"]

            class Worker:
                """Runs one work item.

                    The indentation is normalized.
                """

                async def run(self):
                    """Runs asynchronously."""

                def internal_method(self):
                    """This member remains below its exported class."""

            async def deliver():
                """Delivers work."""

            def internal_helper():
                """Must not be published."""
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("sidecar/**/*.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/sidecar-worker");
        Assert.Equal("sidecar.worker Python API", module.Title);
        Assert.Contains("Worker module.<br /><br />A static Python sidecar.", module.Content, StringComparison.Ordinal);
        Assert.Contains("Runs one work item.<br /><br />The indentation is normalized.", module.Content, StringComparison.Ordinal);
        Assert.Contains(docs, document => document.Path == "api/python/sidecar-worker#class-worker");
        Assert.Contains(docs, document => document.Path == "api/python/sidecar-worker#async-function-deliver");
        Assert.Contains(docs, document => document.Path == "api/python/sidecar-worker#async-method-class-worker-run");
        Assert.Contains(docs, document => document.Path == "api/python/sidecar-worker#method-class-worker-internal-method");
        Assert.DoesNotContain(docs, document => document.Title.Contains("internal_helper", StringComparison.Ordinal));
        Assert.Equal("python", module.Metadata?.CodeLanguage);
        Assert.Contains("<h2>sidecar.worker Python API</h2>", module.Content, StringComparison.Ordinal);
        Assert.Contains("<h3>Worker</h3>", module.Content, StringComparison.Ordinal);
        Assert.Contains("<h3>deliver</h3>", module.Content, StringComparison.Ordinal);
        Assert.Contains("<h4>run</h4>", module.Content, StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(module.Outline!, item => item.Id == "class-worker").Level);
        Assert.Equal(3, Assert.Single(module.Outline!, item => item.Id == "async-method-class-worker-run").Level);
        Assert.Equal("sidecar/worker.py", Assert.Single(module.SymbolSourceProvenance!, source => source.AnchorId == "class-worker").SourcePath);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_UsesLastPythonRedefinitionAndPublishesEachExportOnce()
    {
        await WriteAsync(
            "worker.py",
            """"
            __all__ = ["Worker", "Worker", "run", "run"]

            class Worker:
                """Stale worker."""

                def execute(self):
                    """Stale method."""

            class Worker:
                """Final worker."""

                def execute(self):
                    """Final method."""

            def run():
                """Stale run."""

            def run():
                """Final run."""
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("worker.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/worker");
        Assert.Contains("Final worker.", module.Content, StringComparison.Ordinal);
        Assert.Contains("Final method.", module.Content, StringComparison.Ordinal);
        Assert.Contains("Final run.", module.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale", module.Content, StringComparison.Ordinal);
        Assert.Single(docs, document => document.Path == "api/python/worker#class-worker");
        Assert.Single(docs, document => document.Path == "api/python/worker#method-class-worker-execute");
        Assert.Single(docs, document => document.Path == "api/python/worker#function-run");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_DropsAClassMemberWhenItsLastRedefinitionHasNoDocstring()
    {
        await WriteAsync(
            "worker.py",
            """"
            __all__ = ["Worker"]

            class Worker:
                """Worker."""

                def execute(self):
                    """Stale method."""

                def execute(self):
                    pass
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("worker.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/worker");
        Assert.Contains("Worker.", module.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale method.", module.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(docs, document => document.Path == "api/python/worker#method-class-worker-execute");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_PublishesCaseDistinctExportsWithStableUniqueFragments()
    {
        await WriteAsync(
            "worker.py",
            """"
            __all__ = ["Foo", "foo"]

            def Foo():
                """Uppercase symbol."""

            def foo():
                """Lowercase symbol."""
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("worker.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/worker");
        var uppercase = Assert.Single(docs, document => document.Title == "Foo");
        var lowercase = Assert.Single(docs, document => document.Title == "foo");
        Assert.Equal("api/python/worker#function-foo", uppercase.Path);
        Assert.Equal("api/python/worker#function-foo-666f6f", lowercase.Path);
        Assert.Contains("Uppercase symbol.", module.Content, StringComparison.Ordinal);
        Assert.Contains("Lowercase symbol.", module.Content, StringComparison.Ordinal);
        Assert.Contains("id=\"function-foo\"", module.Content, StringComparison.Ordinal);
        Assert.Contains("id=\"function-foo-666f6f\"", module.Content, StringComparison.Ordinal);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_KeepsCollisionSuffixesUniqueAcrossSymbolsAndClassMembers()
    {
        await WriteAsync(
            "worker.py",
            """"
            __all__ = ["foo_666f6f_2", "Foo", "foo", "foo_666f6f", "A_B", "A"]

            def foo_666f6f_2():
                """Reserves the first numeric suffix."""

            def Foo():
                """Uppercase symbol."""

            def foo():
                """Lowercase symbol."""

            def foo_666f6f():
                """Collides with the lowercase symbol suffix."""

            class A_B:
                def c(self):
                    """First member."""

            class A:
                def b_c(self):
                    """Second member with the same normalized qualified name."""
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("worker.py"));

        var docs = await harvester.HarvestAsync(_testRoot);
        var repeatedDocs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/worker");
        var symbols = docs.Where(document => document.ParentPath == module.Path).ToArray();
        Assert.Equal(8, symbols.Length);
        Assert.Equal(symbols.Length, symbols.Select(document => document.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(docs.Select(document => document.Path), repeatedDocs.Select(document => document.Path));
        Assert.Equal(module.Content, Assert.Single(repeatedDocs, document => document.Path == module.Path).Content);
        Assert.Equal("api/python/worker#function-foo-666f6f-3", Assert.Single(symbols, document => document.Title == "foo_666f6f").Path);
        Assert.Equal("api/python/worker#method-class-a-b-c-2", Assert.Single(symbols, document => document.Title == "A.b_c").Path);
        Assert.Equal(symbols.Length, module.Outline!.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(symbols.Length, module.SymbolSourceProvenance!.Select(item => item.AnchorId).Distinct(StringComparer.Ordinal).Count());
        foreach (var symbol in symbols)
        {
            var anchor = symbol.Path[(symbol.Path.IndexOf('#') + 1)..];
            Assert.Equal(2, module.Content.Split($"id=\"{anchor}\"", StringSplitOptions.None).Length);
        }

        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_UsesModuleFallbackForAnEmptyNormalizedSourceName()
    {
        await WriteAsync(
            "---.py",
            """"
            __all__ = ["run"]

            def run():
                """Runs."""
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("---.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, document => document.Path == "api/python/module");
        Assert.Contains(docs, document => document.Path == "api/python/module#function-run");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_RejectsMissingAndDynamicPublicBoundaries()
    {
        await WriteAsync("missing.py", "def visible():\n    \"\"\"Visible.\"\"\"\n");
        await WriteAsync("dynamic.py", "names = [\"visible\"]\n__all__ = names\ndef visible():\n    \"\"\"Visible.\"\"\"\n");
        var harvester = CreateHarvester(CreateEnabledOptions("*.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        var diagnostics = GetDiagnostics(harvester);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonPublicBoundaryMissing);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonPublicBoundaryInvalid);
    }

    [Fact]
    public async Task HarvestAsync_SkipsMalformedPythonBeforePublishingARecoveredTree()
    {
        await WriteAsync(
            "broken.py",
            """"
            __all__ = ["run"

            def run():
                """Would be published if the module parsed."""
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("broken.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        var diagnostic = Assert.Single(GetDiagnostics(harvester));
        Assert.Equal(DocHarvestDiagnosticCodes.PythonParseFailed, diagnostic.Code);
        Assert.Contains("broken.py", diagnostic.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_ReportsUnsupportedAndUnknownExportNames()
    {
        await WriteAsync(
            "exports.py",
            """"
            __all__ = ["constant", "missing", "run"]
            constant = "value"

            def run():
                """Run."""
            """");
        var harvester = CreateHarvester(CreateEnabledOptions("exports.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, document => document.Path == "api/python/exports#function-run");
        var diagnostics = GetDiagnostics(harvester);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonExportNotSupported);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonExportNotFound);
    }

    [Fact]
    public async Task HarvestAsync_IgnoresAnOtherwiseEmptyPackageInitializerWithoutBoundaryGuidance()
    {
        await WriteAsync("sidecar/__init__.py", "\"\"\"Package description.\"\"\"\n");
        var harvester = CreateHarvester(CreateEnabledOptions("sidecar/**/*.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ReportsOversizedPythonSourceBeforeParsing()
    {
        await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    \"\"\"Run.\"\"\"\n");
        var options = CreateEnabledOptions("worker.py");
        options.Harvest.Python.MaxFileSizeBytes = 10;
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Contains(GetDiagnostics(harvester), diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonFileTooLarge);
    }

    [Fact]
    public async Task HarvestAsync_UsesContextProgressForDecoratedTupleExports()
    {
        await WriteAsync(
            "sidecar/__init__.py",
            """
            ''' Sidecar module. '''
            __all__ = ("Worker", "deliver")

            @public_api
            class Worker:
                ''' Worker docs. '''

                @public_api
                async def run(self):
                    '''\tRuns with normalized indentation.\n\t  Extra detail. '''

            @public_api
            def deliver():
                '''Delivers work.'''
            """);
        var options = CreateEnabledOptions("sidecar/**/*.py");
        var harvester = CreateHarvester(options);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var reporter = new AppSurfaceDocsHarvestProgressReporter(
            provider,
            NullLogger<AppSurfaceDocsHarvestProgressReporter>.Instance);
        var progressId = nameof(PythonDocHarvester);
        var runId = await reporter.BeginRunAsync(
        [
            new AppSurfaceDocsHarvesterRegistration(
                progressId,
                nameof(PythonDocHarvester),
                IsBuiltInProgressHarvester: true)
        ]);
        var context = new DocHarvestContext(
            _testRoot,
            new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance),
            reporter.CreateSession(runId, progressId));

        var docs = await harvester.HarvestAsync(context);

        var module = Assert.Single(docs, document => document.Path == "api/python/sidecar");
        Assert.Contains("Sidecar module.", module.Content, StringComparison.Ordinal);
        Assert.Contains("Runs with normalized indentation.", module.Content, StringComparison.Ordinal);
        Assert.Contains("Extra detail.", module.Content, StringComparison.Ordinal);
        Assert.Contains(docs, document => document.Path == "api/python/sidecar#class-worker");
        Assert.Contains(docs, document => document.Path == "api/python/sidecar#async-method-class-worker-run");
        Assert.Contains(docs, document => document.Path == "api/python/sidecar#function-deliver");
        var progress = Assert.Single(reporter.CurrentSnapshot.Harvesters, item => item.ProgressId == progressId);
        Assert.Equal(AppSurfaceDocsHarvestProgressPhase.Finalizing, progress.Phase);
        Assert.Equal(1, progress.SourceUnitsProcessed);
        Assert.Equal(docs.Count, progress.DocCount);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_RejectsEveryUnsupportedLiteralBoundaryShape()
    {
        await WriteAsync("annotated.py", "__all__: list[str] = [\"run\"]\ndef run():\n    '''Run.'''\n");
        await WriteAsync("multiple.py", "__all__ = [\"run\"]\n__all__ = [\"run\"]\ndef run():\n    '''Run.'''\n");
        await WriteAsync("augmented.py", "__all__ = [\"run\"]\n__all__ += [\"other\"]\ndef run():\n    '''Run.'''\n");
        await WriteAsync("non-string.py", "__all__ = [\"run\", 1]\ndef run():\n    '''Run.'''\n");
        var harvester = CreateHarvester(CreateEnabledOptions("*.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Equal(
            4,
            GetDiagnostics(harvester).Count(diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonPublicBoundaryInvalid));
    }

    [Fact]
    public async Task HarvestAsync_DoesNotPublishExportsWithoutADocstringOrDocumentedMembers()
    {
        await WriteAsync(
            "undocumented.py",
            """
            __all__ = ["run", "Worker"]

            def run():
                pass

            class Worker:
                def work(self):
                    pass
            """);
        var harvester = CreateHarvester(CreateEnabledOptions("undocumented.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_HonorsPythonExcludesAndRejectsCollidingModuleRoutes()
    {
        await WriteAsync("included.py", "__all__ = [\"run\"]\ndef run():\n    '''Included.'''\n");
        await WriteAsync("excluded.py", "__all__ = [\"run\"]\ndef run():\n    '''Excluded.'''\n");
        await WriteAsync("sidecar/foo_bar.py", "__all__ = [\"run\"]\ndef run():\n    '''First collision.'''\n");
        await WriteAsync("sidecar/foo-bar.py", "__all__ = [\"run\"]\ndef run():\n    '''Second collision.'''\n");
        var options = CreateEnabledOptions("*.py", "sidecar/*.py");
        options.Harvest.Python.ExcludeGlobs = ["excluded.py"];
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, document => document.Path == "api/python/included#function-run");
        Assert.DoesNotContain(docs, document => document.Path.Contains("excluded", StringComparison.Ordinal));
        Assert.DoesNotContain(docs, document => document.Path == "api/python/sidecar-foo-bar");
        Assert.Equal(
            2,
            GetDiagnostics(harvester).Count(diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonSlugCollision));
    }

    [Fact]
    public async Task HarvestAsync_ReportsUnavailableNativeParserAsWarningWithoutStrictHealth()
    {
        await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    '''Run.'''\n");
        var options = CreateEnabledOptions("worker.py");
        var harvester = new PythonDocHarvester(
            options,
            NullLogger<PythonDocHarvester>.Instance,
            new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance),
            static () => throw new DllNotFoundException("Tree-sitter native asset is unavailable."));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Equal(DocHarvestDiagnosticSeverity.Warning, Assert.Single(GetDiagnostics(harvester)).Severity);
    }

    [Fact]
    public async Task HarvestAsync_PropagatesCancellationAfterAPathPolicyAdmitsACandidate()
    {
        var workerPath = await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    '''Run.'''\n");
        var options = CreateEnabledOptions("worker.py");
        var harvester = CreateHarvester(options);
        using var cancellation = new CancellationTokenSource();
        var context = new DocHarvestContext(
            _testRoot,
            new CancellingCandidatePathPolicy(workerPath, cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harvester.HarvestAsync(context, cancellation.Token));
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_ContinuesAfterAnUnreadableCandidate()
    {
        var unreadableCandidate = Path.Join(_testRoot, "unreadable.py");
        Directory.CreateDirectory(unreadableCandidate);
        var workerPath = await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    '''Run.'''\n");
        var options = CreateEnabledOptions("*.py");
        var harvester = CreateHarvester(options);
        var context = new DocHarvestContext(
            _testRoot,
            new ListedCandidatePathPolicy(unreadableCandidate, workerPath));

        var docs = await harvester.HarvestAsync(context);

        Assert.Contains(docs, document => document.Path == "api/python/worker#function-run");
        var diagnostic = Assert.Single(GetDiagnostics(harvester));
        Assert.Equal(DocHarvestDiagnosticCodes.PythonParseFailed, diagnostic.Code);
        Assert.Contains("could not be read", diagnostic.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarvestAsync_NormalizesActualTabAndCarriageReturnDocstringsWithoutPublishingBinaryStrings()
    {
        await WriteAsync(
            "worker.py",
            "'''  Worker module.  '''\n"
            + "__all__ = [\"run\", \"binary\"]\n"
            + "def run():\n"
            + "    '''First line\r\n\tSecond line.'''\n"
            + "def binary():\n"
            + "    b'not a static docstring'\n");
        var harvester = CreateHarvester(CreateEnabledOptions("worker.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/worker");
        Assert.Contains("Worker module.", module.Content, StringComparison.Ordinal);
        Assert.Contains("First line<br />Second line.", module.Content, StringComparison.Ordinal);
        Assert.Contains(docs, document => document.Path == "api/python/worker#function-run");
        Assert.DoesNotContain(docs, document => document.Path == "api/python/worker#function-binary");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_SupportsEveryPlainStringDelimiterAndRetainsAnEmptyDeclaredDocstring()
    {
        await WriteAsync(
            "delimiters.py",
            "'''Delimiter module.'''\n"
            + "__all__ = ['single', \"double\", 'empty']\n"
            + "def single():\n"
            + "    'Single quoted.'\n"
            + "def double():\n"
            + "    \"Double quoted.\"\n"
            + "def empty():\n"
            + "    ''\n");
        var harvester = CreateHarvester(CreateEnabledOptions("delimiters.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/delimiters");
        Assert.Contains("Single quoted.", module.Content, StringComparison.Ordinal);
        Assert.Contains("Double quoted.", module.Content, StringComparison.Ordinal);
        Assert.Contains(docs, document => document.Path == "api/python/delimiters#function-empty");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_NormalizesConfiguredIncludesAndPublishesADocumentedEmptyBoundary()
    {
        await WriteAsync(
            "sidecar/worker.py",
            "'''A module that intentionally exports no symbols.'''\n__all__ = ()\n");
        var options = CreateEnabledOptions(
            " ",
            "sidecar\\**\\*.py",
            "sidecar/**/*.py",
            "../outside.py",
            "/rooted.py");
        var harvester = CreateHarvester(options);

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs);
        Assert.Equal("api/python/sidecar-worker", module.Path);
        Assert.Contains("intentionally exports no symbols", module.Content, StringComparison.Ordinal);
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_PublishesDocumentedClassMembersWhenTheClassHasNoDocstring()
    {
        await WriteAsync(
            "worker.py",
            "'''Worker module.'''\n__all__ = [\"Worker\"]\nclass Worker:\n    def run(self):\n        '''Runs.'''\n");
        var harvester = CreateHarvester(CreateEnabledOptions("worker.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        var module = Assert.Single(docs, document => document.Path == "api/python/worker");
        Assert.DoesNotContain("Python Class</span><h2>Worker</h2><div class=\"doc-body\"><p>", module.Content, StringComparison.Ordinal);
        Assert.Contains("Runs.", module.Content, StringComparison.Ordinal);
        Assert.Contains(docs, document => document.Path == "api/python/worker#class-worker");
        Assert.Contains(docs, document => document.Path == "api/python/worker#method-class-worker-run");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_DoesNotTreatAnInterpolatedFirstStatementAsADocstring()
    {
        await WriteAsync(
            "worker.py",
            "'''Worker module.'''\n__all__ = [\"dynamic\"]\ndef dynamic():\n    f'Not a docstring: {1}'\n");
        var harvester = CreateHarvester(CreateEnabledOptions("worker.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Contains(docs, document => document.Path == "api/python/worker");
        Assert.DoesNotContain(docs, document => document.Path == "api/python/worker#function-dynamic");
        Assert.Empty(GetDiagnostics(harvester));
    }

    [Fact]
    public async Task HarvestAsync_RejectsLiteralBoundariesWhoseRightHandSideIsNotACollection()
    {
        await WriteAsync("invalid.py", "__all__ = 'run'\ndef run():\n    '''Run.'''\n");
        var harvester = CreateHarvester(CreateEnabledOptions("invalid.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Contains(
            GetDiagnostics(harvester),
            diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonPublicBoundaryInvalid);
    }

    [Fact]
    public async Task HarvestAsync_UsesFallbackPythonSettingsWhenConfiguredSettingsAreNull()
    {
        await WriteAsync("worker.py", "__all__ = [\"run\"]\ndef run():\n    '''Run.'''\n");
        var options = CreateEnabledOptions("worker.py");
        var harvester = CreateHarvester(options);
        options.Harvest.Python = null!;

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Equal(DocHarvestDiagnosticCodes.PythonMissingInclude, Assert.Single(GetDiagnostics(harvester)).Code);
        Assert.False(((IDocHarvesterActivation)harvester).IsEnabled);
        Assert.False(((IDocHarvesterHealthParticipation)harvester).ParticipatesInStrictHealth);
    }

    [Fact]
    public async Task HarvestAsync_RequiresAnActualDocstringForAnOtherwiseEmptyPackageInitializer()
    {
        await WriteAsync("sidecar/__init__.py", "f\"dynamic package description\"\n");
        var harvester = CreateHarvester(CreateEnabledOptions("sidecar/**/*.py"));

        var docs = await harvester.HarvestAsync(_testRoot);

        Assert.Empty(docs);
        Assert.Equal(
            DocHarvestDiagnosticCodes.PythonPublicBoundaryMissing,
            Assert.Single(GetDiagnostics(harvester)).Code);
    }

    [Fact]
    public void HarvesterContracts_ReflectConfiguredPythonActivationAndHealthParticipation()
    {
        var disabledOptions = new AppSurfaceDocsOptions();
        disabledOptions.Harvest.Python.Enabled = false;
        disabledOptions.Harvest.Python.IncludeGlobs = [];
        var disabled = new PythonDocHarvester(disabledOptions, NullLogger<PythonDocHarvester>.Instance);
        var enabledOptions = CreateEnabledOptions("worker.py");
        var enabled = CreateHarvester(enabledOptions);

        Assert.False(((IDocHarvesterActivation)disabled).IsEnabled);
        Assert.False(((IDocHarvesterHealthParticipation)disabled).ParticipatesInStrictHealth);
        Assert.True(((IDocHarvesterActivation)enabled).IsEnabled);
        Assert.True(((IDocHarvesterHealthParticipation)enabled).ParticipatesInStrictHealth);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("'Package docs.'", false)]
    [InlineData("r'Package docs.'", true)]
    [InlineData("b'Package docs.'", true)]
    [InlineData("1 + 2", true)]
    [InlineData("'Not', 'a docstring'", true)]
    [InlineData("'Package docs.'\nvalue = 1", true)]
    public async Task HarvestAsync_ValidatesRootPackageInitializerStatements(string source, bool requiresBoundary)
    {
        await WriteAsync("__init__.py", source);
        var harvester = CreateHarvester(CreateEnabledOptions("__init__.py"));

        Assert.Empty(await harvester.HarvestAsync(_testRoot));

        var diagnostics = GetDiagnostics(harvester);
        if (requiresBoundary)
        {
            Assert.Equal(DocHarvestDiagnosticCodes.PythonPublicBoundaryMissing, Assert.Single(diagnostics).Code);
        }
        else
        {
            Assert.Empty(diagnostics);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NormalizeOptions_RestoresNullPythonConfigurationWithoutBroadeningItsBoundary(bool missingPythonOptions)
    {
        var options = new AppSurfaceDocsOptions();
        if (missingPythonOptions)
        {
            options.Harvest.Python = null!;
        }
        else
        {
            options.Harvest.Python.DefaultExclusions = null!;
            options.Harvest.Python.IncludeGlobs = null!;
            options.Harvest.Python.ExcludeGlobs = null!;
        }

        // The path-policy constructor also tolerates incomplete, not-yet-normalized settings.
        var policy = new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance);
        Assert.True(policy.ShouldIncludeFilePath("worker.py", AppSurfaceDocsHarvestSourceKind.Python));
        AppSurfaceDocsServiceCollectionExtensions.NormalizeOptions(options);

        Assert.NotNull(options.Harvest.Python);
        Assert.NotNull(options.Harvest.Python.DefaultExclusions);
        Assert.Empty(options.Harvest.Python.IncludeGlobs);
        Assert.Empty(options.Harvest.Python.ExcludeGlobs);
        Assert.False(((IDocHarvesterHealthParticipation)CreateHarvester(options)).ParticipatesInStrictHealth);
    }

    [Fact]
    public async Task HarvestAsync_UsesDefaultsWhenEntireHarvestConfigurationIsMissing()
    {
        var options = new AppSurfaceDocsOptions { Harvest = null! };
        var harvester = CreateHarvester(options);

        Assert.Empty(await harvester.HarvestAsync(_testRoot));
        Assert.Equal(DocHarvestDiagnosticCodes.PythonMissingInclude, Assert.Single(GetDiagnostics(harvester)).Code);
        Assert.False(((IDocHarvesterActivation)harvester).IsEnabled);
        Assert.False(((IDocHarvesterHealthParticipation)harvester).ParticipatesInStrictHealth);
    }

    [Fact]
    public async Task GetDocsAsync_WithBuiltInPythonHarvesterAppliesVcsIgnoreSnapshotAndHealthDiagnostic()
    {
        await WriteAsync(".gitignore", "ignored/\n");
        await WriteAsync(
            "ignored/hidden.py",
            """"
            __all__ = ["hidden"]

            def hidden():
                """Hidden."""
            """");
        await WriteAsync(
            "visible.py",
            """"
            __all__ = ["visible"]

            def visible():
                """Visible."""
            """");
        var options = CreateEnabledOptions("visible.py", "ignored/hidden.py");
        options.Source.RepositoryRoot = _testRoot;
        options.Contributor.Enabled = false;
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(_testRoot);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aggregator = new DocAggregator(
            [CreateHarvester(options)],
            options,
            environment,
            new Memo(cache),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var docs = await aggregator.GetDocsAsync();
        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Contains(docs, document => document.Path == "api/python/visible");
        Assert.DoesNotContain(docs, document => document.Path == "api/python/ignored-hidden");
        Assert.Contains(health.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.VcsIgnoreSummary);
    }

    [Fact]
    public async Task GetHarvestHealthAsync_CountsStrictPythonParserFailures()
    {
        var options = CreateEnabledOptions("worker.py");
        options.Source.RepositoryRoot = _testRoot;
        options.Harvest.Python.StrictHealth = true;
        options.Contributor.Enabled = false;
        var harvester = new PythonDocHarvester(
            options,
            NullLogger<PythonDocHarvester>.Instance,
            new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance),
            static () => throw new DllNotFoundException("Tree-sitter native asset is unavailable."));
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(_testRoot);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var aggregator = new DocAggregator(
            [harvester],
            options,
            environment,
            new Memo(cache),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var health = await aggregator.GetHarvestHealthAsync();

        Assert.Equal(DocHarvestHealthStatus.Failed, health.Status);
        Assert.Equal(1, health.FailedHarvesters);
        Assert.Contains(
            health.Harvesters,
            item => item.HarvesterType == nameof(PythonDocHarvester)
                    && item.Status == DocHarvesterHealthStatus.Failed);
        Assert.Contains(health.Diagnostics, diagnostic => diagnostic.Code == DocHarvestDiagnosticCodes.PythonParserUnavailable);
    }

    [Fact]
    public async Task GetSearchIndexPayloadAsync_ProjectsPythonSymbolsAsGeneratedApiEntries()
    {
        await WriteAsync(
            "sidecar/worker.py",
            """"
            __all__ = ["deliver"]

            def deliver():
                """Delivers a work item."""
            """");
        var options = CreateEnabledOptions("sidecar/**/*.py");
        options.Source.RepositoryRoot = _testRoot;
        options.Contributor.Enabled = false;
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(_testRoot);
        var aggregator = new DocAggregator(
            [CreateHarvester(options)],
            options,
            environment,
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var payload = await aggregator.GetSearchIndexPayloadAsync();

        var symbol = Assert.Single(payload.Documents, document => document.Title == "deliver");
        Assert.Equal("/docs/api/python/sidecar-worker#function-deliver", symbol.Path);
        Assert.Equal("python-function", symbol.PageType);
        Assert.Equal("python", symbol.Language);
        Assert.Equal("Python", symbol.LanguageLabel);
        Assert.Equal("public", symbol.ApiLifecycle);
        Assert.Equal("Public API", symbol.ApiLifecycleLabel);
        Assert.False(symbol.IsDeprecated);
        Assert.True(symbol.IsGeneratedApiSymbol);
        Assert.Contains("Delivers a work item.", symbol.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDocsAsync_LinksAcceptedPythonModuleToItsDocumentedCSharpHost()
    {
        await WriteAsync(
            "sidecar/worker.py",
            """"
            __all__ = ["deliver"]

            def deliver():
                """Delivers a work item."""
            """");
        await WriteAsync(
            "Host.cs",
            """"
            using ForgeTrust.AppSurface.Docs;

            namespace Sample.Host;

            /// <summary>Hosts the worker sidecar.</summary>
            [AppSurfacePythonModule("sidecar/worker.py")]
            public sealed class WorkerHost;
            """");
        var options = CreateEnabledOptions("sidecar/**/*.py");
        options.Source.RepositoryRoot = _testRoot;
        options.Contributor.Enabled = false;
        var environment = A.Fake<IWebHostEnvironment>();
        A.CallTo(() => environment.ContentRootPath).Returns(_testRoot);
        var aggregator = new DocAggregator(
            [
                new CSharpDocHarvester(options, NullLogger<CSharpDocHarvester>.Instance),
                CreateHarvester(options)
            ],
            options,
            environment,
            new Memo(new MemoryCache(new MemoryCacheOptions())),
            new AppSurfaceDocsHtmlSanitizer(),
            NullLogger<DocAggregator>.Instance);

        var docs = await aggregator.GetDocsAsync();

        var pythonModule = Assert.Single(docs, document => document.Path == "api/python/sidecar-worker");
        var csharpHost = Assert.Single(docs, document => document.Path == "Namespaces/Sample.Host");
        Assert.Contains("C# host:", pythonModule.Content, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/Namespaces/Sample.Host.html#Sample-Host-WorkerHost\"", pythonModule.Content, StringComparison.Ordinal);
        Assert.Contains(">WorkerHost</a>", pythonModule.Content, StringComparison.Ordinal);
        Assert.Contains("Python module:", csharpHost.Content, StringComparison.Ordinal);
        Assert.Contains("href=\"/docs/api/python/sidecar-worker\"", csharpHost.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", pythonModule.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("data-appsurfacedocs-python-", csharpHost.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSurfaceDocsOptionsValidator_RejectsNonPositivePythonInputBudget()
    {
        var options = CreateEnabledOptions("worker.py");
        options.Harvest.Python.MaxFileSizeBytes = 0;

        var validation = new AppSurfaceDocsOptionsValidator().Validate(null, options);

        Assert.False(validation.Succeeded);
        Assert.Contains("AppSurfaceDocs:Harvest:Python:MaxFileSizeBytes must be greater than zero.", validation.Failures!);
    }

    [Fact]
    public void AppSurfaceDocsOptionsValidator_RejectsNullPythonSettings()
    {
        var options = CreateEnabledOptions("worker.py");
        options.Harvest.Python = null!;

        var validation = new AppSurfaceDocsOptionsValidator().Validate(null, options);

        Assert.False(validation.Succeeded);
        Assert.Contains("AppSurfaceDocs:Harvest:Python must not be null.", validation.Failures!);
    }

    [Fact]
    public void AppSurfacePythonModuleAttribute_RetainsTheDeclaredModulePath()
    {
        var attribute = new AppSurfacePythonModuleAttribute("sidecar/worker.py");

        Assert.Equal("sidecar/worker.py", attribute.ModulePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static AppSurfaceDocsOptions CreateEnabledOptions(params string[] includeGlobs)
    {
        var options = new AppSurfaceDocsOptions();
        options.Harvest.Python.IncludeGlobs = includeGlobs;
        return options;
    }

    private static PythonDocHarvester CreateHarvester(AppSurfaceDocsOptions options) =>
        new(options, NullLogger<PythonDocHarvester>.Instance);

    private static IReadOnlyList<DocHarvestDiagnostic> GetDiagnostics(PythonDocHarvester harvester) =>
        ((IDocHarvesterDiagnosticProvider)harvester).GetHarvestDiagnostics();

    private async Task<string> WriteAsync(string relativePath, string content)
    {
        var path = TestPathUtils.PathUnder(_testRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private sealed class CancellingCandidatePathPolicy(string workerPath, CancellationTokenSource cancellation) : IHarvestPathPolicy
    {
        public AppSurfaceDocsHarvestPathDecision Evaluate(
            string relativePath,
            AppSurfaceDocsHarvestSourceKind sourceKind)
        {
            var included = ShouldIncludeFilePath(relativePath, sourceKind);
            return new AppSurfaceDocsHarvestPathDecision(
                included,
                relativePath,
                sourceKind,
                AppSurfaceDocsHarvestPathDecisionCode.IncludedByGlobalInclude,
                [],
                []);
        }

        public bool ShouldIncludeFilePath(string relativePath, AppSurfaceDocsHarvestSourceKind sourceKind)
        {
            cancellation.Cancel();
            return true;
        }

        public bool ShouldPruneDirectory(string relativeDirectory, AppSurfaceDocsHarvestSourceKind sourceKind) => false;

        public IEnumerable<string> EnumerateCandidateFiles(
            string rootPath,
            AppSurfaceDocsHarvestSourceKind sourceKind,
            string searchPattern,
            CancellationToken cancellationToken)
        {
            yield return workerPath;
        }
    }

    private sealed class ListedCandidatePathPolicy(params string[] candidatePaths) : IHarvestPathPolicy
    {
        public AppSurfaceDocsHarvestPathDecision Evaluate(
            string relativePath,
            AppSurfaceDocsHarvestSourceKind sourceKind) =>
            new(
                ShouldIncludeFilePath(relativePath, sourceKind),
                relativePath,
                sourceKind,
                AppSurfaceDocsHarvestPathDecisionCode.IncludedByGlobalInclude,
                [],
                []);

        public bool ShouldIncludeFilePath(string relativePath, AppSurfaceDocsHarvestSourceKind sourceKind) => true;

        public bool ShouldPruneDirectory(string relativeDirectory, AppSurfaceDocsHarvestSourceKind sourceKind) => false;

        public IEnumerable<string> EnumerateCandidateFiles(
            string rootPath,
            AppSurfaceDocsHarvestSourceKind sourceKind,
            string searchPattern,
            CancellationToken cancellationToken) => candidatePaths;
    }
}
