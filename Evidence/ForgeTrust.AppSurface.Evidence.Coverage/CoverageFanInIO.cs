using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Streaming admission bounds shared as substrate, never coverage semantics.</summary>
internal sealed record CoverageFanInBounds(long XmlBytes, long JsonBytes, long TotalBytes, int Depth,
    int Lines, int Elements, int Attributes, int TokenCharacters, int Branches, int TotalRecords);

/// <summary>One invocation's bounded native IO and deterministic reader/writer custody.</summary>
internal sealed class CoverageFanInIO(CoverageFanInBounds bounds, CancellationToken token) : IDisposable
{
    private readonly Dictionary<string, CoverageRunOutputLease> _roots = new(StringComparer.Ordinal);
    private readonly List<CoverageArtifactOperationLease> _leases = [];
    private readonly HashSet<string> _uncoordinated = new(StringComparer.Ordinal);
    private string? _outputRoot;
    private readonly Dictionary<string, RetainedFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<byte[], AdmittedDocument> _documents = new(ReferenceEqualityComparer.Instance);
    private long _bytes;
    private int _records;
    internal const string CompletionName = "generation.json";

    /// <summary>Acquires exact physical objects in deterministic order and unwinds all partial acquisition.</summary>
    internal void Acquire(IEnumerable<(string Path, bool Exclusive)> desired, bool requireCoordination = true)
    {
        if (_roots.Count != 0) throw new InvalidOperationException("Custody already acquired.");
        var sorted = new SortedDictionary<string, (string Path, bool Exclusive, CoverageRunOutputLease Root)>(StringComparer.Ordinal);
        try
        {
            foreach (var (path, exclusive) in desired)
            {
                token.ThrowIfCancellationRequested();
                var absolute = Full(path);
                var root = CoverageRunOutputLease.AcquireArtifactDirectory(absolute, false, false)
                    ?? throw new IOException("Fan-in input root is unavailable.");
                if (sorted.ContainsKey(root.ArtifactDirectoryIdentity))
                { root.Dispose(); throw new InvalidDataException("Fan-in roots alias the same physical object."); }
                sorted.Add(root.ArtifactDirectoryIdentity, (absolute, exclusive, root));
            }
            foreach (var (identity, item) in sorted)
            {
                token.ThrowIfCancellationRequested();
                if (requireCoordination)
                {
                    var lease = CoverageArtifactOperationLease.Acquire(item.Path, item.Exclusive);
                    _leases.Add(lease);
                    if (lease.PhysicalIdentity != identity) throw new IOException("Fan-in root changed.");
                }
                _roots.Add(item.Path, item.Root);
                if (!requireCoordination) _uncoordinated.Add(item.Path);
            }
        }
        catch
        {
            Dispose();
            foreach (var item in sorted.Values) item.Root.Dispose();
            throw;
        }
    }

