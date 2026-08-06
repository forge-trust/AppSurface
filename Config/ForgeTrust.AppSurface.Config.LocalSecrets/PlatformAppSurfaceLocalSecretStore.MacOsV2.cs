using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        public string Name => nameof(MacOsV2CompatibilityLocalSecretStore);

        /// <inheritdoc />
        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

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
        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(value);

            if (!TryAcquire(identity, out var mutex, out var failure))
            {
                return failure!;
            }

            try
            {
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
        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            if (!TryAcquire(identity, out var mutex, out var failure))
            {
                return failure!;
            }

            try
            {
                var v2 = DeleteV2(identity);
                if (v2.Status is not (LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing))
                {
                    return v2;
                }

                var legacy = _legacy.Delete(identity);
                if (legacy.Status is not (LocalSecretResultStatus.Found or LocalSecretResultStatus.Missing))
                {
                    return legacy;
                }

                var index = RemoveFromV2Index(identity);
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

        /// <inheritdoc />
        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix)
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
                    var v2 = ReadV2(identity);
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

            if (v2Index.Keys.Contains(identity.Key, StringComparer.Ordinal))
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
                LocalSecretResultStatus.Missing => AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Missing,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-ready",
                        "macOS v2 LocalSecrets store is ready.",
                        "The entitlement-free file-based SecItem Keychain path is available for the current user session.",
                        "Set a secret with `appsurface secrets set` for this pinned namespace.",
                        "local-secrets-macos-migration"),
                    Name),
                LocalSecretResultStatus.Found => AppSurfaceLocalSecretResult.Found(string.Empty, Name),
                _ => probe
            };
        }

        /// <inheritdoc />
        public AppSurfaceLocalSecretMigrationResult Migrate(string applicationName, string environment, string? keyPrefix)
        {
            var namespaceIdentity = NamespaceIdentity(applicationName, environment, keyPrefix);
            if (!TryAcquire(namespaceIdentity, out var mutex, out var failure))
            {
                return AppSurfaceLocalSecretMigrationResult.FailedToStart(failure!.Status, failure.Diagnostic!, Name);
            }

            try
            {
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
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, "Failed", LocalSecretResultStatus.ProviderFailed, InvalidIndex("The legacy index contains an invalid local secret key.")));
                        continue;
                    }

                    var identity = normalized.Identity!;
                    var v2 = ReadV2(identity);
                    if (v2.Status == LocalSecretResultStatus.Found)
                    {
                        var index = EnsureV2IndexContains(identity);
                        rows.Add(index.Status == LocalSecretResultStatus.Found
                            ? new AppSurfaceLocalSecretMigrationRow(key, "AlreadyV2", LocalSecretResultStatus.Found, null)
                            : new AppSurfaceLocalSecretMigrationRow(key, "Failed", index.Status, index.Diagnostic));
                        continue;
                    }

                    if (v2.Status != LocalSecretResultStatus.Missing)
                    {
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, "Failed", v2.Status, v2.Diagnostic));
                        continue;
                    }

                    var v1 = _legacy.Get(identity);
                    if (v1.Status != LocalSecretResultStatus.Found)
                    {
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, "Failed", v1.Status, v1.Diagnostic));
                        continue;
                    }

                    var write = WriteV2(identity, v1.Value!);
                    if (write.Status != LocalSecretResultStatus.Found)
                    {
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, "Failed", write.Status, write.Diagnostic));
                        continue;
                    }

                    var verify = ReadV2(identity);
                    if (verify.Status != LocalSecretResultStatus.Found || !string.Equals(verify.Value, v1.Value, StringComparison.Ordinal))
                    {
                        var failed = verify.Status == LocalSecretResultStatus.Found ? VerificationFailed() : verify;
                        rows.Add(new AppSurfaceLocalSecretMigrationRow(key, "Failed", failed.Status, failed.Diagnostic));
                        continue;
                    }

                    var indexWrite = EnsureV2IndexContains(identity);
                    rows.Add(indexWrite.Status == LocalSecretResultStatus.Found
                        ? new AppSurfaceLocalSecretMigrationRow(key, "Migrated", LocalSecretResultStatus.Found, null)
                        : new AppSurfaceLocalSecretMigrationRow(key, "Failed", indexWrite.Status, indexWrite.Diagnostic));
                }

                return AppSurfaceLocalSecretMigrationResult.Completed(rows, Name);
            }
            finally
            {
                Release(mutex!);
            }
        }

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
            return keys.Add(identity.Key) || index.NeedsRepair
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
            return keys.Remove(identity.Key) || index.NeedsRepair
                ? WriteV2Index(identity.ApplicationName, identity.Environment, identity.KeyPrefix, keys)
                : AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        private V2IndexReadResult ReadV2Index(string applicationName, string environment, string? keyPrefix)
        {
            var index = ReadV2(IndexIdentity(applicationName, environment, keyPrefix));
            if (index.Status == LocalSecretResultStatus.Missing)
            {
                return V2IndexReadResult.Found([], false);
            }

            if (index.Status != LocalSecretResultStatus.Found || index.Value == null)
            {
                return V2IndexReadResult.Failed(index.Status, index.Diagnostic!);
            }

            string?[] serialized;
            try
            {
                serialized = JsonSerializer.Deserialize<string?[]>(index.Value) ?? [];
            }
            catch (JsonException)
            {
                return V2IndexReadResult.Failed(LocalSecretResultStatus.ProviderFailed, InvalidIndex("The v2 index entry could not be parsed."));
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var needsRepair = false;
            foreach (var key in serialized)
            {
                if (string.IsNullOrWhiteSpace(key) || string.Equals(key, IndexKey, StringComparison.Ordinal))
                {
                    needsRepair = true;
                    continue;
                }

                if (!keys.Add(key))
                {
                    needsRepair = true;
                }
            }

            return V2IndexReadResult.Found(keys, needsRepair);
        }

        private AppSurfaceLocalSecretResult WriteV2Index(string applicationName, string environment, string? keyPrefix, IEnumerable<string> keys) =>
            WriteV2(
                IndexIdentity(applicationName, environment, keyPrefix),
                JsonSerializer.Serialize(keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ThenBy(static key => key, StringComparer.Ordinal).ToArray()));

        private static AppSurfaceLocalSecretIdentity IndexIdentity(string applicationName, string environment, string? keyPrefix) =>
            new(applicationName, environment, keyPrefix, IndexKey, $"appsurface:v2:{applicationName}:{environment}:{keyPrefix}:{IndexKey}");

        private static AppSurfaceLocalSecretIdentity NamespaceIdentity(string applicationName, string environment, string? keyPrefix) =>
            new(applicationName, environment, keyPrefix, IndexKey, $"appsurface:v2:{applicationName}:{environment}:{keyPrefix}:namespace");

        private static MacOsSecItemQuery V2Item(AppSurfaceLocalSecretIdentity identity) =>
            new(
                $"AppSurface.LocalSecrets.v2.{identity.ApplicationName}.{identity.Environment}",
                string.IsNullOrEmpty(identity.KeyPrefix) ? identity.Key : $"{identity.KeyPrefix}:{identity.Key}");

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

        private sealed record V2IndexReadResult(
            LocalSecretResultStatus Status,
            IReadOnlyCollection<string> Keys,
            bool NeedsRepair,
            AppSurfaceLocalSecretDiagnostic? Diagnostic)
        {
            public static V2IndexReadResult Found(IReadOnlyCollection<string> keys, bool needsRepair) =>
                new(LocalSecretResultStatus.Found, keys, needsRepair, null);

            public static V2IndexReadResult Failed(LocalSecretResultStatus status, AppSurfaceLocalSecretDiagnostic diagnostic) =>
                new(status, [], false, diagnostic);
        }
    }

    /// <summary>Describes an immutable file-based macOS <c>SecItem</c> generic-password identity.</summary>
    internal sealed record MacOsSecItemQuery(string Service, string Account);

    /// <summary>Describes the status and optional raw value returned by a macOS <c>SecItem</c> read.</summary>
    internal sealed record MacOsSecItemReadResult(int Status, byte[]? Data);

    /// <summary>Isolates native macOS Keychain request construction for deterministic compatibility tests.</summary>
    internal interface IMacOsSecItemInterop
    {
        MacOsSecItemReadResult Read(MacOsSecItemQuery query);

        int Add(MacOsSecItemQuery query, byte[] value);

        int Update(MacOsSecItemQuery query, byte[] value);

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
