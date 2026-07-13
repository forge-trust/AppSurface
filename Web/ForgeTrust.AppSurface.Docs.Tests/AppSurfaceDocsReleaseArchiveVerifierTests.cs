using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace ForgeTrust.AppSurface.Docs.Tests;

[Trait("Category", "Unit")]
public sealed class AppSurfaceDocsReleaseArchiveVerifierTests : IDisposable
{
    private static readonly string EmptySha256 = ComputeBytesSha256([]);

    private readonly string _tempDirectory;

    public AppSurfaceDocsReleaseArchiveVerifierTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "appsurfacedocs-release-archive-verifier-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void TryVerify_ShouldPass_WhenManifestDigestAndFilesMatch()
    {
        var index = WriteFile("index.html", "<html>ok</html>", "text/html");
        var svg = WriteFile("assets/logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "image/svg+xml");
        var routeManifest = WriteFile(
            AppSurfaceDocsFrozenRouteManifest.FileName,
            """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": [
                {
                  "sourcePath": "guide.md",
                  "canonicalRoutePath": "guide",
                  "recoveryAliases": [ "legacy-guide" ],
                  "declaredAliases": []
                }
              ]
            }
            """,
            "application/json");
        var digest = WriteManifest(index, routeManifest, svg);

        var verified = AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            _tempDirectory,
            digest.ToUpperInvariant(),
            out var archive,
            out var failure);

        Assert.True(verified, failure?.Detail);
        Assert.Null(failure);
        Assert.NotNull(archive);
        Assert.Equal(3, archive.FileCount);
        Assert.True(archive.TryGetFile(Path.Join("assets", "logo.svg"), out var file));
        Assert.Equal("image/svg+xml", file.ContentType);
        Assert.True(archive.FrozenRouteManifest.TryResolveAlias("legacy-guide", out var canonicalRoutePath));
        Assert.Equal("guide", canonicalRoutePath);
    }

