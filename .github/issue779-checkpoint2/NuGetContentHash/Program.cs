using System.Security.Cryptography;
using System.Text.Json;
using NuGet.Packaging;

try
{
    if (args.Length != 1) return 65;
    using var stream = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
    var length = stream.Length;
    if (length is <= 0 or > 128L * 1024 * 1024) return 65;
    var archiveHash = Convert.ToBase64String(SHA512.HashData(stream));
    stream.Position = 0;
    using var reader = new PackageArchiveReader(stream, leaveStreamOpen: true);
    var contentHash = reader.GetContentHash(CancellationToken.None);
    if (Convert.FromBase64String(contentHash).Length != 64 || stream.Length != length) return 65;
    Console.WriteLine(JsonSerializer.Serialize(new { content_hash = contentHash, archive_sha512 = archiveHash, bytes = length }));
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
{
    Console.Error.WriteLine("Package hash verification failed.");
    return 65;
}
