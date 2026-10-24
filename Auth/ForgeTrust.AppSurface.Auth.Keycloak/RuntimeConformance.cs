using System.Collections.Immutable;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Private, evidence-based comparison of a captured runtime baseline and final OCI observation.</summary>
internal static class RuntimeConformance
{
    internal sealed record PayloadObservation(string Package, string Path, string Disposition, string? ExpectedSha256, string? ActualSha256, int Mode, long Uid, long Gid);
    internal sealed record PackageObservation(string Package, string Version, string Architecture, string Disposition);
    internal sealed record RuntimeObservation(string Path, string Disposition, string? ExpectedSha256, string? ActualSha256, string Type, int Mode, long Uid, long Gid, string? LinkTarget);
    internal sealed record Result(ImmutableArray<PackageObservation> Packages, ImmutableArray<PayloadObservation> Payloads, ImmutableArray<RuntimeObservation> Files, string BaselineStatusSha256, string ActualStatusSha256);
    internal sealed record DpkgPathRule(string Pattern, bool Include);
    internal sealed record Policy(IReadOnlyList<DpkgPathRule> DpkgRules, string HomePath = "/opt/keycloak", int RequiredUid = 1000, int RequiredGid = 0);

    /// <summary>Reads and compares the captured dpkg policy bytes, preserving rule order.</summary>
    internal static Policy ReadPolicy(ObservedOciImage baselineObserved, ObservedOciImage actualObserved)
    {
        var baseline = PolicyBytes(baselineObserved); var actual = PolicyBytes(actualObserved);
        if (!baseline.AsSpan().SequenceEqual(actual)) throw new InvalidDataException("Captured dpkg path policy changed.");
        var rules = new List<DpkgPathRule>();
        foreach (var raw in Encoding.UTF8.GetString(actual).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var include = line.StartsWith("path-include=", StringComparison.Ordinal); var exclude = line.StartsWith("path-exclude=", StringComparison.Ordinal);
            if (!include && !exclude) throw new InvalidDataException("Unknown dpkg path policy directive.");
            var pattern = line[(include ? "path-include=" : "path-exclude=").Length..].Trim(); if (pattern.Length == 0 || pattern.Contains('\\')) throw new InvalidDataException("Unsafe dpkg path policy pattern.");
            rules.Add(new DpkgPathRule(pattern.TrimStart('/'), include));
        }
        return new Policy(rules);
    }

