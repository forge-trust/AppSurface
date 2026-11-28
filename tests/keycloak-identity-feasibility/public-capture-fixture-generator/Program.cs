using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Auth.Keycloak;
using ForgeTrust.AppSurface.Auth.Keycloak.Tests;
using ForgeTrust.AppSurface.Auth.Keycloak.Tests.Tests;

if (args.Length != 1) return 64;
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Preparation destination must be absent.");
Directory.CreateDirectory(output);
using var baseline = SecurityRecipeFixture.Create();
var originals = baseline.MaterialPaths.Where(item => item.Key is not ("final-index" or "final-archive"))
    .ToDictionary(item => item.Key, item => File.ReadAllBytes(item.Value), StringComparer.Ordinal);
var preparation = new List<object>();
foreach (var variant in new[] { "valid", "invalid-signature", "invalid-zstd" })
{
    var bytes = originals.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
    bytes["openssl"] = RecompressDeb(originals["openssl"], variant == "invalid-zstd");
    var packageNames = new[] { "openssl", "openssl-provider-legacy", "libssl3t64" };
    var paragraphs = packageNames.Select(name => $"Package: {name}\nVersion: 3.0.0\nArchitecture: amd64\nFilename: pool/main/{name[0]}/{name}_3.0.0_amd64.deb\nSize: {bytes[name].Length}\nSHA256: {Hash(bytes[name])}\nDepends: libc6 (>= 2.0)\n");
    var signed = SignedRepositoryFixture.Create(packageIndexText: string.Join("\n", paragraphs));
    bytes["release"] = variant == "invalid-signature"
        ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signed.ReleaseBytes).Replace("Origin: Fixture", "Origin: Fixturf", StringComparison.Ordinal))
        : signed.ReleaseBytes;
    bytes["keyring"] = signed.KeyringBytes;
    bytes["package-index"] = signed.IndexBytes;
    var materials = baseline.Recipe.MaterialReferences.Where(item => bytes.ContainsKey(item.Id))
        .Select(item => item with { Sha256 = Hash(bytes[item.Id]), MaxBytes = bytes[item.Id].LongLength }).ToImmutableArray();
    var originalProfile = ((KeycloakOperation.RuntimeProfileOperation)baseline.Recipe.Operations.Single()).Profile;
    var profile = originalProfile with
    {
        RepositorySnapshots = [originalProfile.RepositorySnapshots.Single() with
        {
            Sha256 = Hash(bytes["release"]), SigningFingerprint = signed.Fingerprint
        }],
        RequiredPackages = originalProfile.RequiredPackages.Select(package => package with { Sha256 = Hash(bytes[package.MaterialId!]) }).ToImmutableArray()
    };
    var recipe = baseline.Recipe with
    {
        Operations = [new KeycloakOperation.RuntimeProfileOperation(profile)],
        MaterialReferences = materials,
        CapturedHostInputs = materials.Select(item => item.Id).ToImmutableArray(),
        ResourceLimits = new(32 * 1024 * 1024, 4096, 32 * 1024 * 1024, 32 * 1024 * 1024, 20)
    };
    var policy = baseline.Policy with
    {
        OwnerApprovalReference = "issue888-synthetic-parser-fixture-only",
        MaterialSha256ByIdentity = materials.ToImmutableDictionary(item => item.Id, item => item.Sha256, StringComparer.Ordinal),
        SigningFingerprints = [signed.Fingerprint],
        HistoricalExactReleaseBytes = [Hash(bytes["release"])]
    };
    var folder = Path.Combine(output, variant);
    var blobStorage = Path.Combine(folder, "blobs-base64");
    Directory.CreateDirectory(blobStorage);
    foreach (var value in bytes.Values.DistinctBy(Hash))
        File.WriteAllText(Path.Combine(blobStorage, Hash(value) + ".b64"), Convert.ToBase64String(value) + "\n");
    var recipeBytes = KeycloakCanonicalJson.Write(recipe);
    var policyBytes = KeycloakCanonicalJson.Write(policy);
    File.WriteAllBytes(Path.Combine(folder, "recipe.json"), recipeBytes);
    File.WriteAllBytes(Path.Combine(folder, "policy.json"), policyBytes);
    var bindings = materials.Select(item => new { id = item.Id, file = "blobs/" + item.Sha256, sha256 = item.Sha256, maxBytes = item.MaxBytes });
    File.WriteAllText(Path.Combine(folder, "bindings.json"), JsonSerializer.Serialize(bindings, JsonOptions()) + "\n");
    var expectation = new
    {
        variant,
        recipeDigest = Hash(recipeBytes),
        policyDigest = Hash(policyBytes),
        platform = "linux/amd64",
        mode = "Security",
        materialCount = materials.Length,
        uniqueBlobs = bytes.Values.Select(Hash).Distinct().Count(),
        opensslDataCompression = "zstd",
        expectedCapture = variant == "valid",
        signingFingerprint = signed.Fingerprint,
        outerBindingsVerified = materials.All(item => policy.MaterialSha256ByIdentity[item.Id] == Hash(bytes[item.Id]) && item.MaxBytes == bytes[item.Id].LongLength)
    };
    File.WriteAllText(Path.Combine(folder, "expectation.json"), JsonSerializer.Serialize(expectation, JsonOptions()) + "\n");
    preparation.Add(expectation);
}
File.WriteAllText(Path.Combine(output, "preparation.json"), JsonSerializer.Serialize(preparation, JsonOptions()) + "\n");
Console.WriteLine("Prepared three frozen variants; signing keys were not serialized.");
return 0;

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
static byte[] RecompressDeb(byte[] source, bool invalidData)
{
    using var input = new MemoryStream(source);
    input.Position = 8;
    using var output = new MemoryStream();
    output.Write(Encoding.ASCII.GetBytes("!<arch>\n"));
    while (input.Position < input.Length)
    {
        var header = new byte[60];
        input.ReadExactly(header);
        var name = Encoding.ASCII.GetString(header, 0, 16).Trim().TrimEnd('/');
        var size = int.Parse(Encoding.ASCII.GetString(header, 48, 10).Trim(), CultureInfo.InvariantCulture);
        var member = new byte[size];
        input.ReadExactly(member);
        if ((size & 1) != 0) input.ReadByte();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            using var compressed = new MemoryStream(member);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            gzip.CopyTo(expanded);
            using var compressor = new ZstdSharp.Compressor();
            member = invalidData && name == "data.tar.gz" ? Encoding.ASCII.GetBytes("invalid-zstandard-frame") : compressor.Wrap(expanded.ToArray()).ToArray();
            name = name[..^3] + ".zst";
        }
        var nextHeader = Encoding.ASCII.GetBytes((name + "/").PadRight(16) + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6)
            + "100644".PadRight(8) + member.Length.ToString(CultureInfo.InvariantCulture).PadLeft(10) + "`\n");
        if (nextHeader.Length != 60) throw new InvalidDataException();
        output.Write(nextHeader);
        output.Write(member);
        if ((member.Length & 1) != 0) output.WriteByte(10);
    }
    return output.ToArray();
}
