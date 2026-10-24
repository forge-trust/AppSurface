using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Auth.Keycloak;

namespace ForgeTrust.AppSurface.Auth.Keycloak.Tests;

public sealed class RuntimeConformanceTests
{
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [Fact]
    public void Verify_accepts_profile_packages_signed_payloads_policy_conffiles_and_links()
    {
        var fixture = RuntimeFixture.Create();

        var result = RuntimeConformance.Verify(
            fixture.Baseline,
            fixture.Actual,
            [fixture.RequiredPackage],
            [fixture.RemovedPackage],
            RuntimeConformance.ReadPolicy(fixture.Baseline, fixture.Actual));

        Assert.Equal(2, result.Packages.Length);
        Assert.Contains(result.Packages, x => x.Package == "base-files" && x.Disposition == "preserved");
        Assert.Contains(result.Packages, x => x.Package == "runtime-core" && x.Disposition == "required");
        Assert.Contains(result.Payloads, x => x.Path == "usr/bin/runtime-core" && x.Disposition == "present");
        Assert.Contains(result.Payloads, x => x.Path == "usr/share/doc/excluded.txt" && x.Disposition == "excluded-by-dpkg-policy");
        Assert.Contains(result.Payloads, x => x.Path == "usr/share/doc/kept.txt" && x.Disposition == "present");
        Assert.Contains(result.Files, x => x.Path == "usr/bin/java" && x.Type == "symlink" && x.LinkTarget == "/opt/java/bin/java");
        Assert.Contains(result.Files, x => x.Path == "usr/lib/runtime-core.hardlink" && x.Type == "hardlink");
        Assert.Equal(Hash(fixture.StatusBaseline), result.BaselineStatusSha256);
        Assert.Equal(Hash(fixture.StatusActual), result.ActualStatusSha256);
    }

    [Fact]
    public void ReadPolicy_rejects_changed_and_unknown_directives()
    {
        var fixture = RuntimeFixture.Create();
        var changed = fixture.WithPolicy("path-exclude=usr/share/doc/*\npath-include=usr/share/doc/other.txt\n");
        var changedError = Assert.Throws<InvalidDataException>(() => RuntimeConformance.ReadPolicy(fixture.Baseline, changed.Actual));
        Assert.Equal("Captured dpkg path policy changed.", changedError.Message);

        var unknown = fixture.WithPolicy("path-maybe=usr/share/doc/*\n");
        var unknownError = Assert.Throws<InvalidDataException>(() => RuntimeConformance.ReadPolicy(unknown.Baseline, unknown.Actual));
        Assert.Equal("Unknown dpkg path policy directive.", unknownError.Message);
    }

    [Fact]
    public void Verify_rejects_nonroot_home_passwd_and_certificate_profile_failures()
    {
        var fixture = RuntimeFixture.Create();
        AssertFailure(fixture with { Actual = fixture.Actual with { ConfigUser = "0:0" } }, "fixed non-root profile");
        AssertFailure(fixture with { Actual = fixture.Without("opt/keycloak").Actual }, "Runtime home");
        AssertFailure(fixture with { Actual = fixture.Without("etc/passwd").Actual }, "passwd payload");
        AssertFailure(fixture with { Actual = fixture.WithPasswd("ubuntu:x:1000:0::/home/ubuntu:/bin/bash\n").Actual }, "Keycloak account identity is not proven by passwd payload.");
        AssertFailure(fixture with { Actual = fixture.Without("etc/ssl/certs/ca-certificates.crt").Actual }, "certificate store");
        AssertFailure(fixture with { Actual = fixture.WithLink("etc/ssl/certs/ca-certificates.crt", "../../outside").Actual }, "certificate store");
        AssertFailure(fixture with { Actual = fixture.With("usr/bin/curl", "file").Actual }, "Forbidden runtime executable");
    }

    [Fact]
    public void Verify_rejects_required_removed_unexpected_and_unapproved_package_status()
    {
        var fixture = RuntimeFixture.Create();
        AssertFailure(fixture with { Actual = fixture.WithStatus(fixture.StatusActual.Replace("Package: runtime-core", "Package: missing-core")).Actual }, "Required package status");
        AssertFailure(fixture with { Actual = fixture.WithStatus(fixture.StatusActual + "\nPackage: curl\nVersion: 7\nArchitecture: amd64\nStatus: install ok installed\n").Actual }, "Removed package remains");
        AssertFailure(fixture with { Actual = fixture.WithStatus(fixture.StatusActual + "\nPackage: unexpected\nVersion: 1\nArchitecture: amd64\nStatus: install ok installed\n").Actual }, "Unexpected package");
        AssertFailure(fixture with { Actual = fixture.WithStatus(fixture.StatusActual.Replace("Package: base-files\nVersion: 1", "Package: base-files\nVersion: 2")).Actual }, "Unapproved baseline package");
    }

