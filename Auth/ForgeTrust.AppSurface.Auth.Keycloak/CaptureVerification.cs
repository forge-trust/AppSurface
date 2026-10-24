using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

public static partial class KeycloakSecurity
{
    internal static void VerifyCapturedInputs(KeycloakSecurityRecipe recipe, KeycloakMaterialTrustPolicy policy, string directory, CancellationToken token)
    {
        var source = recipe.SourceImage;
        OciLayout.VerifyUpstreamIndex(Material(directory, source.IndexMaterialId!), source.Index, source.PlatformManifest, source.Platform);
        var observed = new OciReader(OciReader.SourceCaptureLimits).Read(Material(directory, source.ArchiveMaterialId!), new(null, source.PlatformManifest,
            source.Platform, source.ConfigDigest!, source.DiffIds), token);
        Require(observed.Entries.Any(x => x.Path == "opt/keycloak/bin/kc.sh" && x.Type == "file"), "Source profile is missing Keycloak.");
        foreach (var image in recipe.RuntimeFrontendOciIdentities.Concat(recipe.Operations.OfType<KeycloakOperation.RuntimeProfileOperation>().SelectMany(x => x.Profile.RuntimeOciIdentities)))
        {
            var platform = image.Platform ?? source.Platform;
            OciLayout.VerifyUpstreamIndex(Material(directory, image.IndexMaterialId!), image.Digest, image.PlatformManifestDigest!, platform);
            _ = new OciReader().Read(Material(directory, image.ArchiveMaterialId!), new(null, image.PlatformManifestDigest!, platform,
                image.ConfigDigest!, image.DiffIds), token);
        }
        var actual = observed.Entries.ToDictionary(x => x.Path, StringComparer.Ordinal);
        if (recipe.TemplateBaseline is { } baseline)
        {
            Require(actual.TryGetValue(baseline.ArchivePath.TrimStart('/'), out var jar) && jar.Type == "file"
                && jar.Sha256 == "sha256:" + baseline.ArchiveSha256, "Template source archive differs.");
            var declared = recipe.MaterialReferences.Single(x => x.Id == baseline.MaterialId);
            Require(declared.Sha256 == baseline.Sha256, "Template baseline material differs.");
            var selected = Path.Combine(PrivateTemporaryRoot(), "appsurface-template-" + Guid.NewGuid().ToString("N"));
            try
            {
                new OciReader(OciReader.SourceCaptureLimits).Read(Material(directory, source.ArchiveMaterialId!),
                    new(null, source.PlatformManifest, source.Platform, source.ConfigDigest!, source.DiffIds),
                    new OciReadSelection(new HashSet<string>([baseline.ArchivePath.TrimStart('/')], StringComparer.Ordinal), selected), token);
                var sourceJar = Path.Combine(selected, baseline.ArchivePath.TrimStart('/'));
                ArchiveTransformer.ValidateReadOnlyInput(sourceJar, token);
                using var zip = ZipFile.OpenRead(sourceJar);
                Require(zip.Entries.Count <= 65536, "Template archive count exceeds profile.");
                var entry = zip.Entries.SingleOrDefault(x => x.FullName == baseline.Entry);
                Require(entry is not null && entry.Length <= 128 * 1024, "Template baseline is absent or exceeds profile.");
                using var input = entry!.Open(); using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var bytes = new byte[8192]; int read; long total = 0;
                while ((read = input.Read(bytes)) != 0) { token.ThrowIfCancellationRequested(); total += read; Require(total <= 128 * 1024, "Template baseline exceeds profile."); hash.AppendData(bytes, 0, read); }
                Require(total == entry.Length && Convert.ToHexStringLower(hash.GetHashAndReset()) == baseline.Sha256, "Reviewed template baseline differs from source member.");
            }
            finally { CleanOwned(selected); }
        }

        foreach (var operation in recipe.Operations)
        {
            token.ThrowIfCancellationRequested();
            if (operation is KeycloakOperation.ArtifactReplacementGroupOperation group)
                foreach (var replacement in group.Group.Replacements)
                {
                    Require(actual.TryGetValue(replacement.Original.Destination.TrimStart('/'), out var file)
                        && file.Type == "file" && file.Sha256 == "sha256:" + replacement.Original.Sha256, "Original artifact differs from captured source.");
                    var material = recipe.MaterialReferences.Single(x => x.Sha256 == replacement.Replacement.Sha256);
                    Require(policy.PermittedCoordinateMappings.Contains(Coordinate(replacement.Replacement.Coordinate), StringComparer.Ordinal), "Replacement coordinate is not authorized by host policy.");
                    VerifyMavenIdentity(Material(directory, material.Id), replacement.Replacement.Coordinate);
                }
            if (operation is KeycloakOperation.EmbeddedArchiveReplacementOperation archive)
            {
                var modules = archive.Replacement.ModuleSet!;
                Require(actual.TryGetValue(modules.Destination.TrimStart('/'), out var cli) && cli.Type == "file"
                    && cli.Sha256 == "sha256:" + modules.SourceArtifact.Sha256, "Original embedded archive differs.");
                foreach (var originalModule in modules.SourceModuleSha256ByIdentity)
                    Require(actual.Values.Any(file => file.Type == "file" && file.Path.StartsWith("opt/keycloak/lib/", StringComparison.Ordinal)
                        && file.Sha256 == "sha256:" + originalModule.Value), "Original module digest is not present in the captured source library tree.");
                // The original module association is proved from the retained source image's
                // actual CLI archive. A digest on the containing image alone is insufficient:
                // it cannot establish that the declared Jackson coordinates are present.
                VerifyEmbeddedMavenIdentity(recipe, directory, modules.SourceModuleSha256ByIdentity.Keys, token);
                foreach (var item in modules.ReplacementModuleSha256ByIdentity)
                {
                    var coordinate = ParseCoordinate(item.Key);
                    Require(policy.PermittedCoordinateMappings.Contains(Coordinate(coordinate), StringComparer.Ordinal), "Archive module coordinate is not authorized.");
                    VerifyMavenIdentity(MaterialBySha(recipe, directory, item.Value), coordinate);
                }
            }
            if (operation is KeycloakOperation.RuntimeProfileOperation runtime) VerifyRepository(recipe, policy, runtime.Profile, directory, token);
        }
    }

