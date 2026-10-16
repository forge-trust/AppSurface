# SDK-only package content hash helper

This native-harness prerequisite computes a downloaded package's NuGet content hash with the selected SDK's `PackageArchiveReader.GetContentHash`. It references only SDK NuGet assemblies, with no package or project dependencies and no subject evaluation. Build it with the same selected SDK before acquiring subject inputs.

Invocation is `dotnet NuGetContentHash.dll <downloaded-package>`. One nonempty archive no larger than128MiB is read through a retained stream. A successful invocation emits exactly `content_hash`, `archive_sha512`, and `bytes` as bounded JSON. The Python downloader compares the signed-aware content hash with the unchanged lock and separately compares the raw archive hash and byte length with its original downloaded memory before extraction. These outputs do not grant execution or artifact authority.

Signed package content hashes normalize out the package signature; a complete signed ZIP SHA-512 need not equal its lock content hash. See [NuGet PackageArchiveReader](https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs) and [archive normalization](https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/Signing/Archive/SignedPackageArchiveUtility.cs). Unsigned archives use their complete hash. The `.nupkg.sha512` cache file stores the raw archive hash; `.nupkg.metadata` stores the NuGet content hash.

Wrong arguments, malformed archives, excessive size or read/hash failures exit65 with a fixed message and no path or exception echo. An external prerequisite deadline owns this small process. It loads no subject assembly, MSBuild project, runtime admission or supervisor capability.