    [Theory]
    [InlineData("not-a-sha")]
    [InlineData("abc")]
    public void TryVerify_ShouldFail_WhenCatalogDigestIsInvalid(string digest)
    {
        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE002", failure.Code);
        Assert.Contains("digest is invalid", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenManifestIsMissing()
    {
        var failure = VerifyFailure(new string('0', 64));

        Assert.Equal("ASDOCSARCHIVE001", failure.Code);
        Assert.Contains("missing", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenManifestDigestDoesNotMatchCatalogPin()
    {
        WriteManifest();

        var failure = VerifyFailure(new string('f', 64));

        Assert.Equal("ASDOCSARCHIVE002", failure.Code);
        Assert.Contains("does not match", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenManifestJsonIsMalformed()
    {
        var digest = WriteRawManifest("{");

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE003", failure.Code);
        Assert.Contains("unreadable", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenManifestCannotBeRead()
    {
        var digest = WriteManifest();
        var manifestPath = TestPathUtils.PathUnder(_tempDirectory, AppSurfaceDocsReleaseArchiveVerifier.FileName);
        var fileSystem = ThrowingReleaseArchiveFileSystem.ThrowWhenReading(manifestPath);

        var failure = VerifyFailure(digest, fileSystem);

        Assert.Equal("ASDOCSARCHIVE001", failure.Code);
        Assert.Contains("could not be read", failure.PublicMessage);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "schema": "future", "files": [] }""")]
    public void TryVerify_ShouldFail_WhenManifestSchemaIsUnsupported(string json)
    {
        var digest = WriteRawManifest(json);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE003", failure.Code);
        Assert.Contains("schema is unsupported", failure.PublicMessage);
    }

    [Theory]
    [MemberData(nameof(InvalidEntryShapes))]
    public void TryVerify_ShouldFail_WhenFileEntryShapeIsInvalid(object? entry)
    {
        var digest = WriteManifest(entry);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE003", failure.Code);
        Assert.Contains("invalid file entry", failure.PublicMessage);
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("folder\\index.html")]
    [InlineData("c:index.html")]
    [InlineData("search?.html")]
    [InlineData("foo//bar.html")]
    [InlineData(".")]
    [InlineData("docs/../index.html")]
    [InlineData(".hidden")]
    public void TryVerify_ShouldFail_WhenManifestPathIsUnsafe(string path)
    {
        var digest = WriteManifest(Entry(path, length: 0, sha256: EmptySha256));

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE005", failure.Code);
        Assert.Contains("unsafe path", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenManifestContainsDuplicatePaths()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        var digest = WriteManifest(index, index);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE004", failure.Code);
        Assert.Contains("duplicate path", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenManifestListsMissingFile()
    {
        var digest = WriteManifest(Entry("missing.html", length: 0, sha256: EmptySha256));

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE006", failure.Code);
        Assert.Equal("missing.html", failure.Path);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenFileLengthDiffersFromManifest()
    {
        var index = WriteFile("index.html", "<html>ok</html>") with { Length = 999 };
        var digest = WriteManifest(index);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE007", failure.Code);
        Assert.Contains("length does not match", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenFileLengthCannotBeRead()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        var digest = WriteManifest(index);
        var filePath = Path.Join(_tempDirectory, "index.html");
        var fileSystem = ThrowingReleaseArchiveFileSystem.ThrowWhenReadingLength(filePath);

        var failure = VerifyFailure(digest, fileSystem);

        Assert.Equal("ASDOCSARCHIVE008", failure.Code);
        Assert.Equal("index.html", failure.Path);
        Assert.Contains("length verification", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenFileDigestDiffersFromManifest()
    {
        var index = WriteFile("index.html", "<html>ok</html>") with { Sha256 = new string('0', 64) };
        var digest = WriteManifest(index);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE008", failure.Code);
        Assert.Contains("digest does not match", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenFileCannotBeReadForDigestVerification()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        var digest = WriteManifest(index);
        var filePath = Path.Join(_tempDirectory, "index.html");
        var fileSystem = ThrowingReleaseArchiveFileSystem.ThrowWhenHashing(filePath);

        var failure = VerifyFailure(digest, fileSystem);

        Assert.Equal("ASDOCSARCHIVE008", failure.Code);
        Assert.Contains("could not be read", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenRouteManifestIsMalformed()
    {
        var routeManifest = WriteFile(AppSurfaceDocsFrozenRouteManifest.FileName, "{", "application/json");
        var digest = WriteManifest(routeManifest);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE003", failure.Code);
        Assert.Equal(AppSurfaceDocsFrozenRouteManifest.FileName, failure.Path);
        Assert.Contains("route manifest is malformed", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenRouteManifestExistsButIsNotListed()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        WriteFile(AppSurfaceDocsFrozenRouteManifest.FileName, ValidRouteManifestJson(), "application/json");
        var digest = WriteManifest(index);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE009", failure.Code);
        Assert.Equal(AppSurfaceDocsFrozenRouteManifest.FileName, failure.Path);
        Assert.Contains("not listed", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenServeableArchiveFileIsNotListed()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        WriteFile("search.css", "body { color: #fff; }", "text/css");
        var digest = WriteManifest(index);

        var failure = VerifyFailure(digest);

        Assert.Equal("ASDOCSARCHIVE009", failure.Code);
        Assert.Equal("search.css", failure.Path);
        Assert.Contains("serveable file", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenCaseVariantServeableArchiveFileIsNotListed()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        var digest = WriteManifest(index);
        var fileSystem = new ExtraEnumeratedFileSystem(
            TestPathUtils.PathUnder(_tempDirectory, "INDEX.HTML"),
            StringComparer.Ordinal);

        var failure = VerifyFailure(digest, fileSystem);

        Assert.Equal("ASDOCSARCHIVE009", failure.Code);
        Assert.Equal("INDEX.HTML", failure.Path);
        Assert.Contains("serveable file", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldPass_WhenCaseInsensitiveFilesystemReportsManifestPathWithDifferentCasing()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        var digest = WriteManifest(index);
        var fileSystem = new ExtraEnumeratedFileSystem(
            TestPathUtils.PathUnder(_tempDirectory, "INDEX.HTML"),
            StringComparer.OrdinalIgnoreCase);

        var verified = AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            _tempDirectory,
            digest,
            fileSystem,
            out var archive,
            out var failure);

        Assert.True(verified, failure?.Detail);
        Assert.Null(failure);
        Assert.NotNull(archive);
    }

    [Fact]
    public void ResolvePhysicalPathComparer_ShouldUseTheArchiveRootsFilesystemBehavior()
    {
        var caseInsensitive = AppSurfaceDocsReleaseArchiveFileSystem.ResolvePhysicalPathComparer(
            "/release/archive",
            _ => true);
        var caseSensitive = AppSurfaceDocsReleaseArchiveFileSystem.ResolvePhysicalPathComparer(
            "/release/archive",
            _ => false);
        var uppercaseRoot = AppSurfaceDocsReleaseArchiveFileSystem.ResolvePhysicalPathComparer(
            "/RELEASE/ARCHIVE",
            _ => false);
        var numericRoot = AppSurfaceDocsReleaseArchiveFileSystem.ResolvePhysicalPathComparer(
            "/123/456",
            _ => throw new InvalidOperationException("A root without letters must not probe another path."));

        Assert.Same(StringComparer.OrdinalIgnoreCase, caseInsensitive);
        Assert.Same(StringComparer.Ordinal, caseSensitive);
        Assert.Same(StringComparer.Ordinal, uppercaseRoot);
        Assert.Same(StringComparer.Ordinal, numericRoot);
    }

    [Fact]
    public void TryVerify_ShouldFail_WhenArchiveFilesCannotBeEnumerated()
    {
        var digest = WriteManifest();
        var fileSystem = ThrowingReleaseArchiveFileSystem.ThrowWhenEnumerating();

        var failure = VerifyFailure(digest, fileSystem);

        Assert.Equal("ASDOCSARCHIVE009", failure.Code);
        Assert.Contains("could not be enumerated", failure.PublicMessage);
    }

    [Fact]
    public void TryVerify_ShouldIgnoreUnlistedFilesThatHandlerWillNotServe()
    {
        var index = WriteFile("index.html", "<html>ok</html>");
        WriteFile("notes.txt", "not serveable");
        var digest = WriteManifest(index);

        var verified = AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            _tempDirectory,
            digest,
            out var archive,
            out var failure);

        Assert.True(verified, failure?.Detail);
        Assert.NotNull(archive);
        Assert.Equal(1, archive.FileCount);
    }

    [Fact]
    public void TryVerify_ShouldAllowManifestWithoutFiles_WhenTreeContainsOnlyManifest()
    {
        var digest = WriteRawManifest($$"""{ "schema": "{{AppSurfaceDocsReleaseArchiveVerifier.Schema}}" }""");

        var verified = AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
            _tempDirectory,
            digest,
            out var archive,
            out var failure);

        Assert.True(verified, failure?.Detail);
        Assert.NotNull(archive);
        Assert.Equal(0, archive.FileCount);
    }

    [Fact]
    public void FileMatches_ShouldCompareFileInfoAgainstVerifiedMetadata()
    {
        var svg = WriteFile("logo.svg", "<svg></svg>", "image/svg+xml");
        using var provider = new PhysicalFileProvider(_tempDirectory, ExclusionFilters.None);
        var fileInfo = provider.GetFileInfo("logo.svg");

        Assert.True(AppSurfaceDocsReleaseArchiveVerifier.FileMatches(fileInfo, svg));
        Assert.False(AppSurfaceDocsReleaseArchiveVerifier.FileMatches(new NotFoundFileInfo("missing.svg"), svg));
        Assert.False(AppSurfaceDocsReleaseArchiveVerifier.FileMatches(fileInfo, svg with { Length = svg.Length + 1 }));
        Assert.False(AppSurfaceDocsReleaseArchiveVerifier.FileMatches(fileInfo, svg with { Sha256 = new string('0', 64) }));
        Assert.False(AppSurfaceDocsReleaseArchiveVerifier.FileMatches(new ThrowingFileInfo(fileInfo.Length), svg));
    }

    [Fact]
    public void VerifiedReleaseArchiveConstructor_ShouldRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsVerifiedReleaseArchive(null!, AppSurfaceDocsFrozenRouteManifest.Empty));
        Assert.Throws<ArgumentNullException>(
            () => new AppSurfaceDocsVerifiedReleaseArchive(
                new Dictionary<string, AppSurfaceDocsReleaseArchiveFile>(StringComparer.OrdinalIgnoreCase),
                null!));
    }

    public static TheoryData<object?> InvalidEntryShapes()
    {
        return new TheoryData<object?>
        {
            null,
            Entry("   ", length: 0, sha256: EmptySha256),
            Entry("index.html", length: -1, sha256: EmptySha256),
            Entry("index.html", length: 0, hashAlgorithm: "sha1", sha256: EmptySha256),
            Entry("index.html", length: 0, sha256: "   "),
            Entry("index.html", length: 0, sha256: "not-a-sha")
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private AppSurfaceDocsArchiveVerificationFailure VerifyFailure(
        string expectedManifestSha256,
        AppSurfaceDocsReleaseArchiveFileSystem? fileSystem = null)
    {
        AppSurfaceDocsVerifiedReleaseArchive? archive;
        AppSurfaceDocsArchiveVerificationFailure? failure;
        var verified = fileSystem is null
            ? AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
                _tempDirectory,
                expectedManifestSha256,
                out archive,
                out failure)
            : AppSurfaceDocsReleaseArchiveVerifier.TryVerify(
                _tempDirectory,
                expectedManifestSha256,
                fileSystem,
                out archive,
                out failure);

        Assert.False(verified);
        Assert.Null(archive);
        Assert.NotNull(failure);
        Assert.False(string.IsNullOrWhiteSpace(failure.Detail));
        return failure;
    }

    private AppSurfaceDocsReleaseArchiveFile WriteFile(string relativePath, string content, string? contentType = null)
    {
        var path = TestPathUtils.PathUnder(_tempDirectory, relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new AppSurfaceDocsReleaseArchiveFile(
            relativePath,
            new FileInfo(path).Length,
            contentType,
            ComputeFileSha256(path));
    }

    private string WriteManifest(params object?[] entries)
    {
        var jsonEntries = entries.Select(
            entry => entry is AppSurfaceDocsReleaseArchiveFile file
                ? Entry(file.Path, file.Length, file.ContentType, "sha256", file.Sha256)
                : entry);
        return WriteRawManifest(
            JsonSerializer.Serialize(
                new { schema = AppSurfaceDocsReleaseArchiveVerifier.Schema, files = jsonEntries },
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private string WriteRawManifest(string json)
    {
        var path = TestPathUtils.PathUnder(_tempDirectory, AppSurfaceDocsReleaseArchiveVerifier.FileName);
        File.WriteAllText(path, json);
        return ComputeFileSha256(path);
    }

    private static object Entry(
        string path,
        long length,
        string? contentType = null,
        string hashAlgorithm = "sha256",
        string? sha256 = null)
    {
        return new
        {
            path,
            length,
            contentType,
            hashAlgorithm,
            sha256 = sha256 ?? EmptySha256
        };
    }

    private static string ValidRouteManifestJson()
    {
        return """
            {
              "schema": "appsurface-docs-route-manifest-v1",
              "entries": []
            }
            """;
    }

    private static string ComputeFileSha256(string path)
    {
        return ComputeBytesSha256(File.ReadAllBytes(path));
    }

    private static string ComputeBytesSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class ThrowingReleaseArchiveFileSystem : AppSurfaceDocsReleaseArchiveFileSystem
    {
        private readonly string? _readFailurePath;
        private readonly string? _hashFailurePath;
        private readonly string? _lengthFailurePath;
        private readonly bool _throwOnEnumerate;

        private ThrowingReleaseArchiveFileSystem(
            string? readFailurePath = null,
            string? hashFailurePath = null,
            string? lengthFailurePath = null,
            bool throwOnEnumerate = false)
        {
            _readFailurePath = readFailurePath;
            _hashFailurePath = hashFailurePath;
            _lengthFailurePath = lengthFailurePath;
            _throwOnEnumerate = throwOnEnumerate;
        }

        internal static ThrowingReleaseArchiveFileSystem ThrowWhenReading(string path)
        {
            return new ThrowingReleaseArchiveFileSystem(readFailurePath: path);
        }

        internal static ThrowingReleaseArchiveFileSystem ThrowWhenHashing(string path)
        {
            return new ThrowingReleaseArchiveFileSystem(hashFailurePath: path);
        }

        internal static ThrowingReleaseArchiveFileSystem ThrowWhenReadingLength(string path)
        {
            return new ThrowingReleaseArchiveFileSystem(lengthFailurePath: path);
        }

        internal static ThrowingReleaseArchiveFileSystem ThrowWhenEnumerating()
        {
            return new ThrowingReleaseArchiveFileSystem(throwOnEnumerate: true);
        }

        internal override bool FileExists(string path)
        {
            return File.Exists(path);
        }

        internal override byte[] ReadAllBytes(string path)
        {
            if (string.Equals(path, _readFailurePath, StringComparison.Ordinal))
            {
                throw new IOException("test read failure");
            }

            return File.ReadAllBytes(path);
        }

        internal override long GetLength(string path)
        {
            if (string.Equals(path, _lengthFailurePath, StringComparison.Ordinal))
            {
                throw new IOException("test length failure");
            }

            return new FileInfo(path).Length;
        }

        internal override string ComputeSha256(string path)
        {
            if (string.Equals(path, _hashFailurePath, StringComparison.Ordinal))
            {
                throw new IOException("test hash failure");
            }

            return ComputeFileSha256(path);
        }

        internal override IEnumerable<string> EnumerateFiles(string rootPath)
        {
            if (_throwOnEnumerate)
            {
                throw new IOException("test enumerate failure");
            }

            return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories);
        }
    }

    private sealed class ExtraEnumeratedFileSystem(string extraPath, StringComparer pathComparer) : AppSurfaceDocsReleaseArchiveFileSystem
    {
        internal override StringComparer GetPathComparer(string rootPath)
        {
            return pathComparer;
        }

        internal override bool FileExists(string path)
        {
            return File.Exists(path);
        }

        internal override byte[] ReadAllBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        internal override long GetLength(string path)
        {
            return new FileInfo(path).Length;
        }

        internal override string ComputeSha256(string path)
        {
            return ComputeFileSha256(path);
        }

        internal override IEnumerable<string> EnumerateFiles(string rootPath)
        {
            return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).Append(extraPath);
        }
    }

    private sealed class ThrowingFileInfo(long length) : IFileInfo
    {
        public bool Exists => true;

        public long Length => length;

        public string? PhysicalPath => null;

        public string Name => "throwing.svg";

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public bool IsDirectory => false;

        public Stream CreateReadStream()
        {
            throw new IOException("test stream failure");
        }
    }
}