    internal static void VerifyRepository(KeycloakSecurityRecipe recipe, KeycloakMaterialTrustPolicy policy, RuntimeProfile profile, string directory, CancellationToken token = default)
    {
        foreach (var snapshot in profile.RepositorySnapshots)
        {
            token.ThrowIfCancellationRequested();
            Require(policy.SigningFingerprints.Contains(snapshot.SigningFingerprint, StringComparer.Ordinal)
                && !policy.WithdrawnKeys.Contains(snapshot.SigningFingerprint, StringComparer.Ordinal)
                && policy.SigningAlgorithms.Contains("RSA/SHA512", StringComparer.Ordinal), "Snapshot signer is not authorized.");
            var releaseMaterial = recipe.MaterialReferences.Single(x => x.Sha256 == snapshot.Sha256);
            Require(releaseMaterial.ExactHttpsUri == snapshot.ExactHttpsUri, "Signed release origin differs.");
            var keyMaterial = recipe.MaterialReferences.Single(x => x.Id == snapshot.KeyringMaterialId);
            using var release = File.OpenRead(Material(directory, releaseMaterial.Id));
            using var keys = File.OpenRead(Material(directory, keyMaterial.Id));
            var indexes = snapshot.PackageIndexMaterialIds.ToDictionary(x => x.Key, x => (Stream)File.OpenRead(Material(directory, x.Value)), StringComparer.Ordinal);
            try
            {
                var historical = policy.HistoricalExactReleaseBytes.Contains(snapshot.Sha256, StringComparer.Ordinal);
                var verification = new SignedRepository.VerificationPolicy(snapshot.Suite, snapshot.Components, snapshot.Architectures,
                    null, DateTimeOffset.UtcNow, historical, historical ? snapshot.Sha256 : null);
                var requested = profile.RequiredPackages.Select(x => new SignedRepository.RequestedPackage(x.Name, x.Version, x.Architecture));
                var result = SignedRepository.VerifyRelease(release, keys, snapshot.Sha256, keyMaterial.Sha256, snapshot.SigningFingerprint, verification, requested, indexes, token);
                foreach (var package in profile.RequiredPackages)
                {
                    token.ThrowIfCancellationRequested();
                    var item = result.Packages.Single(x => x.Package == package.Name && x.Version == package.Version && x.Architecture == package.Architecture);
                    Require(item.Sha256 == package.Sha256, "Package differs from signed index.");
                    using var bytes = File.OpenRead(Material(directory, package.MaterialId!));
                    Require(bytes.Length == item.Size, "Package length differs from signed index.");
                    _ = DebPayloadReader.Read(bytes, package.Sha256, new(package.Name, package.Version, package.Architecture), token);
                }
            }
            finally { foreach (var stream in indexes.Values) stream.Dispose(); }
        }
    }