    /// <summary>Compares package status, package payload dispositions, conffiles, ownership, links, and fixed runtime profile facts.</summary>
    internal static Result Verify(
        ObservedOciImage baselineObserved,
        ObservedOciImage actualObserved,
        IReadOnlyList<DebPayloadReader.PackageResult> signedPackages,
        IReadOnlyList<KeycloakRemovedPackageSpec> removed,
        Policy policy)
    {
        ArgumentNullException.ThrowIfNull(baselineObserved); ArgumentNullException.ThrowIfNull(actualObserved); ArgumentNullException.ThrowIfNull(signedPackages); ArgumentNullException.ThrowIfNull(removed); ArgumentNullException.ThrowIfNull(policy);
        var baselineStatus = Status(baselineObserved); var actualStatus = Status(actualObserved); var baselinePackages = ParseStatus(baselineStatus); var actualPackages = ParseStatus(actualStatus);
        var required = signedPackages.ToDictionary(x => (x.Identity.Package, x.Identity.Architecture), x => x.Identity.Version);
        var removedKeys = removed.Select(x => (x.Name, x.Architecture)).ToHashSet();
        foreach (var package in signedPackages) { var key = (package.Identity.Package, package.Identity.Architecture); if (!actualPackages.TryGetValue(key, out var actual) || actual.Version != package.Identity.Version || actual.Architecture != package.Identity.Architecture) throw new InvalidDataException("Required package status is absent or mismatched."); }
        foreach (var package in removed)
        {
            if (!baselinePackages.TryGetValue((package.Name, package.Architecture), out var original) || original.Version != package.ExpectedVersion)
                throw new InvalidDataException("Removed package baseline identity differs.");
            if (actualPackages.ContainsKey((package.Name, package.Architecture))) throw new InvalidDataException("Removed package remains installed.");
        }
        foreach (var key in actualPackages.Keys) if (!baselinePackages.ContainsKey(key) && !required.ContainsKey(key)) throw new InvalidDataException("Unexpected package was installed.");
        foreach (var key in baselinePackages.Keys) if (!actualPackages.ContainsKey(key) && !removedKeys.Contains(key)) throw new InvalidDataException("Unapproved package removal.");
        foreach (var key in baselinePackages.Keys.Where(x => actualPackages.ContainsKey(x) && !required.ContainsKey(x) && !removedKeys.Contains(x))) if (baselinePackages[key].Version != actualPackages[key].Version || baselinePackages[key].Architecture != actualPackages[key].Architecture) throw new InvalidDataException("Unapproved baseline package version or architecture change.");
        var packages = actualPackages.Select(x => new PackageObservation(x.Key.Package, x.Value.Version, x.Value.Architecture, required.ContainsKey(x.Key) ? "required" : removedKeys.Contains(x.Key) ? "removed" : baselinePackages.ContainsKey(x.Key) ? "preserved" : "unexpected")).OrderBy(x => x.Package, StringComparer.Ordinal).ThenBy(x => x.Architecture, StringComparer.Ordinal).ToImmutableArray();
        VerifyProfile(actualObserved, policy);
        var baselineFiles = actualMap(baselineObserved); var actualFiles = actualMap(actualObserved); var payloads = ImmutableArray.CreateBuilder<PayloadObservation>(); var runtime = ImmutableArray.CreateBuilder<RuntimeObservation>();
        foreach (var package in signedPackages)
        {
            foreach (var file in package.Files.Where(x => x.Kind == "file"))
            {
                var path = Canonical(file.Path); var excluded = !Included(path, policy.DpkgRules); actualFiles.TryGetValue(path, out var actual); var expectedHash = file.Sha256?.ToLowerInvariant(); var actualHash = actual?.Sha256;
                if (excluded) { if (baselineFiles.TryGetValue(path, out var baseFile) && baseFile.Type == "file") { if (actual is null || !EqualHash(baseFile.Sha256, actualHash)) throw new InvalidDataException("Excluded baseline file changed or disappeared."); } else if (actual is not null) throw new InvalidDataException("Excluded new file is present."); payloads.Add(new(package.Identity.Package, path, "excluded-by-dpkg-policy", expectedHash, actualHash, file.Mode, file.Uid, file.Gid)); continue; }
                if (actual is null || actual.Type != "file" || !EqualHash(expectedHash, actual.Sha256)) throw new InvalidDataException("Signed regular payload is absent or mismatched.");
                if (actual.Mode != file.Mode || actual.Uid != file.Uid || actual.Gid != file.Gid) throw new InvalidDataException("Signed payload ownership or mode mismatched."); payloads.Add(new(package.Identity.Package, path, "present", expectedHash, actualHash, actual.Mode, actual.Uid, actual.Gid));
            }
            foreach (var conffile in package.Conffiles)
            {
                var path = Canonical(conffile); if (!actualFiles.TryGetValue(path, out var actual) || actual.Type != "file") throw new InvalidDataException("Signed conffile is absent or not regular."); var signedConffile = package.Files.FirstOrDefault(x => x.Kind == "file" && Canonical(x.Path) == path) ?? throw new InvalidDataException("Conffile is not present in signed payload."); if (!EqualHash(signedConffile.Sha256, actual.Sha256)) throw new InvalidDataException("Signed conffile hash mismatch.");
            }
        }
        foreach (var pair in actualFiles) { if (pair.Value.Type is "symlink" or "hardlink") runtime.Add(new(pair.Key, "link", null, pair.Value.Sha256, pair.Value.Type, pair.Value.Mode, pair.Value.Uid, pair.Value.Gid, pair.Value.LinkTarget)); else if (pair.Value.Type == "file") runtime.Add(new(pair.Key, "observed-regular", null, pair.Value.Sha256, pair.Value.Type, pair.Value.Mode, pair.Value.Uid, pair.Value.Gid, null)); }
        return new(packages, payloads.ToImmutable(), runtime.ToImmutable(), HashStatus(baselineStatus), HashStatus(actualStatus));
    }

