using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

public sealed partial class PlatformAppSurfaceLocalSecretStore
{
    /// <summary>
    /// Implements the macOS v2 write-forward bridge while preserving v1 as a read-only recovery source.
    /// </summary>
    /// <remarks>
    /// V2 uses <c>SecItem</c> without a data-protection attribute, access group, or custom ACL. This intentionally
    /// targets the entitlement-free file-based Keychain configuration proven by the macOS feasibility spike. The v1
    /// store remains available only to identify records that require explicit operator migration.
    /// </remarks>
    internal sealed class MacOsV2CompatibilityLocalSecretStore : IAppSurfaceLocalSecretStore, IAppSurfaceLocalSecretMetadataStore, IAppSurfaceLocalSecretMigrationStore
    {
        private const string IndexKey = "__appsurface_index__";
        private const string DoctorKey = "__appsurface_doctor__";
        private const string StoreName = "macOS Keychain (v2)";
        private const int ErrSecSuccess = 0;
        private const int ErrSecDuplicateItem = -25299;
        private const int ErrSecItemNotFound = -25300;
        private const int ErrSecInteractionNotAllowed = -25308;
        private const int ErrSecAuthFailed = -25293;
        private const int ErrSecUserCanceled = -128;
        private const int ErrSecMissingEntitlement = -34018;
        private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(5);

        private readonly IAppSurfaceLocalSecretStore _legacy;
        private readonly IAppSurfaceLocalSecretMetadataStore? _legacyMetadata;
        private readonly IMacOsSecItemInterop _interop;
        private readonly AppSurfaceLocalSecretIdentityNormalizer _normalizer;

        internal MacOsV2CompatibilityLocalSecretStore(
            IAppSurfaceLocalSecretStore legacy,
            IMacOsSecItemInterop interop,
            AppSurfaceLocalSecretIdentityNormalizer? normalizer = null)
        {
            ArgumentNullException.ThrowIfNull(legacy);
            ArgumentNullException.ThrowIfNull(interop);

            _legacy = legacy;
            _legacyMetadata = legacy as IAppSurfaceLocalSecretMetadataStore;
            _interop = interop;
            _normalizer = normalizer ?? new AppSurfaceLocalSecretIdentityNormalizer();
        }

        /// <inheritdoc />
        public string Name => StoreName;

        /// <inheritdoc />
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            var index = ReadV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            if (index.Status != LocalSecretResultStatus.Found) return AppSurfaceLocalSecretResult.NotFound(index.Status, index.Diagnostic!, Name);
            var matches = index.Keys.Where(key => StringComparer.OrdinalIgnoreCase.Equals(key, identity.Key.Value)).ToArray();
            if (matches.Length > 1) return AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic("config-key-collision", "Local secret identities collide.",
                    "The native index contains case-only spellings of one logical key.", "Migrate or remove duplicate exact records.", "local-secrets-migration"), Name);
            if (matches.Length == 1) identity = identity with { Key = ForgeTrust.AppSurface.Config.AppSurfaceConfigKey.Parse(matches[0]) };
            var v2 = ReadV2(identity);
            if (v2.Status != LocalSecretResultStatus.Missing)
            {
                return v2;
            }

            if (!TryAcquire(identity, out var mutex, out var failure))
            {
                return failure!;
            }