    /// <summary>Retains an immediate regular file through hashing and parsing; rejects links and growing input.</summary>
    internal byte[] Read(string path, bool xml = false)
    {
        token.ThrowIfCancellationRequested();
        var absolute = Full(path);
        var root = Root(Path.GetDirectoryName(absolute)!);
        if (_files.TryGetValue(absolute, out var retained)) return retained.Bytes;
        var stream = root.OpenArtifactFile(Path.GetFileName(absolute), false, false);
        try
        {
            var limit = xml ? bounds.XmlBytes : bounds.JsonBytes;
            if (stream.Length > limit) throw new InvalidDataException("Fan-in input exceeds byte budget.");
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                Charge(read);
                if (memory.Length + read > limit) throw new InvalidDataException("Fan-in input exceeds byte budget.");
                memory.Write(buffer, 0, read);
            }
            root.VerifyArtifactBinding();
            var bytes = memory.ToArray();
            _files.Add(absolute, new(stream, root, Path.GetFileName(absolute),
                CoverageRunOutputLease.GetArtifactObjectIdentity(stream.SafeFileHandle), bytes, Hash(bytes)));
            return bytes;
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Admits XML structure before DOM materialization. V2 wire XML forbids DTDs/entities.</summary>
    internal XDocument Xml(byte[] bytes, bool allowInertGeneratorDoctype = false)
    {
        if (bytes.LongLength > bounds.XmlBytes) throw new InvalidDataException("Fan-in XML exceeds byte budget.");
        if (_documents.TryGetValue(bytes, out var document))
        {
            if (!allowInertGeneratorDoctype && document.HadDoctype)
                throw new InvalidDataException("V2 wire XML forbids declarations.");
            return document.Document;
        }
        var settings = new XmlReaderSettings
        {
            DtdProcessing = allowInertGeneratorDoctype ? DtdProcessing.Parse : DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = bounds.XmlBytes,
            MaxCharactersFromEntities = 1
        };
        using (var stream = new MemoryStream(bytes, writable: false))
        using (var reader = XmlReader.Create(stream, settings))
        {
            var elements = 0; var lines = 0; var branches = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Depth > bounds.Depth || reader.Value.Length > bounds.TokenCharacters)
                    throw new InvalidDataException("Fan-in XML exceeds structure budget.");
                if (reader.NodeType == XmlNodeType.DocumentType)
                {
                    if (!allowInertGeneratorDoctype || reader.Name != "coverage" || !string.IsNullOrWhiteSpace(reader.Value))
                        throw new InvalidDataException("Fan-in XML declaration is forbidden.");
                }
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (checked(++elements) > bounds.Elements || reader.AttributeCount > bounds.Attributes)
                    throw new InvalidDataException("Fan-in XML exceeds structure budget.");
                if (reader.Name == "line")
                {
                    if (checked(++lines) > bounds.Lines || checked(++_records) > bounds.TotalRecords)
                        throw new InvalidDataException("Fan-in XML exceeds record budget.");
                }
                if (reader.Name == "condition")
                {
                    if (checked(++branches) > bounds.Branches || checked(++_records) > bounds.TotalRecords)
                        throw new InvalidDataException("Fan-in XML exceeds record budget.");
                }
                while (reader.MoveToNextAttribute())
                    if (reader.Value.Length > bounds.TokenCharacters) throw new InvalidDataException("Fan-in XML token exceeds budget.");
                reader.MoveToElement();
            }
        }
        using var input = new MemoryStream(bytes, writable: false);
        using var load = XmlReader.Create(input, settings);
        var result = XDocument.Load(load);
        var hadDoctype = result.DocumentType is not null;
        if (result.DocumentType is { } declaration && (declaration.Name != "coverage"
            || !string.IsNullOrEmpty(declaration.PublicId)
            || declaration.SystemId != "http://cobertura.sourceforge.net/xml/coverage-04.dtd"
            || !string.IsNullOrWhiteSpace(declaration.InternalSubset)))
            throw new InvalidDataException("Only the known inert normalizer declaration is supported.");
        result.DocumentType?.Remove();
        _documents.Add(bytes, new(result, hadDoctype));
        return result;
    }

    /// <summary>Checks exact allowed inventory without following unknown entries.</summary>
    internal void ExactInventory(string directory, IEnumerable<string> names)
    {
        var expected = names.ToHashSet(StringComparer.Ordinal);
        expected.Add(".appsurface-coverage-operation.lock");
        var entries = Root(directory).EnumerateArtifactEntries(expected.Count + 1);
        if (entries.Any(x => x.IsDirectory || !expected.Contains(x.Name))
            || names.Any(name => !entries.Any(x => x.Name == name)))
            throw new InvalidDataException("Fan-in directory inventory differs.");
    }

    internal string PhysicalIdentity(string directory) => Root(directory).ArtifactDirectoryIdentity;

    /// <summary>Serializes normalized output with byte and cancellation admission before buffer growth.</summary>
    internal byte[] EncodeXml(XDocument document)
    {
        using var output = new BoundedXmlOutput(Math.Min(bounds.XmlBytes, bounds.TotalBytes - _bytes), token);
        document.Save(output, SaveOptions.DisableFormatting);
        var bytes = output.ToArray();
        // A combined or method-preserving projection can contain more records than any
        // individual input. Refuse an output that a consumer could not admit under this policy.
        _ = Xml(bytes);
        return bytes;
    }
    /// <summary>Upgrades a validated invocation to ordered combined input/output custody without nested reacquisition.</summary>
    /// <remarks>Retained file handles survive the transition. The caller rechecks their binding and bytes under the
    /// new leases before publication. Transport/raw producer directories retain no receiving-host ownership claim.</remarks>
    internal CoverageArtifactOperationLease BindOutput(string output, bool replaceOwned, Action onCreated)
    {
        token.ThrowIfCancellationRequested();
        if (_outputRoot is not null) throw new InvalidOperationException("Output custody is already bound.");
        output = Full(output);
        if (_roots.ContainsKey(output)) throw new InvalidDataException("Output aliases an input.");
        using var parent = CoverageRunOutputLease.AcquireArtifactDirectory(Path.GetDirectoryName(output)!, false, false)
            ?? throw new DirectoryNotFoundException("Output parent must exist.");
        var name = Path.GetFileName(output);
        CoverageRunOutputLease.ValidateArtifactComponent(name);
        var present = parent.EnumerateArtifactEntries(100000).Any(x => x.Name == name);
        if (present && !replaceOwned) throw new IOException("Fresh output already exists.");
        var root = present ? CoverageRunOutputLease.AcquireArtifactDirectory(output, false, true)
            ?? throw new IOException("Output is unavailable.") : parent.CreateArtifactDirectory(name, onCreated: onCreated);
        _roots.Add(output, root);
        _outputRoot = output;
        foreach (var lease in _leases.AsEnumerable().Reverse()) lease.Dispose();
        _leases.Clear();
        CoverageArtifactOperationLease? writer = null;
        try
        {
            foreach (var (path, directory) in _roots.Where(x => !_uncoordinated.Contains(x.Key))
                .OrderBy(x => x.Value.ArtifactDirectoryIdentity, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var lease = CoverageArtifactOperationLease.Acquire(path, path == output);
                _leases.Add(lease);
                if (lease.PhysicalIdentity != directory.ArtifactDirectoryIdentity)
                    throw new IOException("Invocation root changed during combined custody acquisition.");
                if (path == output) writer = lease;
            }
            VerifyBindings();
            return writer ?? throw new IOException("Output custody is unavailable.");
        }
        catch { Dispose(); throw; }
    }
    internal void VerifyBindings()
    {
        foreach (var lease in _leases) lease.VerifyBinding();
        foreach (var root in _roots.Values) root.VerifyArtifactBinding();
        foreach (var retained in _files.Values)
        {
            token.ThrowIfCancellationRequested();
            using var named = retained.Root.OpenArtifactFile(retained.Name, false, false);
            if (CoverageRunOutputLease.GetArtifactObjectIdentity(named.SafeFileHandle) != retained.Identity
                || retained.Stream.Length != retained.Bytes.LongLength)
                throw new IOException("Fan-in input binding changed while retained.");
            retained.Stream.Position = 0;
            if (HashStream(retained.Stream, retained.Bytes.LongLength, token) != retained.Hash)
                throw new IOException("Fan-in input bytes changed while retained.");
        }
    }
    private static string Full(string path) => CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(path));
    private CoverageRunOutputLease Root(string directory) => _roots.TryGetValue(Full(directory), out var root)
        ? root : throw new InvalidOperationException("Fan-in IO requires retained custody.");
    private void Charge(int count)
    {
        _bytes = checked(_bytes + count);
        if (_bytes > bounds.TotalBytes) throw new InvalidDataException("Fan-in total byte budget exceeded.");
    }
    /// <summary>Admits staged output against the same operation-wide byte budget as retained inputs.</summary>
    internal void AdmitOutput(IEnumerable<byte[]> files)
    {
        foreach (var bytes in files)
        {
            token.ThrowIfCancellationRequested();
            _bytes = checked(_bytes + bytes.LongLength);
            if (_bytes > bounds.TotalBytes) throw new InvalidDataException("Fan-in total staging budget exceeded.");
        }
    }
    internal void AdmitMetadata(long length)
    {
        token.ThrowIfCancellationRequested();
        _bytes = checked(_bytes + length);
        if (_bytes > bounds.TotalBytes) throw new InvalidDataException("Fan-in metadata exceeds the total operation budget.");
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    /// <summary>Hashes retained bytes with bounded reads and cancellation, without pathname reacquisition.</summary>
    internal static string HashStream(Stream stream, long maximumBytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length = checked(length + read);
            if (length > maximumBytes) throw new InvalidDataException("Retained bytes exceed the admitted budget.");
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public void Dispose()
    {
        foreach (var file in _files.Values) file.Stream.Dispose();
        _files.Clear();
        _documents.Clear();
        foreach (var lease in _leases.AsEnumerable().Reverse()) lease.Dispose();
        _leases.Clear();
        foreach (var root in _roots.Values) root.Dispose();
        _roots.Clear();
    }
    private sealed record RetainedFile(FileStream Stream, CoverageRunOutputLease Root, string Name,
        string Identity, byte[] Bytes, string Hash);
    private sealed record AdmittedDocument(XDocument Document, bool HadDoctype);

    private sealed class BoundedXmlOutput(long maximum, CancellationToken cancellationToken) : MemoryStream
    {
        private void Admit(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count > maximum - Position)
                throw new InvalidDataException("Fan-in serialized XML exceeds its admitted output budget.");
        }
        public override void Write(byte[] buffer, int offset, int count)
        { Admit(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Admit(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { Admit(1); base.WriteByte(value); }
    }
}