    private static void VerifyProfile(ObservedOciImage image, Policy policy)
    {
        if (image.ConfigUser != $"{policy.RequiredUid}:{policy.RequiredGid}" || image.ConfigUser.Equals("ubuntu", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Runtime user is not the fixed non-root profile.");
        var files = actualMap(image); if (!files.TryGetValue(policy.HomePath.TrimStart('/'), out var home) || home.Type != "directory" || home.Uid != policy.RequiredUid || home.Gid != policy.RequiredGid) throw new InvalidDataException("Runtime home is absent or has wrong ownership.");
        foreach (var forbidden in new[] { "usr/bin/curl", "usr/bin/pebble" }) if (files.ContainsKey(forbidden)) throw new InvalidDataException("Forbidden runtime executable remains.");
        foreach (var certificate in new[] { "etc/ssl/certs/ca-certificates.crt", "opt/java/openjdk/lib/security/cacerts" }) { var cert = ResolveRegular(certificate, files); if (cert is null) throw new InvalidDataException("Required certificate store is absent or unsafe."); }
        if (!files.TryGetValue("etc/passwd", out var passwd) || passwd.TargetedPayload is null || !Encoding.UTF8.GetString(passwd.TargetedPayload).Split('\n').Any(line => { var f = line.Split(':'); return f.Length >= 7 && f[0] == "keycloak" && f[2] == policy.RequiredUid.ToString() && f[3] == policy.RequiredGid.ToString() && f[5] == policy.HomePath && f[6] == "/usr/sbin/nologin"; })) throw new InvalidDataException("Keycloak account identity is not proven by passwd payload.");
        if (passwd.TargetedPayload is not null && Encoding.UTF8.GetString(passwd.TargetedPayload).Split('\n').Any(x => x.StartsWith("ubuntu:", StringComparison.Ordinal))) throw new InvalidDataException("Ubuntu account remains.");
    }
    private static Dictionary<string, ObservedFile> actualMap(ObservedOciImage image) { var map = new Dictionary<string, ObservedFile>(StringComparer.Ordinal); foreach (var file in image.Entries) { var path = Canonical(file.Path); if (!map.TryAdd(path, file)) throw new InvalidDataException("Duplicate final canonical path."); } return map; }
    private static ObservedFile? ResolveRegular(string path, IReadOnlyDictionary<string, ObservedFile> files) { var seen = new HashSet<string>(StringComparer.Ordinal); var current = Canonical(path); for (var i = 0; i < 32; i++) { if (!seen.Add(current) || !files.TryGetValue(current, out var item)) return null; if (item.Type == "file") return item; if (item.Type != "symlink" || string.IsNullOrWhiteSpace(item.LinkTarget) || item.LinkTarget.Contains('\\')) return null; current = ResolveLink(current, item.LinkTarget); } return null; }
    private static string ResolveLink(string source, string target) { if (target.StartsWith('/')) return Canonical(target[1..]); var parts = source[..Math.Max(0, source.LastIndexOf('/'))].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList(); foreach (var part in target.Split('/')) { if (part is "" or ".") continue; if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Symlink escapes image root."); parts.RemoveAt(parts.Count - 1); } else parts.Add(part); } return Canonical(string.Join('/', parts)); }
    private static byte[] PolicyBytes(ObservedOciImage image) { var map = actualMap(image); var entry = map.GetValueOrDefault("etc/dpkg/dpkg.cfg.d/excludes"); return entry?.Type == "file" && entry.TargetedPayload is not null ? entry.TargetedPayload : throw new InvalidDataException("Captured dpkg policy payload is required."); }
    private static Dictionary<(string Package, string Architecture), StatusRecord> ParseStatus(byte[] bytes) { var result = new Dictionary<(string, string), StatusRecord>(); foreach (var paragraph in Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)) { var fields = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var line in paragraph.Split('\n')) { var colon = line.IndexOf(':'); if (colon > 0 && !line.StartsWith(' ') && !line.StartsWith('\t') && !fields.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new InvalidDataException("Duplicate dpkg status field."); } if (fields.TryGetValue("Package", out var p) && fields.TryGetValue("Version", out var v) && fields.TryGetValue("Architecture", out var a) && fields.GetValueOrDefault("Status") == "install ok installed") { if (!result.TryAdd((p, a), new(v, a))) throw new InvalidDataException("Duplicate dpkg package identity."); } } return result; }
    private static byte[] Status(ObservedOciImage image) { var entry = image.Entries.FirstOrDefault(x => Canonical(x.Path) == "var/lib/dpkg/status" && x.Type == "file"); return entry?.TargetedPayload ?? throw new InvalidDataException("Captured dpkg status payload is required."); }
    private static string HashStatus(byte[] status) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(status)).ToLowerInvariant();
    private static bool EqualHash(string? a, string? b) => a is not null && b is not null && Digest(a).Equals(Digest(b), StringComparison.OrdinalIgnoreCase);
    private static string Digest(string value) => value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
    private static string Canonical(string s) { if (s.Contains('\\') || s.StartsWith('/')) throw new InvalidDataException("Unsafe runtime path."); var p = s; while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..]; p = p.TrimEnd('/'); if (p.Length == 0) return "."; if (p.Split('/').Any(x => x is "" or "." or "..")) throw new InvalidDataException("Unsafe runtime path."); return p; }
    private static bool Included(string path, IReadOnlyList<DpkgPathRule> rules) { var included = true; foreach (var rule in rules) if (Glob(rule.Pattern, path)) included = rule.Include; return included; }
    private static bool Glob(string pattern, string value) { var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$"; return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
    private sealed record StatusRecord(string Version, string Architecture);
}