    [Fact]
    public void Verify_rejects_signed_payload_hash_mode_ownership_conffile_and_policy_deltas()
    {
        var fixture = RuntimeFixture.Create();
        AssertFailure(fixture with { Actual = fixture.With("usr/bin/runtime-core", "file", sha: Hash("wrong")).Actual }, "Signed regular payload");
        AssertFailure(fixture with { Actual = fixture.With("usr/bin/runtime-core", "file", mode: 420).Actual }, "ownership or mode");
        AssertFailure(fixture with { Actual = fixture.Without("etc/runtime.conf").Actual }, "Signed regular payload");
        AssertFailure(fixture with { Actual = fixture.With("usr/share/doc/excluded.txt", "file", sha: Hash("changed")).Actual }, "Excluded baseline file");
        var extraConffile = fixture with { RequiredPackage = fixture.RequiredPackage with { Conffiles = ["etc/missing.conf"] } };
        AssertFailure(extraConffile, "Signed conffile");
    }

    [Fact]
    public void Verify_rejects_invalid_runtime_paths_and_duplicate_paths()
    {
        var fixture = RuntimeFixture.Create();
        AssertFailure(fixture with { Actual = fixture.Actual with { Entries = fixture.Actual.Entries.Add(fixture.Actual.Entries[0] with { Path = "../escape" }) } }, "Unsafe runtime path");
        AssertFailure(fixture with { Actual = fixture.WithDuplicate("usr/bin/java").Actual }, "Duplicate final canonical path");
    }

    [Fact]
    public void Verify_rejects_ubuntu_passwd_and_certificate_symlink_loops()
    {
        var fixture = RuntimeFixture.Create();
        AssertFailure(fixture with { Actual = fixture.WithPasswd("keycloak:x:1000:0::/opt/keycloak:/usr/sbin/nologin\nubuntu:x:1000:0::/home/ubuntu:/bin/bash\n").Actual }, "Ubuntu account remains");
        AssertFailure(fixture with { Actual = fixture.WithLink("etc/ssl/certs/ca-certificates.crt", "etc/ssl/certs/ca-certificates.crt").Actual }, "certificate store");
    }

