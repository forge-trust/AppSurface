using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

internal static class CoverageAsyncMethodMetadata
{
    private static readonly Guid Sha256DocumentHash = new("8829d00f-11b8-4213-878b-770e8597ac16");

    internal static IReadOnlyList<CoverageAsyncMethod> Read(byte[] assembly, byte[] pdb, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(pdb);
        CoverageFanInComposer.ValidateRepositoryRoot(sourceRoot);
        try
        {
            using var pe = new PEReader(new MemoryStream(assembly, writable: false));
            if (!pe.HasMetadata) throw new InvalidDataException("Compiler assembly has no metadata.");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) throw new InvalidDataException("Compiler symbol input is not an assembly.");
            using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb, writable: false));
            var debug = provider.GetMetadataReader();
            if (!IsBound(pe, debug, pdb)) throw new InvalidDataException("Portable PDB does not match its assembly.");

            var candidates = new List<(MethodDefinitionHandle MoveNext, MethodDefinitionHandle Kickoff)>();
            foreach (var handle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(handle);
                if (metadata.GetString(method.Name) != "MoveNext") continue;
                var debugInfo = debug.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(
                    MetadataTokens.GetRowNumber(handle)));
                var kickoff = debugInfo.GetStateMachineKickoffMethod();
                if (!kickoff.IsNil) candidates.Add((handle, kickoff));
            }

            var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            var results = new List<CoverageAsyncMethod>();
            foreach (var group in candidates.GroupBy(static candidate => candidate.Kickoff))
            {
                if (group.Count() != 1) continue;
                var candidate = group.Single();
                var moveNext = metadata.GetMethodDefinition(candidate.MoveNext);
                var moveNextOwner = moveNext.GetDeclaringType();
                var moveNextType = metadata.GetTypeDefinition(moveNextOwner);
                if (moveNextType.GetMethods().Count(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "MoveNext") != 1
                    || !HasMoveNextSignature(metadata, moveNext.Signature)
                    || moveNext.RelativeVirtualAddress == 0) continue;

                var kickoff = metadata.GetMethodDefinition(candidate.Kickoff);
                var kickoffOwner = kickoff.GetDeclaringType();
                var sourceMethod = metadata.GetString(kickoff.Name);
                var sourceOwner = metadata.GetTypeDefinition(kickoffOwner);
                if (kickoff.GetGenericParameters().Count != 0
                    || sourceOwner.GetMethods().Count(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == sourceMethod) != 1
                    || !TryGetTypeName(metadata, kickoffOwner, out var sourceType)
                    || !TryGetTypeName(metadata, moveNextOwner, out var stateMachineType)
                    || !HasAsyncStateMachineAttribute(metadata, kickoff, stateMachineType)) continue;

                if (!TryGetSequencePoints(debug, candidate.MoveNext, sourceRoot, out var file, out var lines,
                    out var checksum, out var pointShape)) continue;
                var body = pe.GetMethodBody(moveNext.RelativeVirtualAddress);
                var il = body.GetILBytes();
                if (il is not { Length: > 0 }) continue;

                var fingerprint = Fingerprint(assemblyName, sourceType, sourceMethod, stateMachineType, file,
                    checksum, metadata.GetBlobBytes(kickoff.Signature), metadata.GetBlobBytes(moveNext.Signature),
                    il, body, pointShape);
                results.Add(new CoverageAsyncMethod(assemblyName, sourceType, sourceMethod, stateMachineType,
                    file, lines, fingerprint));
            }

            return results.OrderBy(static method => method.File, StringComparer.Ordinal)
                .ThenBy(static method => method.SourceType, StringComparer.Ordinal)
                .ThenBy(static method => method.SourceMethod, StringComparer.Ordinal).ToArray();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException("Compiler symbol metadata could not be read.", exception);
        }
    }

    private static bool IsBound(PEReader pe, MetadataReader pdb, byte[] pdbBytes)
    {
        var header = pdb.DebugMetadataHeader;
        var directory = pe.ReadDebugDirectory();
        var codeView = directory.Where(static entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
        var checksums = directory.Where(static entry => entry.Type == DebugDirectoryEntryType.PdbChecksum).ToArray();
        if (header is null || header.Id.Length != 20 || codeView.Length != 1 || checksums.Length != 1) return false;

        var id = header.Id.AsSpan();
        var data = pe.ReadCodeViewDebugDirectoryData(codeView[0]);
        if (data.Age != 1 || data.Guid != new Guid(id[..16])
            || codeView[0].Stamp != System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(id[16..])) return false;
        var checksum = pe.ReadPdbChecksumDebugDirectoryData(checksums[0]);
        if (checksum.AlgorithmName != "SHA256" || checksum.Checksum.Length != SHA256.HashSizeInBytes) return false;

        // The compiler hashes the portable PDB before filling the content ID with a value derived from that hash.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(pdbBytes.AsSpan(0, header.IdStartOffset));
        hash.AppendData(new byte[20]);
        hash.AppendData(pdbBytes.AsSpan(header.IdStartOffset + 20));
        return hash.GetHashAndReset().AsSpan().SequenceEqual(checksum.Checksum.AsSpan());
    }

    private static bool HasAsyncStateMachineAttribute(MetadataReader metadata, MethodDefinition kickoff, string stateMachineType)
    {
        var count = 0;
        var match = false;
        foreach (var handle in kickoff.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
            if (metadata.GetString(type.Namespace) != "System.Runtime.CompilerServices"
                || metadata.GetString(type.Name) != "AsyncStateMachineAttribute"
                || type.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            if (metadata.GetString(assembly.Name) is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib" or "netstandard")) continue;
            count++;
            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) continue;
            var serializedName = value.ReadSerializedString();
            if (serializedName is null || value.ReadUInt16() != 0 || value.RemainingBytes != 0) continue;
            match = string.Equals(serializedName.Split(',', 2)[0].Trim().Replace('+', '/'), stateMachineType, StringComparison.Ordinal);
        }

        return count == 1 && match;
    }

    private static bool TryGetSequencePoints(MetadataReader pdb, MethodDefinitionHandle moveNext, string sourceRoot,
        out string file, out IReadOnlyList<int> lines, out string checksum, out string pointShape)
    {
        file = checksum = pointShape = string.Empty;
        lines = [];
        var info = pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(moveNext)));
        var points = info.GetSequencePoints().ToArray();
        if (points.Length == 0) return false;
        var documentHandle = default(DocumentHandle);
        var visibleLines = new SortedSet<int>();
        var shape = new StringBuilder();
        foreach (var point in points)
        {
            var current = point.Document.IsNil ? info.Document : point.Document;
            if (current.IsNil || (!documentHandle.IsNil && current != documentHandle)) return false;
            documentHandle = current;
            if (point.IsHidden) shape.Append(point.Offset).Append(":h;");
            else
            {
                if (point.StartLine <= 0 || point.EndLine < point.StartLine) return false;
                visibleLines.Add(point.StartLine);
                shape.Append(point.Offset).Append(':').Append(point.StartLine).Append(':').Append(point.StartColumn)
                    .Append(':').Append(point.EndLine).Append(':').Append(point.EndColumn).Append(';');
            }
        }

        var document = pdb.GetDocument(documentHandle);
        var digest = pdb.GetBlobBytes(document.Hash);
        var documentName = pdb.GetString(document.Name);
        if (visibleLines.Count == 0 || pdb.GetGuid(document.HashAlgorithm) != Sha256DocumentHash || digest.Length != 32)
            return false;
        try
        {
            // Deterministic SDK builds map the checkout source root to /_/.
            var documentRoot = documentName.StartsWith("/_/", StringComparison.Ordinal) ? "/_" : sourceRoot;
            file = CoverageFanInComposer.GetRepositoryRelativePath(documentName, documentRoot);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        lines = visibleLines.ToArray();
        checksum = Convert.ToHexString(digest);
        pointShape = shape.ToString();
        return true;
    }

    private static bool HasMoveNextSignature(MetadataReader metadata, BlobHandle signatureHandle)
    {
        var signature = metadata.GetBlobReader(signatureHandle);
        return signature.RemainingBytes >= 3
            && (signature.ReadByte() & 0x6f) == 0x20
            && signature.ReadCompressedInteger() == 0
            && signature.ReadByte() == 1
            && signature.RemainingBytes == 0;
    }

    private static string Fingerprint(string assembly, string type, string method, string stateMachine, string file,
        string checksum, byte[] kickoffSignature, byte[] moveNextSignature, byte[] il, MethodBodyBlock body, string points)
    {
        var shape = new StringBuilder();
        foreach (var value in new[] { assembly, type, method, stateMachine, file, checksum,
                     Convert.ToHexString(kickoffSignature), Convert.ToHexString(moveNextSignature),
                     Convert.ToHexString(SHA256.HashData(il)), points, ExceptionShape(body) })
            shape.Append(value.Length).Append(':').Append(value).Append(';');
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString())));
    }

    private static string ExceptionShape(MethodBodyBlock body) => string.Join(';', body.ExceptionRegions.Select(region =>
        $"{(int)region.Kind}:{region.TryOffset}:{region.TryLength}:{region.HandlerOffset}:{region.HandlerLength}:{region.FilterOffset}:{MetadataTokens.GetToken(region.CatchType)}"));

    private static bool TryGetTypeName(MetadataReader metadata, TypeDefinitionHandle handle, out string name)
    {
        var parts = new Stack<string>();
        string namespaceName;
        while (true)
        {
            var type = metadata.GetTypeDefinition(handle);
            parts.Push(metadata.GetString(type.Name));
            var parent = type.GetDeclaringType();
            if (parent.IsNil)
            {
                namespaceName = metadata.GetString(type.Namespace);
                break;
            }
            handle = parent;
        }
        name = string.Join('/', parts);
        if (namespaceName.Length != 0) name = namespaceName + "." + name;
        return name.Length != 0;
    }
}

internal sealed record CoverageAsyncMethod(string Assembly, string SourceType, string SourceMethod,
    string StateMachineType, string File, IReadOnlyList<int> Lines, string Fingerprint);