            try
            {
                v2 = ReadV2(identity);
                if (v2.Status != LocalSecretResultStatus.Missing)
                {
                    return v2;
                }

                var legacy = _legacy.Get(identity);
                return legacy.Status switch
                {
                    LocalSecretResultStatus.Found => MigrationRequired(identity),
                    LocalSecretResultStatus.Missing => AppSurfaceLocalSecretResult.Missing(Name),
                    _ => legacy
                };
            }
            finally
            {
                Release(mutex!);
            }
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) =>
            PlatformLocalSecretMaintenanceLease.Run(
                () => PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, identity.ApplicationName, identity.Environment, identity.KeyPrefix, AppSurfaceLocalSecretMigrationCoordinator.DefaultLeaseTimeout, CancellationToken.None),
                () => SetUnderLease(identity, value), diagnostic => AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.Unavailable, diagnostic, Name));

        private AppSurfaceLocalSecretResult SetUnderLease(AppSurfaceLocalSecretIdentity identity, string value)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(value);


            if (!TryAcquire(identity, out var mutex, out var failure))
            {
                return failure!;
            }

            try
            {
                var index = ReadV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
                if (index.Status != LocalSecretResultStatus.Found)
                {
                    return AppSurfaceLocalSecretResult.NotFound(index.Status, index.Diagnostic!, Name);
                }

                var matches = index.Keys.Where(key => StringComparer.OrdinalIgnoreCase.Equals(key, identity.Key.Value)).ToArray();
                if (matches.Length > 1)
                {
                    return AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.ProviderFailed,
                        new AppSurfaceLocalSecretDiagnostic("config-key-collision", "Local secret identities collide.",
                            "The native index contains case-only spellings of one logical key.",
                            "Migrate or remove duplicate exact records.", "local-secrets-migration"), Name);
                }

                // SecItem account names retain their existing exact spelling for case-insensitive logical updates.
                if (matches.Length == 1) identity = identity with { Key = AppSurfaceConfigKey.Parse(matches[0]) };
                var write = WriteV2(identity, value);
                if (write.Status != LocalSecretResultStatus.Found)
                {
                    return write;
                }

                var verify = ReadV2(identity);
                if (verify.Status != LocalSecretResultStatus.Found || !string.Equals(verify.Value, value, StringComparison.Ordinal))
                {
                    return verify.Status == LocalSecretResultStatus.Found
                        ? VerificationFailed()
                        : verify;
                }

                return EnsureV2IndexContains(identity);
            }
            finally
            {
                Release(mutex!);
            }
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) =>
            PlatformLocalSecretMaintenanceLease.Run(
                () => PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, identity.ApplicationName, identity.Environment, identity.KeyPrefix, AppSurfaceLocalSecretMigrationCoordinator.DefaultLeaseTimeout, CancellationToken.None),
                () => DeleteUnderLease(identity), diagnostic => AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.Unavailable, diagnostic, Name));

        private AppSurfaceLocalSecretResult DeleteUnderLease(AppSurfaceLocalSecretIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);


            if (!TryAcquire(identity, out var mutex, out var failure))
            {
                return failure!;
            }

            try
            {
                // Validate both storage versions before either destructive call. Logical deletion must agree with Get.
                var v2Index = ReadV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
                if (v2Index.Status != LocalSecretResultStatus.Found)
                    return AppSurfaceLocalSecretResult.NotFound(v2Index.Status, v2Index.Diagnostic!, Name);
                var v2Matches = v2Index.Keys.Where(key => StringComparer.OrdinalIgnoreCase.Equals(key, identity.Key.Value)).ToArray();
                if (v2Matches.Length > 1) return LogicalDeleteCollision();

                var legacyIndex = _legacy is IndexedLocalSecretStore indexed
                    ? indexed.ReadIndexForMigration(identity.ApplicationName, identity.Environment, identity.KeyPrefix)
                    : ReadLegacyDeleteIndex(identity);
                if (legacyIndex.Status != LocalSecretResultStatus.Found)
                    return AppSurfaceLocalSecretResult.NotFound(legacyIndex.Status, legacyIndex.Diagnostic!, Name);
                var legacyMatches = legacyIndex.Keys.Where(key => StringComparer.OrdinalIgnoreCase.Equals(key, identity.Key.Value)).ToArray();
                if (legacyMatches.Length > 1) return LogicalDeleteCollision();
                var v2Identity = v2Matches.Length == 1 ? identity with { Key = AppSurfaceConfigKey.Parse(v2Matches[0]) } : identity;
                var legacyIdentity = legacyMatches.Length == 1
                    ? _normalizer.Normalize(identity.ApplicationName, identity.Environment, identity.KeyPrefix, legacyMatches[0]).Identity!
                    : identity;

                var v2 = DeleteV2(v2Identity);
                if (v2.Status is not (LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing))
                {
                    return v2;
                }

                var legacy = _legacy.Delete(legacyIdentity);
                if (legacy.Status is not (LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing))
                {
                    return legacy;
                }

                var index = RemoveFromV2Index(v2Identity);
                if (index.Status != LocalSecretResultStatus.Found)
                {
                    return index;
                }

                return v2.Status == LocalSecretResultStatus.Found || legacy.Status == LocalSecretResultStatus.Found
                    ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                    : AppSurfaceLocalSecretResult.Missing(Name);
            }
            finally
            {
                Release(mutex!);
            }
        }

        /// <summary>Reads a non-indexed compatibility adapter's logical names before a public delete.</summary>
        private LocalSecretIndexReadResult ReadLegacyDeleteIndex(AppSurfaceLocalSecretIdentity identity)
        {
            var listed = _legacy.List(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            return new LocalSecretIndexReadResult(listed.Status, listed.Keys, false, listed.Diagnostic);
        }

        private AppSurfaceLocalSecretResult LogicalDeleteCollision() =>
            AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic("config-key-collision", "Local secret identities collide.",
                    "A native index contains case-only spellings of one logical key.",
                    "Migrate or remove duplicate exact records.", "local-secrets-migration"), Name);

        /// <inheritdoc />
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            PlatformLocalSecretMaintenanceLease.Run(
                () => PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, applicationName, environment, keyPrefix, AppSurfaceLocalSecretMigrationCoordinator.DefaultLeaseTimeout, CancellationToken.None),
                () => ListUnderLease(applicationName, environment, keyPrefix), diagnostic => AppSurfaceLocalSecretListResult.Failed(LocalSecretResultStatus.Unavailable, diagnostic, Name));

        private AppSurfaceLocalSecretListResult ListUnderLease(string applicationName, string environment, string? keyPrefix)
        {
            var namespaceIdentity = NamespaceIdentity(applicationName, environment, keyPrefix);
            if (!TryAcquire(namespaceIdentity, out var mutex, out var failure))
            {
                return AppSurfaceLocalSecretListResult.Failed(failure!.Status, failure.Diagnostic!, Name);
            }

            try
            {
                var v2Index = ReadV2Index(applicationName, environment, keyPrefix);
                if (v2Index.Status != LocalSecretResultStatus.Found)
                {
                    return AppSurfaceLocalSecretListResult.Failed(v2Index.Status, v2Index.Diagnostic!, Name);
                }

                var legacy = _legacy.List(applicationName, environment, keyPrefix);
                if (legacy.Status != LocalSecretResultStatus.Found)
                {
                    return AppSurfaceLocalSecretListResult.Failed(legacy.Status, legacy.Diagnostic!, legacy.Source);
                }

                var allKeys = v2Index.Keys.Concat(legacy.Keys).ToHashSet(StringComparer.Ordinal);
                var liveV2Keys = new HashSet<string>(StringComparer.Ordinal);
                var liveKeys = new HashSet<string>(StringComparer.Ordinal);
                var needsRepair = v2Index.NeedsRepair;
                foreach (var key in allKeys)
                {
                    var normalized = _normalizer.Normalize(applicationName, environment, keyPrefix, key);
                    if (!normalized.Succeeded)
                    {
                        return AppSurfaceLocalSecretListResult.Failed(
                            LocalSecretResultStatus.ProviderFailed,
                            InvalidIndex("The v2 index contains an invalid local secret key."),
                            Name);
                    }

                    var identity = normalized.Identity!;
                    var v2 = ExistsV2(identity);
                    if (v2.Status == LocalSecretResultStatus.Found)
                    {
                        liveV2Keys.Add(key);
                        liveKeys.Add(key);
                        continue;
                    }

                    if (v2.Status != LocalSecretResultStatus.Missing)
                    {
                        return AppSurfaceLocalSecretListResult.Failed(v2.Status, v2.Diagnostic!, Name);
                    }

                    var v1 = _legacy.Get(identity);
                    if (v1.Status == LocalSecretResultStatus.Found)
                    {
                        liveKeys.Add(key);
                        continue;
                    }

                    if (v1.Status == LocalSecretResultStatus.Missing)
                    {
                        needsRepair = true;
                        continue;
                    }

                    return AppSurfaceLocalSecretListResult.Failed(v1.Status, v1.Diagnostic!, v1.Source);
                }

                if (needsRepair)
                {
                    var repair = WriteV2Index(applicationName, environment, keyPrefix, liveV2Keys);
                    if (repair.Status != LocalSecretResultStatus.Found)
                    {
                        return AppSurfaceLocalSecretListResult.Failed(repair.Status, repair.Diagnostic!, Name);
                    }
                }

                return AppSurfaceLocalSecretListResult.Found(liveKeys, Name);
            }
            finally
            {
                Release(mutex!);
            }
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretResult Probe(AppSurfaceLocalSecretIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            var v2Index = ReadV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            if (v2Index.Status != LocalSecretResultStatus.Found)
            {
                return AppSurfaceLocalSecretResult.NotFound(v2Index.Status, v2Index.Diagnostic!, Name);
            }

            if (identity.MigrationStoredKey is not null)
            {
                if (v2Index.Keys.Contains(identity.StoredKey, StringComparer.Ordinal))
                    return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
                return identity.StorageName.StartsWith("appsurface:v2:", StringComparison.Ordinal)
                    ? AppSurfaceLocalSecretResult.Missing(Name)
                    : _legacyMetadata?.Probe(identity) ?? AppSurfaceLocalSecretResult.Missing(Name);
            }

            var matches = v2Index.Keys.Where(key => StringComparer.OrdinalIgnoreCase.Equals(key, identity.StoredKey)).ToArray();
            if (matches.Length > 1)
            {
                return AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.ProviderFailed,
                    new AppSurfaceLocalSecretDiagnostic("config-key-collision", "Local secret identities collide.",
                        "The native index contains case-only spellings of one logical key.",
                        "Migrate or remove duplicate exact records.", "local-secrets-migration"), Name);
            }

            if (matches.Length == 1)
            {
                return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
            }

            return _legacyMetadata?.Probe(identity) ?? AppSurfaceLocalSecretResult.Missing(Name);
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix)
        {
            var probe = ReadV2(NamespaceIdentity(applicationName, environment, keyPrefix));
            return probe.Status switch
            {
                LocalSecretResultStatus.Missing or LocalSecretResultStatus.Found => AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Missing,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-ready",
                        "macOS v2 LocalSecrets store is ready.",
                        "The entitlement-free file-based SecItem Keychain path is available for the current user session.",
                        "Set a secret with `appsurface secrets set` for this pinned namespace.",
                        "local-secrets-macos-migration"),
                    Name),
                _ => probe
            };
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretMigrationResult Migrate(string applicationName, string environment, string? keyPrefix) =>
            PlatformLocalSecretMaintenanceLease.Run(
                () => PlatformLocalSecretMaintenanceLease.Acquire(PlatformLocalSecretStatePaths.DefaultDirectory, applicationName, environment, keyPrefix, AppSurfaceLocalSecretMigrationCoordinator.DefaultLeaseTimeout, CancellationToken.None),
                () => MigrateUnderLease(applicationName, environment, keyPrefix), diagnostic => AppSurfaceLocalSecretMigrationResult.FailedToStart(LocalSecretResultStatus.Unavailable, diagnostic, Name));

        private AppSurfaceLocalSecretMigrationResult MigrateUnderLease(string applicationName, string environment, string? keyPrefix)
        {
            var namespaceIdentity = NamespaceIdentity(applicationName, environment, keyPrefix);
            if (!TryAcquire(namespaceIdentity, out var mutex, out var failure))
            {
                return AppSurfaceLocalSecretMigrationResult.FailedToStart(failure!.Status, failure.Diagnostic!, Name);
            }

            try
            {
                var v2Index = ReadV2Index(applicationName, environment, keyPrefix);
                if (v2Index.Status != LocalSecretResultStatus.Found)
                {
                    return AppSurfaceLocalSecretMigrationResult.FailedToStart(v2Index.Status, v2Index.Diagnostic!, Name);
                }

                var legacy = _legacy.List(applicationName, environment, keyPrefix);
                if (legacy.Status != LocalSecretResultStatus.Found)
                {
                    return AppSurfaceLocalSecretMigrationResult.FailedToStart(legacy.Status, legacy.Diagnostic!, legacy.Source);
                }

                var rows = new List<AppSurfaceLocalSecretMigrationRow>();
                foreach (var key in legacy.Keys)
                {
                    var normalized = _normalizer.Normalize(applicationName, environment, keyPrefix, key);
                    if (!normalized.Succeeded)
                    {
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(
                            key,
                            AppSurfaceLocalSecretMigrationAction.Failed,
                            LocalSecretResultStatus.ProviderFailed,
                            InvalidIndex("The legacy index contains an invalid local secret key.")));
                        continue;
                    }

                    var identity = normalized.Identity!;
                    var v2 = ReadV2(identity);
                    if (v2.Status == LocalSecretResultStatus.Found)
                    {
                        var index = EnsureV2IndexContains(identity);
                        rows.Add(index.Status == LocalSecretResultStatus.Found
                            ? new AppSurfaceLocalSecretMigrationRow(key, AppSurfaceLocalSecretMigrationAction.AlreadyV2, LocalSecretResultStatus.Found, null)
                            : new AppSurfaceLocalSecretMigrationRow(key, AppSurfaceLocalSecretMigrationAction.Failed, index.Status, index.Diagnostic));
                        continue;
                    }

                    if (v2.Status != LocalSecretResultStatus.Missing)
                    {
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, AppSurfaceLocalSecretMigrationAction.Failed, v2.Status, v2.Diagnostic));
                        continue;
                    }

                    var v1 = _legacy.Get(identity);
                    if (v1.Status != LocalSecretResultStatus.Found || v1.Value == null)
                    {
                        var status = v1.Status == LocalSecretResultStatus.Found
                            ? LocalSecretResultStatus.ProviderFailed
                            : v1.Status;
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(
                            key,
                            AppSurfaceLocalSecretMigrationAction.Failed,
                            status,
                            v1.Diagnostic ?? InvalidLegacyRead()));
                        continue;
                    }

                    var write = WriteV2(identity, v1.Value);
                    if (write.Status != LocalSecretResultStatus.Found)
                    {
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, AppSurfaceLocalSecretMigrationAction.Failed, write.Status, write.Diagnostic));
                        continue;
                    }

                    var verify = ReadV2(identity);
                    if (verify.Status != LocalSecretResultStatus.Found || !string.Equals(verify.Value, v1.Value, StringComparison.Ordinal))
                    {
                        var failed = verify.Status == LocalSecretResultStatus.Found ? VerificationFailed() : verify;
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, AppSurfaceLocalSecretMigrationAction.Failed, failed.Status, failed.Diagnostic));
                        continue;
                    }

                    var indexWrite = EnsureV2IndexContains(identity);
                    rows.Add(indexWrite.Status == LocalSecretResultStatus.Found
                        ? new AppSurfaceLocalSecretMigrationRow(key, AppSurfaceLocalSecretMigrationAction.Migrated, LocalSecretResultStatus.Found, null)
                        : new AppSurfaceLocalSecretMigrationRow(key, AppSurfaceLocalSecretMigrationAction.Failed, indexWrite.Status, indexWrite.Diagnostic));
                }

                return AppSurfaceLocalSecretMigrationResult.Completed(rows, Name);
            }
            finally
            {
                Release(mutex!);
            }
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretIdentityResult GetKeyMigrationDestinationIdentity(
            string applicationName, string environment, string? keyPrefix, AppSurfaceConfigKey destinationKey)
        {
            ArgumentNullException.ThrowIfNull(destinationKey);
            var normalized = _normalizer.Normalize(applicationName, environment, keyPrefix, destinationKey.Value);
            if (normalized.Identity is not { } identity) return normalized;
            return AppSurfaceLocalSecretIdentityResult.Valid(identity with
            {
                StorageName = $"appsurface:v2:{identity.ApplicationName}:{identity.Environment}:{identity.KeyPrefix}:{identity.Key.Value}"
            });
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretKeyMigrationResult MigrateKey(
            string applicationName, string environment, string? keyPrefix, string sourceStoredKey,
            ForgeTrust.AppSurface.Config.AppSurfaceConfigKey destinationKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
            ArgumentException.ThrowIfNullOrWhiteSpace(environment);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceStoredKey);
            ArgumentNullException.ThrowIfNull(destinationKey);

            var normalized = GetKeyMigrationDestinationIdentity(applicationName, environment, keyPrefix, destinationKey);
            if (!normalized.Succeeded || normalized.Identity is null)
            {
                return AppSurfaceLocalSecretKeyMigrationResult.Failed(
                    LocalSecretResultStatus.InvalidIdentity, "invalid", AppSurfaceLocalSecretMigrationState.Prepared,
                    sourceStoredKey, destinationKey, normalized.Diagnostic!, Name);
            }

            var destination = normalized.Identity;
            var sourceIsV2 = sourceStoredKey.StartsWith("appsurface:v2:", StringComparison.Ordinal);
            var source = sourceIsV2
                ? ResolveV2Source(applicationName, environment, keyPrefix, sourceStoredKey)
                : ResolveLegacySource(applicationName, environment, keyPrefix, sourceStoredKey);
            if (source is null)
            {
                return AppSurfaceLocalSecretKeyMigrationResult.Failed(
                    LocalSecretResultStatus.InvalidIdentity, "invalid-source", AppSurfaceLocalSecretMigrationState.Prepared,
                    sourceStoredKey, destinationKey,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-migration-source-invalid", "The exact source identifier is invalid.",
                        "The source identifier does not belong to the requested LocalSecrets namespace.",
                        "Use the exact stored identifier reported by `appsurface secrets doctor`.", "local-secrets-migration"), Name);
            }

            return AppSurfaceLocalSecretMigrationCoordinator.Run(
                new MacOsV2MigrationBackend(this, source, destination, sourceIsV2),
                applicationName, environment, keyPrefix, sourceStoredKey, destination.StorageName,
                destinationKey, Name);
        }

        private static AppSurfaceLocalSecretIdentity? ResolveLegacySource(string applicationName, string environment, string? keyPrefix, string storedKey) =>
            LocalSecretMigrationIdentity.Resolve(applicationName, environment, keyPrefix, storedKey, v2: false);

        private static AppSurfaceLocalSecretIdentity? ResolveV2Source(string applicationName, string environment, string? keyPrefix, string storedKey) =>
            LocalSecretMigrationIdentity.Resolve(applicationName, environment, keyPrefix, storedKey, v2: true);

        /// <summary>Whether the retained legacy adapter exposes exact native reads, deletes and index publication.</summary>
        internal bool SupportsExactLegacyMigration => _legacy is IndexedLocalSecretStore;

        /// <summary>Reads a migration record without applying logical-key or index lookup policy.</summary>
        internal AppSurfaceLocalSecretResult ReadMigrationValue(AppSurfaceLocalSecretIdentity identity, bool v2) =>
            v2 ? ReadV2(identity) : _legacy is IndexedLocalSecretStore indexed
                ? indexed.ReadRaw(identity) : throw new IOException("The legacy store cannot confirm an exact native read.");
        internal AppSurfaceLocalSecretResult WriteMigrationValue(AppSurfaceLocalSecretIdentity identity, string value) => WriteV2(identity, value);
        internal AppSurfaceLocalSecretResult DeleteMigrationValue(AppSurfaceLocalSecretIdentity identity, bool v2) =>
            v2 ? DeleteV2(identity) : _legacy is IndexedLocalSecretStore indexed
                ? indexed.DeleteRaw(identity) : throw new IOException("The legacy store cannot confirm an exact native delete.");
        internal void PublishLegacyMigrationIndex(AppSurfaceLocalSecretIdentity source)
        {
            if (_legacy is not IndexedLocalSecretStore indexed) return;
            var index = indexed.ReadIndexForMigration(source.ApplicationName, source.Environment, source.KeyPrefix);
            if (index.Status != LocalSecretResultStatus.Found) throw new IOException("The legacy index could not be read.");
            var write = indexed.WriteIndexForMigration(source.ApplicationName, source.Environment, source.KeyPrefix,
                index.Keys.Where(key => !StringComparer.Ordinal.Equals(key, source.StoredKey)));
            if (write.Status != LocalSecretResultStatus.Found) throw new IOException("The legacy index could not be published.");
        }

        internal LocalSecretIndexReadResult ReadMigrationIndex(string applicationName, string environment, string? keyPrefix) => ReadV2Index(applicationName, environment, keyPrefix);
        internal AppSurfaceLocalSecretResult WriteMigrationIndex(string applicationName, string environment, string? keyPrefix, IEnumerable<string> keys) => WriteV2Index(applicationName, environment, keyPrefix, keys);

        private AppSurfaceLocalSecretResult ReadV2(AppSurfaceLocalSecretIdentity identity)
        {
            var read = _interop.Read(V2Item(identity));
            if (read.Status != ErrSecSuccess)
            {
                return MapStatus(read.Status, "read");
            }

            if (read.Data == null)
            {
                return InvalidRead();
            }

            var data = read.Data;
            try
            {
                return AppSurfaceLocalSecretResult.Found(Encoding.UTF8.GetString(data), Name);
            }
            finally
            {
                Array.Clear(data);
            }
        }

        private AppSurfaceLocalSecretResult ExistsV2(AppSurfaceLocalSecretIdentity identity)
        {
            var status = _interop.Exists(V2Item(identity));
            return status == ErrSecSuccess
                ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                : MapStatus(status, "presence check");
        }

        private AppSurfaceLocalSecretResult WriteV2(AppSurfaceLocalSecretIdentity identity, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                var item = V2Item(identity);
                var status = _interop.Add(item, bytes);
                if (status == ErrSecDuplicateItem)
                {
                    status = _interop.Update(item, bytes);
                }

                return status == ErrSecSuccess
                    ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                    : MapStatus(status, "write");
            }
            finally
            {
                Array.Clear(bytes);
            }
        }

        private AppSurfaceLocalSecretResult DeleteV2(AppSurfaceLocalSecretIdentity identity)
        {
            var status = _interop.Delete(V2Item(identity));
            return status == ErrSecSuccess
                ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                : MapStatus(status, "delete");
        }

        private AppSurfaceLocalSecretResult EnsureV2IndexContains(AppSurfaceLocalSecretIdentity identity)
        {
            var index = ReadV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            if (index.Status != LocalSecretResultStatus.Found)
            {
                return AppSurfaceLocalSecretResult.NotFound(index.Status, index.Diagnostic!, Name);
            }

            var keys = index.Keys.ToHashSet(StringComparer.Ordinal);
            return keys.Add(identity.StoredKey) || index.NeedsRepair
                ? WriteV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix, keys)
                : AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        private AppSurfaceLocalSecretResult RemoveFromV2Index(AppSurfaceLocalSecretIdentity identity)
        {
            var index = ReadV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            if (index.Status != LocalSecretResultStatus.Found)
            {
                return AppSurfaceLocalSecretResult.NotFound(index.Status, index.Diagnostic!, Name);
            }

            var keys = index.Keys.ToHashSet(StringComparer.Ordinal);
            return keys.Remove(identity.StoredKey) || index.NeedsRepair
                ? WriteV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix, keys)
                : AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        private LocalSecretIndexReadResult ReadV2Index(string applicationName, string environment, string? keyPrefix)
            => ReadLocalSecretIndex(
                () => ReadV2(IndexIdentity(applicationName, environment, keyPrefix)),
                IndexKey,
                InvalidIndex,
                "The v2 index entry could not be parsed.");

        private AppSurfaceLocalSecretResult WriteV2Index(string applicationName, string environment, string? keyPrefix, IEnumerable<string> keys) =>
            WriteV2(
                IndexIdentity(applicationName, environment, keyPrefix),
                SerializeLocalSecretIndex(keys));

        private static AppSurfaceLocalSecretIdentity IndexIdentity(string applicationName, string environment, string? keyPrefix) =>
            new(applicationName, environment, keyPrefix, AppSurfaceConfigKey.FromSegments(IndexKey), $"appsurface:v2:{applicationName}:{environment}:{keyPrefix}:{IndexKey}");

        private static AppSurfaceLocalSecretIdentity NamespaceIdentity(string applicationName, string environment, string? keyPrefix) =>
            new(applicationName, environment, keyPrefix, AppSurfaceConfigKey.FromSegments(DoctorKey), $"appsurface:v2:{applicationName}:{environment}:{keyPrefix}:namespace");

        private static MacOsSecItemQuery V2Item(AppSurfaceLocalSecretIdentity identity) =>
            new(
                $"AppSurface.LocalSecrets.v2.{identity.ApplicationName}.{identity.Environment}",
                string.IsNullOrEmpty(identity.KeyPrefix) ? identity.StoredKey : $"{identity.KeyPrefix}:{identity.StoredKey}");

        private AppSurfaceLocalSecretResult MigrationRequired(AppSurfaceLocalSecretIdentity identity) =>
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.MigrationRequired,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-migration-required",
                    "A readable legacy macOS local secret needs migration.",
                    "The CLI/AppHost-safe v2 Keychain record is absent while the retained legacy record is still readable.",
                    $"Run `appsurface secrets migrate --app {identity.ApplicationName} --environment {identity.Environment}{(identity.KeyPrefix == null ? string.Empty : $" --prefix {identity.KeyPrefix}")}`.",
                    "local-secrets-macos-migration"),
                Name);

        private AppSurfaceLocalSecretResult MapStatus(int status, string operation)
        {
            if (status == ErrSecItemNotFound)
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            if (status is ErrSecInteractionNotAllowed or ErrSecAuthFailed or ErrSecUserCanceled)
            {
                return AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Locked,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-locked",
                        "macOS Keychain could not complete the request.",
                        $"Keychain returned OSStatus {status} during `{operation}`.",
                        "Unlock the login keychain or allow access for the current user session, then retry.",
                        "local-secrets-macos-migration",
                        retryable: true),
                    Name);
            }

            if (status == ErrSecMissingEntitlement)
            {
                return AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Unavailable,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-entitlement-unsupported",
                        "The selected macOS Keychain configuration is not available to this app.",
                        $"Keychain returned errSecMissingEntitlement (OSStatus {status}); arbitrary AppHosts must not be asked to join an AppSurface-owned access group.",
                        "Use the entitlement-free LocalSecrets configuration or a remote/team secret provider.",
                        "local-secrets-macos-migration"),
                    Name);
            }

            return AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.Unavailable,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-unavailable",
                    "macOS Keychain could not complete the request.",
                    $"Keychain returned OSStatus {status} during `{operation}`.",
                    "Run `appsurface secrets doctor` and retry after restoring the current user Keychain session.",
                    "local-secrets-macos-migration",
                    retryable: true),
                Name);
        }

        private static AppSurfaceLocalSecretDiagnostic InvalidIndex(string cause) =>
            new(
                "local-secret-index-invalid",
                "Local secret index is invalid.",
                cause,
                "Remove the invalid v2 index entry, then set or migrate the intended LocalSecrets keys again.",
                "local-secrets-macos-migration");

        private AppSurfaceLocalSecretResult VerificationFailed() =>
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-v2-verification-failed",
                    "macOS LocalSecrets could not verify the v2 write.",
                    "The Keychain write completed but a fresh read did not confirm the expected value.",
                    "Retry the operation; do not remove the retained legacy record automatically.",
                    "local-secrets-macos-migration",
                    retryable: true),
                Name);

        private AppSurfaceLocalSecretResult InvalidRead() =>
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-v2-read-invalid",
                    "macOS LocalSecrets received an invalid v2 read result.",
                    "The Keychain read reported success without returning secret data.",
                    "Retry the operation; do not assume a legacy secret was migrated.",
                    "local-secrets-macos-migration",
                    retryable: true),
                Name);

        private AppSurfaceLocalSecretDiagnostic InvalidLegacyRead() =>
            new(
                "local-secret-legacy-read-invalid",
                "macOS LocalSecrets received an invalid legacy read result.",
                "The retained legacy Keychain read reported success without returning a secret value.",
                "Retry the migration after restoring the current user Keychain session; do not assume this key was migrated.",
                "local-secrets-macos-migration",
                retryable: true);

        private bool TryAcquire(AppSurfaceLocalSecretIdentity identity, out Mutex? mutex, out AppSurfaceLocalSecretResult? failure)
        {
            mutex = null;
            failure = null;
            try
            {
                mutex = new Mutex(false, MutexName(identity));
                try
                {
                    if (mutex.WaitOne(MutexTimeout))
                    {
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    return true;
                }

                mutex.Dispose();
                mutex = null;
                failure = Busy();
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                mutex?.Dispose();
                mutex = null;
                failure = AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.ProviderFailed,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-mutex-failed",
                        "LocalSecrets could not coordinate the operation.",
                        $"The namespace mutex could not be acquired because {ex.GetType().Name} was raised.",
                        "Retry the operation; do not assume migration completed.",
                        "local-secrets-macos-migration",
                        retryable: true),
                    Name);
                return false;
            }
        }

        private AppSurfaceLocalSecretResult Busy() =>
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.ProviderFailed,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-operation-busy",
                    "Another LocalSecrets operation is still running.",
                    "The namespace mutex was not available within five seconds.",
                    "Retry after the other LocalSecrets operation completes.",
                    "local-secrets-macos-migration",
                    retryable: true),
                Name);

        private static string MutexName(AppSurfaceLocalSecretIdentity identity)
        {
            var input = string.Concat(identity.ApplicationName, "\0", identity.Environment, "\0", identity.KeyPrefix ?? string.Empty);
            var bytes = Encoding.UTF8.GetBytes(input);
            try
            {
                return $"AppSurface_LocalSecrets_{Convert.ToHexString(SHA256.HashData(bytes))}";
            }
            finally
            {
                Array.Clear(bytes);
            }
        }

        private static void Release(Mutex mutex)
        {
            using (mutex)
            {
                mutex.ReleaseMutex();
            }
        }

    }

    /// <summary>Describes an immutable file-based macOS <c>SecItem</c> generic-password identity.</summary>
    internal sealed record MacOsSecItemQuery(string Service, string Account);

    /// <summary>Describes the status and optional raw value returned by a macOS <c>SecItem</c> read.</summary>
    internal sealed record MacOsSecItemReadResult(int Status, byte[]? Data);

    /// <summary>Isolates native macOS Keychain request construction for deterministic compatibility tests.</summary>
    internal interface IMacOsSecItemInterop
    {
        /// <summary>
        /// Reads the raw value for a generic-password <paramref name="query"/>.
        /// </summary>
        /// <param name="query">The immutable SecItem identity to read.</param>
        /// <returns>
        /// The raw OSStatus and optional copied value. <see cref="MacOsV2CompatibilityLocalSecretStore"/> maps OSStatus
        /// through its local-secret status mapping; a success status with null data is treated as a fail-closed provider error.
        /// </returns>
        MacOsSecItemReadResult Read(MacOsSecItemQuery query);

        /// <summary>
        /// Checks whether a generic-password <paramref name="query"/> exists without requesting its value data.
        /// </summary>
        /// <param name="query">The immutable SecItem identity to check.</param>
        /// <returns>The raw OSStatus, which the v2 store maps through its local-secret status mapping.</returns>
        int Exists(MacOsSecItemQuery query);

        /// <summary>
        /// Adds a generic-password <paramref name="value"/> under <paramref name="query"/> without overwriting a record.
        /// </summary>
        /// <param name="query">The immutable SecItem identity to add.</param>
        /// <param name="value">The value bytes to store.</param>
        /// <returns>
        /// The raw OSStatus, which the v2 store maps through its local-secret status mapping. Existing records must return
        /// <c>errSecDuplicateItem</c> so <see cref="MacOsV2CompatibilityLocalSecretStore"/> can route the write to <see cref="Update"/>.
        /// </returns>
        int Add(MacOsSecItemQuery query, byte[] value);

        /// <summary>
        /// Replaces the value for an existing generic-password <paramref name="query"/>.
        /// </summary>
        /// <param name="query">The immutable SecItem identity to update.</param>
        /// <param name="value">The replacement value bytes.</param>
        /// <returns>The raw OSStatus, which the v2 store maps through its local-secret status mapping.</returns>
        int Update(MacOsSecItemQuery query, byte[] value);

        /// <summary>
        /// Deletes the generic-password record identified by <paramref name="query"/>.
        /// </summary>
        /// <param name="query">The immutable SecItem identity to delete.</param>
        /// <returns>The raw OSStatus, which the v2 store maps through its local-secret status mapping.</returns>
        int Delete(MacOsSecItemQuery query);
    }

    [SupportedOSPlatform("macos")]
    private sealed partial class NativeMacOsSecItemInterop : IMacOsSecItemInterop
    {
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint Utf8Encoding = 0x08000100;

        private static readonly IntPtr SecurityLibrary = NativeLibrary.Load(SecurityFramework);
        private static readonly IntPtr CoreFoundationLibrary = NativeLibrary.Load(CoreFoundationFramework);
        private static readonly IntPtr SecClass = SecurityConstant("kSecClass");
        private static readonly IntPtr SecClassGenericPassword = SecurityConstant("kSecClassGenericPassword");
        private static readonly IntPtr SecAttrService = SecurityConstant("kSecAttrService");
        private static readonly IntPtr SecAttrAccount = SecurityConstant("kSecAttrAccount");
        private static readonly IntPtr SecValueData = SecurityConstant("kSecValueData");
        private static readonly IntPtr SecReturnData = SecurityConstant("kSecReturnData");
        private static readonly IntPtr True = CoreFoundationConstant("kCFBooleanTrue");
        private static readonly IntPtr DictionaryKeyCallbacks = NativeLibrary.GetExport(CoreFoundationLibrary, "kCFTypeDictionaryKeyCallBacks");
        private static readonly IntPtr DictionaryValueCallbacks = NativeLibrary.GetExport(CoreFoundationLibrary, "kCFTypeDictionaryValueCallBacks");

        public static NativeMacOsSecItemInterop Instance { get; } = new();

        private NativeMacOsSecItemInterop()
        {
        }

        public MacOsSecItemReadResult Read(MacOsSecItemQuery query)
        {
            var attributes = CreateAttributes(query);
            IntPtr result = IntPtr.Zero;
            try
            {
                CFDictionarySetValue(attributes, SecReturnData, True);
                var status = SecItemCopyMatching(attributes, out result);
                if (status != 0 || result == IntPtr.Zero)
                {
                    return new MacOsSecItemReadResult(status, null);
                }

                var length = CFDataGetLength(result);
                var data = new byte[checked((int)length)];
                if (length > 0)
                {
                    Marshal.Copy(CFDataGetBytePtr(result), data, 0, data.Length);
                }

                return new MacOsSecItemReadResult(status, data);
            }
            finally
            {
                Release(result);
                Release(attributes);
            }
        }

        public int Exists(MacOsSecItemQuery query)
        {
            var attributes = CreateAttributes(query);
            IntPtr result = IntPtr.Zero;
            try
            {
                return SecItemCopyMatching(attributes, out result);
            }
            finally
            {
                Release(result);
                Release(attributes);
            }
        }

        public int Add(MacOsSecItemQuery query, byte[] value)
        {
            var attributes = CreateAttributes(query);
            var data = CreateData(value);
            try
            {
                CFDictionarySetValue(attributes, SecValueData, data);
                return SecItemAdd(attributes, IntPtr.Zero);
            }
            finally
            {
                Release(data);
                Release(attributes);
            }
        }

        public int Update(MacOsSecItemQuery query, byte[] value)
        {
            var attributes = CreateAttributes(query);
            var update = CreateDictionary();
            var data = CreateData(value);
            try
            {
                CFDictionarySetValue(update, SecValueData, data);
                return SecItemUpdate(attributes, update);
            }
            finally
            {
                Release(data);
                Release(update);
                Release(attributes);
            }
        }

        public int Delete(MacOsSecItemQuery query)
        {
            var attributes = CreateAttributes(query);
            try
            {
                return SecItemDelete(attributes);
            }
            finally
            {
                Release(attributes);
            }
        }

        private static IntPtr CreateAttributes(MacOsSecItemQuery query)
        {
            var attributes = CreateDictionary();
            var service = CFStringCreateWithCString(IntPtr.Zero, query.Service, Utf8Encoding);
            var account = CFStringCreateWithCString(IntPtr.Zero, query.Account, Utf8Encoding);
            try
            {
                CFDictionarySetValue(attributes, SecClass, SecClassGenericPassword);
                CFDictionarySetValue(attributes, SecAttrService, service);
                CFDictionarySetValue(attributes, SecAttrAccount, account);
                return attributes;
            }
            catch
            {
                Release(attributes);
                throw;
            }
            finally
            {
                Release(account);
                Release(service);
            }
        }

        private static IntPtr CreateDictionary() =>
            CFDictionaryCreateMutable(IntPtr.Zero, 0, DictionaryKeyCallbacks, DictionaryValueCallbacks);

        private static unsafe IntPtr CreateData(byte[] value)
        {
            fixed (byte* bytes = value)
            {
                return CFDataCreate(IntPtr.Zero, bytes, value.Length);
            }
        }

        private static IntPtr SecurityConstant(string name) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityLibrary, name));

        private static IntPtr CoreFoundationConstant(string name) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundationLibrary, name));

        private static void Release(IntPtr value)
        {
            if (value != IntPtr.Zero)
            {
                CFRelease(value);
            }
        }

        [LibraryImport(SecurityFramework)]
        private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [LibraryImport(SecurityFramework)]
        private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

        [LibraryImport(SecurityFramework)]
        private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        [LibraryImport(SecurityFramework)]
        private static partial int SecItemDelete(IntPtr query);

        [LibraryImport(CoreFoundationFramework)]
        private static partial IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);

        [LibraryImport(CoreFoundationFramework)]
        private static partial void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

        [LibraryImport(CoreFoundationFramework, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

        [LibraryImport(CoreFoundationFramework)]
        private static unsafe partial IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

        [LibraryImport(CoreFoundationFramework)]
        private static partial nint CFDataGetLength(IntPtr data);

        [LibraryImport(CoreFoundationFramework)]
        private static partial IntPtr CFDataGetBytePtr(IntPtr data);

        [LibraryImport(CoreFoundationFramework)]
        private static partial void CFRelease(IntPtr cf);
    }
}