    internal static string Material(string directory, string id)
    {
        Require(id.Length is > 0 and <= 128 && id is not "." and not ".." && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'), "Invalid material ID.");
        return Path.Combine(directory, id);
    }
    internal static string MaterialBySha(KeycloakSecurityRecipe recipe, string directory, string sha) => Material(directory, recipe.MaterialReferences.Single(x => x.Sha256 == sha).Id);
    internal static string Coordinate(ArtifactCoordinate x) => $"{x.Group}:{x.Artifact}:{x.Version}:{x.Classifier ?? string.Empty}:{x.Extension}";
    internal static ArtifactCoordinate ParseCoordinate(string value)
    {
        var parts = value.Split(':');
        Require(parts.Length is 3 or 5, "Module coordinate must be exact.");
        return new(parts[0], parts[1], parts[2], parts.Length == 5 && parts[3].Length != 0 ? parts[3] : null, parts.Length == 5 ? parts[4] : "jar");
    }
    // Microsoft's exact jre11 coordinates intentionally retain the Java target in the
    // Maven version while pom.properties reports the corresponding library release.
    // This is a closed upstream identity mapping, not a generic suffix normalization.
    private static string ExpectedMavenMetadataVersion(ArtifactCoordinate value) =>
        value is { Group: "com.microsoft.sqlserver", Artifact: "mssql-jdbc", Version: "13.4.0.jre11", Classifier: null, Extension: "jar" }
            ? "13.4.0" : value is { Group: "com.microsoft.sqlserver", Artifact: "mssql-jdbc", Version: "13.2.1.jre11", Classifier: null, Extension: "jar" }
            ? "13.2.1" : value.Version;

    internal static void VerifyMavenIdentity(string path, ArtifactCoordinate expected)
    {
        // Exact content hashes remain mandatory. This additional check rejects an inconsistent
        // Maven association before execution; it never infers a coordinate from its filename.
        ArchiveTransformer.ValidateReadOnlyInput(path, default);
        using var source = File.OpenRead(path);
        Require(source.Length <= 256L * 1024 * 1024, "Artifact exceeds bounded profile.");
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        Require(archive.Entries.Count <= 65536, "Artifact entry count exceeds profile.");
        var propertyName = $"META-INF/maven/{expected.Group}/{expected.Artifact}/pom.properties";
        var matches = archive.Entries.Where(x => x.FullName == propertyName).ToArray();
        if (matches.Length == 1)
        {
            Require(matches[0].Length <= 65536, "Maven metadata exceeds profile.");
            using var reader = new StreamReader(matches[0].Open(), new UTF8Encoding(false, true));
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.ReadLine() is string line)
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var pair = line.Split('=', 2);
                Require(pair.Length == 2 && values.TryAdd(pair[0].Trim(), pair[1].Trim()), "Ambiguous Maven metadata.");
            }
            Require(values.GetValueOrDefault("groupId") == expected.Group && values.GetValueOrDefault("artifactId") == expected.Artifact
                && values.GetValueOrDefault("version") == ExpectedMavenMetadataVersion(expected), "Artifact Maven identity differs.");
            return;
        }
        Require(matches.Length == 0, "Ambiguous Maven metadata.");
        // Several reviewed upstream artifacts omit pom.properties. Their exact digest and
        // approved coordinate association are authoritative; a manifest version, when present,
        // must agree rather than being silently ignored.
        var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
        Require(manifest is not null && manifest.Length <= 16L * 1024 * 1024, "Artifact metadata is unavailable.");
        using var text = new StreamReader(manifest!.Open(), new UTF8Encoding(false, true));
        var main = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        long mainBytes = 0;
        while (text.ReadLine() is string line)
        {
            // The first blank line ends the manifest main section. Per-entry metadata is
            // bounded by the 16 MiB ZIP entry ceiling above; signatures and other sections
            // are deliberately not inspected for Maven identity.
            if (line.Length == 0) break;
            mainBytes = checked(mainBytes + Encoding.UTF8.GetByteCount(line) + 2);
            Require(mainBytes <= 65536, "Artifact main manifest metadata exceeds profile.");
            if (line[0] is ' ' or '\t')
            {
                Require(previous is not null && line.Length > 1, "Manifest continuation is ambiguous.");
                main[previous!] += line[1..];
                continue;
            }

            var split = line.IndexOf(": ", StringComparison.Ordinal);
            Require(split > 0 && split + 2 <= line.Length, "Malformed manifest main metadata.");
            var name = line[..split];
            Require(main.TryAdd(name, line[(split + 2)..]), "Ambiguous manifest main metadata.");
            previous = name;
        }

