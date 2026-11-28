using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Captures candidate archive bytes so admission and hashing can use the same snapshot.</summary>
internal static class PackedCandidateArchive
{
    /// <summary>The inclusive maximum compressed archive size in bytes.</summary>
    internal const int MaximumBytes = 512 * 1024 * 1024;
    private const int MaximumSpecificationBytes = 1024 * 1024;

    /// <summary>Describes whether capture obtained a complete archive within the size limit.</summary>
    internal enum CaptureStatus
    {
        /// <summary>The stream supplied exactly its initially reported length.</summary>
        Complete,

        /// <summary>The stream reported a length above the compressed archive limit.</summary>
        TooLarge,

        /// <summary>The stream grew beyond its initially reported length.</summary>
        Grew,
    }

    /// <summary>Reads the opened archive once and verifies EOF after its initially reported length.</summary>
    /// <param name="path">The candidate archive path after any caller-specific path admission checks.</param>
    /// <param name="cancellationToken">Cancels the archive read and the EOF check.</param>
    /// <returns>The capture status and the complete original bytes on success.</returns>
    /// <exception cref="EndOfStreamException">The archive shrank during capture.</exception>
    internal static async Task<(CaptureStatus Status, byte[] Bytes)> CaptureAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        if (length > MaximumBytes) return (CaptureStatus.TooLarge, []);

        var bytes = new byte[(int)length];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        return extra == 0 ? (CaptureStatus.Complete, bytes) : (CaptureStatus.Grew, []);
    }

    /// <summary>Reads a candidate NuSpec and returns the single direct metadata element, attached to its document.</summary>
    /// <param name="specification">The NuSpec entry in the captured candidate archive.</param>
    /// <returns>The metadata, or null if the entry is empty, exceeds 1 MiB or supplies bytes beyond its declared length.</returns>
    /// <remarks>The decompressed read is bounded even when the declared length is inaccurate. XML DTDs and external entities are prohibited.</remarks>
    /// <exception cref="EndOfStreamException">The entry ends before supplying all declared bytes.</exception>
    /// <exception cref="XmlException">The NuSpec is malformed or declares a DTD.</exception>
    /// <exception cref="InvalidOperationException">The NuSpec has missing or ambiguous metadata.</exception>
    internal static XElement? ReadBoundedMetadata(ZipArchiveEntry specification)
    {
        var length = specification.Length;
        if (length is <= 0 or > MaximumSpecificationBytes) return null;

        using var contents = specification.Open();
        var bytes = new byte[(int)length];
        contents.ReadExactly(bytes);
        if (contents.ReadByte() != -1) return null;

        using var snapshot = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(snapshot, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader).Root!.Elements().Single(element => element.Name.LocalName == "metadata");
    }
}