    private static void AssertFailure(RuntimeFixture fixture, string expectedMessage)
    {
        var error = Assert.Throws<InvalidDataException>(() => RuntimeConformance.Verify(
            fixture.Baseline,
            fixture.Actual,
            [fixture.RequiredPackage],
            [fixture.RemovedPackage],
            RuntimeConformance.ReadPolicy(fixture.Baseline, fixture.Actual)));
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    private sealed record RuntimeFixture(
        ObservedOciImage Baseline,
        ObservedOciImage Actual,
        DebPayloadReader.PackageResult RequiredPackage,
        KeycloakRemovedPackageSpec RemovedPackage,
        string StatusBaseline,
        string StatusActual,
        string Policy)
    {
        internal static RuntimeFixture Create()
        {
            const string policy = "path-exclude=usr/share/doc/*\npath-include=usr/share/doc/kept.txt\n";
            const string statusBaseline = "Package: base-files\nVersion: 1\nArchitecture: amd64\nStatus: install ok installed\n\nPackage: curl\nVersion: 7\nArchitecture: amd64\nStatus: install ok installed\n\nPackage: runtime-core\nVersion: 3\nArchitecture: amd64\nStatus: install ok installed\n";
            const string statusActual = "Package: base-files\nVersion: 1\nArchitecture: amd64\nStatus: install ok installed\n\nPackage: runtime-core\nVersion: 3\nArchitecture: amd64\nStatus: install ok installed\n";
            var packageFiles = ImmutableArray.Create(
                new DebPayloadReader.FileRecord("usr/bin/runtime-core", "file", 7, Hash("runtime"), 493, 1000, 0, null),
                new DebPayloadReader.FileRecord("usr/share/doc/excluded.txt", "file", 8, Hash("excluded"), 420, 0, 0, null),
                new DebPayloadReader.FileRecord("usr/share/doc/kept.txt", "file", 4, Hash("kept"), 420, 0, 0, null),
                new DebPayloadReader.FileRecord("etc/runtime.conf", "file", 7, Hash("config"), 420, 1000, 0, null));
            var required = new DebPayloadReader.PackageResult(
                new("runtime-core", "3", "amd64"),
                ImmutableDictionary<string, string>.Empty.Add("Package", "runtime-core"),
                ["etc/runtime.conf"], packageFiles, 100, 200);
            var removed = new KeycloakRemovedPackageSpec("curl", "amd64", "7");
            var common = Entries(policy, statusBaseline);
            var actual = Entries(policy, statusActual).Where(x => x.Path != "var/lib/dpkg/status").ToImmutableArray()
                .Add(File("var/lib/dpkg/status", statusActual));
            return new(Image("baseline", common), Image("actual", actual), required, removed, statusBaseline, statusActual, policy);
        }

        internal RuntimeFixture WithPolicy(string value)
        {
            var baseline = WithEntry(Baseline, File("etc/dpkg/dpkg.cfg.d/excludes", value));
            var actual = WithEntry(Actual, File("etc/dpkg/dpkg.cfg.d/excludes", value));
            return this with { Baseline = baseline, Actual = actual, Policy = value };
        }

        internal RuntimeFixture WithStatus(string value) => this with { Actual = WithEntry(Actual, File("var/lib/dpkg/status", value)), StatusActual = value };
        internal RuntimeFixture WithPasswd(string value) => this with { Actual = WithEntry(Actual, File("etc/passwd", value)) };
        internal RuntimeFixture Without(string path) => this with { Actual = Actual with { Entries = Actual.Entries.Where(x => x.Path != path).ToImmutableArray() } };
        internal RuntimeFixture WithLink(string path, string target) => this with { Actual = WithEntry(Actual, new ObservedFile(path, "symlink", "", 0, 511, 0, 0, target, 0)) };
        internal RuntimeFixture With(string path, string type, string? sha = null, int? mode = null) => this with { Actual = WithEntry(Actual, new ObservedFile(path, type, sha ?? Hash("runtime"), 7, mode ?? 493, 1000, 0, null, 0)) };
        internal RuntimeFixture WithDuplicate(string path) => this with { Actual = Actual with { Entries = Actual.Entries.Add(Actual.Entries.First(x => x.Path == path)) } };

        private static ObservedOciImage Image(string id, ImmutableArray<ObservedFile> entries) => new(
            id, "manifest-" + id, "linux/amd64", "config-" + id, "1000:0", [], ImmutableDictionary<string, string>.Empty, [], [], entries,
            [], new OciReaderLimits(), 1, 1);

        private static ImmutableArray<ObservedFile> Entries(string policy, string status)
        {
            var entries = ImmutableArray.CreateBuilder<ObservedFile>();
            entries.Add(File("etc/dpkg/dpkg.cfg.d/excludes", policy));
            entries.Add(File("var/lib/dpkg/status", status));
            entries.Add(File("etc/passwd", "keycloak:x:1000:0::/opt/keycloak:/usr/sbin/nologin\n"));
            entries.Add(new ObservedFile("opt/keycloak", "directory", "", 0, 493, 1000, 0, null, 0));
            entries.Add(File("usr/share/ca-certificates/ca.pem", "ca"));
            entries.Add(new ObservedFile("etc/ssl/certs/ca-certificates.crt", "symlink", "", 0, 511, 0, 0, "/usr/share/ca-certificates/ca.pem", 0));
            entries.Add(File("opt/java/openjdk/lib/security/cacerts", "cacerts"));
            entries.Add(File("usr/bin/runtime-core", "runtime", 493, 1000, 0));
            entries.Add(File("usr/share/doc/excluded.txt", "excluded"));
            entries.Add(File("usr/share/doc/kept.txt", "kept"));
            entries.Add(File("etc/runtime.conf", "config", 420, 1000, 0));
            entries.Add(new ObservedFile("usr/bin/java", "symlink", "", 0, 511, 0, 0, "/opt/java/bin/java", 0));
            entries.Add(new ObservedFile("opt/java/bin/java", "file", Hash("java"), 4, 493, 1000, 0, null, 0));
            entries.Add(new ObservedFile("usr/lib/runtime-core.hardlink", "hardlink", "", 0, 493, 1000, 0, "usr/bin/runtime-core", 0));
            return entries.ToImmutable();
        }

        private static ObservedFile File(string path, string content, int mode = 420, long uid = 0, long gid = 0) => new(path, "file", Hash(content), Encoding.UTF8.GetByteCount(content), mode, uid, gid, null, 0, Encoding.UTF8.GetBytes(content));
        private static ObservedOciImage WithEntry(ObservedOciImage image, ObservedFile entry) => image with { Entries = image.Entries.Where(x => x.Path != entry.Path).Append(entry).ToImmutableArray() };
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