        if (main.TryGetValue("Implementation-Version", out var implementationVersion))
            Require(implementationVersion == expected.Version, "Artifact manifest version differs.");
        else if (expected.Group == "org.bouncycastle")
            Require(main.GetValueOrDefault("Bundle-Version") == expected.Version, "Artifact bundle version differs.");
        else
            throw new InvalidDataException("Artifact metadata lacks a recognized version identity.");

    }

    private static void VerifyEmbeddedMavenIdentity(KeycloakSecurityRecipe recipe, string directory, IEnumerable<string> coordinates, CancellationToken token)
    {
        var expected = coordinates.Select(ParseCoordinate).ToArray();
        Require(expected.Length > 0, "Embedded archive has no declared source modules.");
        var source = recipe.SourceImage;
        var selected = Path.Combine(Path.GetTempPath(), "appsurface-original-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            // SelectionState writes the verified source payload to a private selected copy.
            // Do not add the 16 MiB CLI to OciReader's in-memory targeted-payload aggregate.
            new OciReader(OciReader.SourceCaptureLimits).Read(Material(directory, source.ArchiveMaterialId!),
                new OciExpectedImage(null, source.PlatformManifest, source.Platform, source.ConfigDigest!, source.DiffIds),
                new OciReadSelection(new HashSet<string>(["opt/keycloak/bin/client/keycloak-admin-cli-26.7.2.jar"], StringComparer.Ordinal), selected), token);
            var cli = Path.Combine(selected, "opt", "keycloak", "bin", "client", "keycloak-admin-cli-26.7.2.jar");
            Require(File.Exists(cli), "Retained source image does not contain the Keycloak admin CLI archive.");
            ArchiveTransformer.ValidateReadOnlyInput(cli, token);
            using var archive = new ZipArchive(File.OpenRead(cli), ZipArchiveMode.Read, leaveOpen: false);
            foreach (var coordinate in expected)
            {
                var entryName = $"META-INF/maven/{coordinate.Group}/{coordinate.Artifact}/pom.properties";
                var entry = archive.Entries.SingleOrDefault(x => x.FullName == entryName);
                Require(entry is not null, $"Original embedded module metadata is missing: {Coordinate(coordinate)}.");
                using var reader = new StreamReader(entry!.Open(), new UTF8Encoding(false, true));
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.ReadLine() is string line)
                {
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var pair = line.Split('=', 2);
                    Require(pair.Length == 2 && values.TryAdd(pair[0].Trim(), pair[1].Trim()), "Ambiguous embedded Maven metadata.");
                }
                Require(values.GetValueOrDefault("groupId") == coordinate.Group
                    && values.GetValueOrDefault("artifactId") == coordinate.Artifact
                    && values.GetValueOrDefault("version") == coordinate.Version,
                    $"Original embedded module identity differs: {Coordinate(coordinate)}.");
            }
        }
        finally { KeycloakSecurity.CleanOwned(selected); }
    }
}
