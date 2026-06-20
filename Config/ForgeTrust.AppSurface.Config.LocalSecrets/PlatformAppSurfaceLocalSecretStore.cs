using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// LocalSecrets store that delegates to the current operating system's user secret facility when available.
/// </summary>
/// <remarks>
/// macOS uses Security.framework Keychain generic passwords, Windows uses current-user Credential Manager generic credentials,
/// Linux uses Secret Service through <c>secret-tool</c>, and unsupported sessions return display-safe diagnostics.
/// </remarks>
[ExcludeFromCodeCoverage(
    Justification = "Real OS credential stores depend on desktop session state, prompts, and native services; deterministic tests cover fake stores and status mapping.")]
public sealed partial class PlatformAppSurfaceLocalSecretStore : IAppSurfaceLocalSecretStore
{
    private readonly IAppSurfaceLocalSecretStore _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformAppSurfaceLocalSecretStore"/> class.
    /// </summary>
    public PlatformAppSurfaceLocalSecretStore()
    {
        _inner = CreateInnerStore();
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => _inner.Get(identity);

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => _inner.Set(identity, value);

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => _inner.Delete(identity);

    /// <inheritdoc />
    public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
        _inner.List(applicationName, environment, keyPrefix);

    /// <inheritdoc />
    public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
        _inner.Doctor(applicationName, environment, keyPrefix);

    private static IAppSurfaceLocalSecretStore CreateInnerStore()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsKeychainLocalSecretStore();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var secretTool = FindOnPath("secret-tool");
            return secretTool == null
                ? new UnsupportedPlatformLocalSecretStore("Linux Secret Service command `secret-tool` is unavailable in this session.")
                : new LinuxSecretServiceLocalSecretStore(secretTool);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsCredentialManagerLocalSecretStore();
        }

        return new UnsupportedPlatformLocalSecretStore("This operating system does not have an AppSurface LocalSecrets adapter.");
    }

    private static string? FindOnPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
        {
            return null;
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        return paths
            .Select(path => Path.Join(path, fileName))
            .FirstOrDefault(File.Exists);
    }

    private sealed class UnsupportedPlatformLocalSecretStore(string cause) : IAppSurfaceLocalSecretStore
    {
        public string Name => nameof(UnsupportedPlatformLocalSecretStore);

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => Unsupported();

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value) => Unsupported();

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity) => Unsupported();

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretListResult.Failed(LocalSecretResultStatus.UnsupportedPlatform, Diagnostic(), Name);

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) => Unsupported();

        private AppSurfaceLocalSecretResult Unsupported() =>
            AppSurfaceLocalSecretResult.NotFound(LocalSecretResultStatus.UnsupportedPlatform, Diagnostic(), Name);

        private AppSurfaceLocalSecretDiagnostic Diagnostic() =>
            new(
                "local-secret-store-unsupported",
                "Local secret store is unsupported in this session.",
                cause,
                "Use environment variables or key-per-file in this session, or run on a supported desktop user session.",
                "local-secrets-platform-compatibility");
    }

    internal sealed partial class MacOsKeychainLocalSecretStore : IndexedLocalSecretStore
    {
        private const int ErrSecSuccess = 0;
        private const int ErrSecDuplicateItem = -25299;
        private const int ErrSecItemNotFound = -25300;
        private const int ErrSecInteractionNotAllowed = -25308;
        private const int ErrSecAuthFailed = -25293;
        private const int ErrSecUserCanceled = -128;

        public override string Name => "macOS Keychain";

        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            var names = BuildKeychainName(identity);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)names.Service.Length,
                names.Service,
                (uint)names.Account.Length,
                names.Account,
                out var passwordLength,
                out var passwordData,
                out var itemRef);
            try
            {
                if (status == ErrSecSuccess)
                {
                    var bytes = new byte[passwordLength];
                    Marshal.Copy(passwordData, bytes, 0, bytes.Length);
                    return AppSurfaceLocalSecretResult.Found(Encoding.UTF8.GetString(bytes), Name);
                }

                return MapMacOsStatus(status, "read");
            }
            finally
            {
                if (passwordData != IntPtr.Zero)
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
                }

                ReleaseIfNeeded(itemRef);
            }
        }

        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            var names = BuildKeychainName(identity);
            var password = Encoding.UTF8.GetBytes(value);
            var status = SecKeychainAddGenericPassword(
                IntPtr.Zero,
                (uint)names.Service.Length,
                names.Service,
                (uint)names.Account.Length,
                names.Account,
                (uint)password.Length,
                password,
                out var addedItemRef);
            ReleaseIfNeeded(addedItemRef);

            if (status == ErrSecDuplicateItem)
            {
                status = UpdateExisting(identity, password);
            }

            if (status != ErrSecSuccess)
            {
                return MapMacOsStatus(status, "write");
            }

            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            var status = FindItem(identity, out var itemRef);
            try
            {
                if (status == ErrSecItemNotFound)
                {
                    return AppSurfaceLocalSecretResult.Missing(Name);
                }

                if (status != ErrSecSuccess)
                {
                    return MapMacOsStatus(status, "delete");
                }

                status = SecKeychainItemDelete(itemRef);
                if (status != ErrSecSuccess)
                {
                    return MapMacOsStatus(status, "delete");
                }
            }
            finally
            {
                ReleaseIfNeeded(itemRef);
            }

            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix) =>
            AppSurfaceLocalSecretResult.NotFound(
                LocalSecretResultStatus.Missing,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-ready",
                    "macOS Keychain is available.",
                    "Security.framework can access the current user's Keychain session.",
                    "Set a local secret with `appsurface secrets set`.",
                    "local-secrets-platform-compatibility"),
                Name);

        private static string Service(AppSurfaceLocalSecretIdentity identity) =>
            $"AppSurface.LocalSecrets.{identity.ApplicationName}.{identity.Environment}";

        internal static string Account(AppSurfaceLocalSecretIdentity identity) =>
            string.IsNullOrWhiteSpace(identity.KeyPrefix) ? identity.Key : $"{identity.KeyPrefix}:{identity.Key}";

        internal static KeychainNameBytes BuildKeychainName(AppSurfaceLocalSecretIdentity identity) =>
            new(Encode(Service(identity)), Encode(Account(identity)));

        private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        internal AppSurfaceLocalSecretResult MapMacOsStatus(int status, string operation)
        {
            if (status == ErrSecItemNotFound)
            {
                return AppSurfaceLocalSecretResult.Missing(Name);
            }

            var resultStatus = status is ErrSecInteractionNotAllowed or ErrSecAuthFailed or ErrSecUserCanceled
                ? LocalSecretResultStatus.Locked
                : LocalSecretResultStatus.Unavailable;
            return AppSurfaceLocalSecretResult.NotFound(
                resultStatus,
                new AppSurfaceLocalSecretDiagnostic(
                    resultStatus == LocalSecretResultStatus.Locked ? "local-secret-store-locked" : "local-secret-store-unavailable",
                    "macOS Keychain could not complete the request.",
                    $"Keychain returned OSStatus {status} during `{operation}`.",
                    "Unlock the login keychain, allow access for the current user session, or use environment variables/key-per-file for this environment.",
                    "local-secrets-platform-compatibility",
                    retryable: true),
                Name);
        }

        private static int UpdateExisting(AppSurfaceLocalSecretIdentity identity, byte[] password)
        {
            var status = FindItem(identity, out var itemRef);
            try
            {
                return status == ErrSecSuccess
                    ? SecKeychainItemModifyAttributesAndData(itemRef, IntPtr.Zero, (uint)password.Length, password)
                    : status;
            }
            finally
            {
                ReleaseIfNeeded(itemRef);
            }
        }

        private static int FindItem(AppSurfaceLocalSecretIdentity identity, out IntPtr itemRef)
        {
            var names = BuildKeychainName(identity);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)names.Service.Length,
                names.Service,
                (uint)names.Account.Length,
                names.Account,
                out _,
                out var passwordData,
                out itemRef);
            if (passwordData != IntPtr.Zero)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            }

            return status;
        }

        internal readonly record struct KeychainNameBytes(byte[] Service, byte[] Account);

        private static void ReleaseIfNeeded(IntPtr itemRef)
        {
            if (itemRef != IntPtr.Zero)
            {
                CFRelease(itemRef);
            }
        }

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
            byte[] serviceName,
            uint accountNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)]
            byte[] accountName,
            uint passwordLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)]
            byte[] passwordData,
            out IntPtr itemRef);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
            byte[] serviceName,
            uint accountNameLength,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)]
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef,
            IntPtr attrList,
            uint length,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)]
            byte[] data);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainItemDelete(IntPtr itemRef);

        [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
        private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static partial void CFRelease(IntPtr cf);
    }

    internal sealed class LinuxSecretServiceLocalSecretStore : CommandBackedLocalSecretStore
    {
        private readonly string _secretToolPath;

        public LinuxSecretServiceLocalSecretStore(string secretToolPath)
            : this(secretToolPath, DefaultPlatformSecretCommandRunner.Instance)
        {
        }

        internal LinuxSecretServiceLocalSecretStore(string secretToolPath, IPlatformSecretCommandRunner commandRunner)
            : base(commandRunner)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(secretToolPath);

            _secretToolPath = secretToolPath;
        }

        public override string Name => "Linux Secret Service";

        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            var result = Run(
                _secretToolPath,
                BuildArguments("lookup", identity),
                null);
            if (result.ExitCode == 0)
            {
                return AppSurfaceLocalSecretResult.Found(result.Output, Name);
            }

            return IsLinuxMissing(result)
                ? AppSurfaceLocalSecretResult.Missing(Name)
                : MapCommandFailure(result, "read");
        }

        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            var label = $"AppSurface {identity.ApplicationName} {identity.Environment} {identity.Key}";
            var result = Run(
                _secretToolPath,
                ["store", "--label", label, .. BuildArguments(identity)],
                value);
            if (result.ExitCode != 0)
            {
                return MapCommandFailure(result, "write");
            }

            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            var result = Run(
                _secretToolPath,
                BuildArguments("clear", identity),
                null);
            if (result.ExitCode != 0)
            {
                return IsLinuxMissing(result)
                    ? AppSurfaceLocalSecretResult.Missing(Name)
                    : MapCommandFailure(result, "delete");
            }

            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix)
        {
            var result = Run(_secretToolPath, ["search", "appsurface", "local-secrets"], null);
            return result.ExitCode == 0
                ? AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Missing,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-ready",
                        "Linux Secret Service is reachable.",
                        "`secret-tool` can talk to the current user secret service session.",
                        "Set a local secret with `appsurface secrets set`.",
                        "local-secrets-platform-compatibility"),
                    Name)
                : MapCommandFailure(result, "doctor");
        }

        private static bool IsLinuxMissing(PlatformSecretCommandResult result) =>
            result.ExitCode != 0
            && string.IsNullOrWhiteSpace(result.Output)
            && string.IsNullOrWhiteSpace(result.Error);

        internal static string[] BuildArguments(string command, AppSurfaceLocalSecretIdentity identity) =>
            [command, .. BuildArguments(identity)];

        private static string[] BuildArguments(AppSurfaceLocalSecretIdentity identity) =>
            [
                "appsurface",
                "local-secrets",
                "application",
                identity.ApplicationName,
                "environment",
                identity.Environment,
                "prefix",
                identity.KeyPrefix ?? string.Empty,
                "key",
                identity.Key
            ];
    }

    [SupportedOSPlatform("windows")]
    private sealed partial class WindowsCredentialManagerLocalSecretStore : IndexedLocalSecretStore
    {
        private const int ErrorNotFound = 1168;
        private const int CredentialTypeGeneric = 1;
        private const int CredentialPersistLocalMachine = 2;

        public override string Name => "Windows Credential Manager";

        protected override AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (!CredReadW(TargetName(identity), CredentialTypeGeneric, 0, out var credentialPointer))
            {
                return Marshal.GetLastPInvokeError() == ErrorNotFound
                    ? AppSurfaceLocalSecretResult.Missing(Name)
                    : WindowsFailure("read", LocalSecretResultStatus.Unavailable);
            }

            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                {
                    return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
                }

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return AppSurfaceLocalSecretResult.Found(Encoding.Unicode.GetString(bytes), Name);
            }
            finally
            {
                CredFree(credentialPointer);
            }
        }

        protected override AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value)
        {
            var targetName = TargetName(identity);
            var blob = Encoding.Unicode.GetBytes(value);
            var blobHandle = GCHandle.Alloc(blob, GCHandleType.Pinned);
            try
            {
                var credential = new NativeCredential
                {
                    Type = CredentialTypeGeneric,
                    TargetName = targetName,
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = blobHandle.AddrOfPinnedObject(),
                    Persist = CredentialPersistLocalMachine,
                    UserName = Environment.UserName
                };

                var credentialPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
                try
                {
                    Marshal.StructureToPtr(credential, credentialPointer, fDeleteOld: false);
                    if (!CredWriteW(credentialPointer, 0))
                    {
                        return WindowsFailure("write", LocalSecretResultStatus.Unavailable);
                    }
                }
                finally
                {
                    Marshal.DestroyStructure<NativeCredential>(credentialPointer);
                    Marshal.FreeHGlobal(credentialPointer);
                }
            }
            finally
            {
                blobHandle.Free();
            }

            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity)
        {
            if (!CredDeleteW(TargetName(identity), CredentialTypeGeneric, 0))
            {
                return Marshal.GetLastPInvokeError() == ErrorNotFound
                    ? AppSurfaceLocalSecretResult.Missing(Name)
                    : WindowsFailure("delete", LocalSecretResultStatus.Unavailable);
            }

            return AppSurfaceLocalSecretResult.Found(string.Empty, Name);
        }

        protected override AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix)
        {
            var probeIdentity = new AppSurfaceLocalSecretIdentity(
                applicationName,
                environment,
                keyPrefix,
                "__appsurface_doctor__",
                $"appsurface:{applicationName}:{environment}:{keyPrefix}:__appsurface_doctor__");
            var write = WriteStoredValue(probeIdentity, "ready");
            if (write.Status != LocalSecretResultStatus.Found)
            {
                return write;
            }

            var read = ReadStoredValue(probeIdentity);
            _ = DeleteStoredValue(probeIdentity);
            return read.Status == LocalSecretResultStatus.Found
                ? AppSurfaceLocalSecretResult.NotFound(
                    LocalSecretResultStatus.Missing,
                    new AppSurfaceLocalSecretDiagnostic(
                        "local-secret-store-ready",
                        "Windows Credential Manager is reachable.",
                        "The current user can write, read, and delete a generic credential.",
                        "Set a local secret with `appsurface secrets set`.",
                        "local-secrets-platform-compatibility"),
                    Name)
                : read;
        }

        private AppSurfaceLocalSecretResult WindowsFailure(string operation, LocalSecretResultStatus status) =>
            AppSurfaceLocalSecretResult.NotFound(
                status,
                new AppSurfaceLocalSecretDiagnostic(
                    "local-secret-store-unavailable",
                    "Windows Credential Manager could not complete the request.",
                    $"The current-user credential store failed during `{operation}`.",
                    "Run in an interactive Windows user session, unlock the user profile, or use environment variables/key-per-file for this environment.",
                    "local-secrets-platform-compatibility",
                    retryable: true),
                Name);

        private static string TargetName(AppSurfaceLocalSecretIdentity identity) => $"AppSurface.LocalSecrets.{identity.StorageName}";

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredReadW(string target, int type, int reservedFlag, out IntPtr credential);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredWriteW(IntPtr userCredential, int flags);

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredDeleteW(string target, int type, int flags);

        [LibraryImport("advapi32.dll")]
        private static partial void CredFree(IntPtr buffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string? Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }
    }

    internal abstract class IndexedLocalSecretStore : IAppSurfaceLocalSecretStore
    {
        protected const string IndexKey = "__appsurface_index__";

        public abstract string Name { get; }

        public AppSurfaceLocalSecretResult Get(AppSurfaceLocalSecretIdentity identity) => ReadStoredValue(identity);

        public AppSurfaceLocalSecretResult Set(AppSurfaceLocalSecretIdentity identity, string value)
        {
            if (IsIndexIdentity(identity))
            {
                return WriteStoredValue(identity, value);
            }

            var write = WriteStoredValue(identity, value);
            return write.Status == LocalSecretResultStatus.Found
                ? AddToIndex(identity)
                : write;
        }

        public AppSurfaceLocalSecretResult Delete(AppSurfaceLocalSecretIdentity identity)
        {
            if (IsIndexIdentity(identity))
            {
                return DeleteStoredValue(identity);
            }

            var delete = DeleteStoredValue(identity);
            if (delete.Status != LocalSecretResultStatus.Found && delete.Status != LocalSecretResultStatus.Missing)
            {
                return delete;
            }

            var index = ReadIndex(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            if (index.Status != LocalSecretResultStatus.Found)
            {
                return AppSurfaceLocalSecretResult.NotFound(index.Status, index.Diagnostic!, Name);
            }

            var keys = index.Keys.ToHashSet(StringComparer.Ordinal);
            var wasIndexed = keys.Remove(identity.Key);
            if (!wasIndexed)
            {
                return delete.Status == LocalSecretResultStatus.Found
                    ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                    : delete;
            }

            var write = WriteIndex(identity.ApplicationName, identity.Environment, identity.KeyPrefix, keys);
            return write.Status == LocalSecretResultStatus.Found
                ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                : write;
        }

        public AppSurfaceLocalSecretListResult List(string applicationName, string environment, string? keyPrefix)
        {
            var index = ReadIndex(applicationName, environment, keyPrefix);
            if (index.Status != LocalSecretResultStatus.Found)
            {
                return AppSurfaceLocalSecretListResult.Failed(index.Status, index.Diagnostic!, Name);
            }

            var liveKeys = new HashSet<string>(StringComparer.Ordinal);
            var needsRepair = index.NeedsRepair;

            foreach (var key in index.Keys)
            {
                var keyIdentity = KeyIdentity(applicationName, environment, keyPrefix, key);
                if (!keyIdentity.Succeeded)
                {
                    return AppSurfaceLocalSecretListResult.Failed(
                        LocalSecretResultStatus.ProviderFailed,
                        InvalidIndexDiagnostic("The platform store index entry contains an invalid local secret key."),
                        Name);
                }

                var value = ReadStoredValue(keyIdentity.Identity!);
                if (value.Status == LocalSecretResultStatus.Found)
                {
                    liveKeys.Add(key);
                    continue;
                }

                if (value.Status == LocalSecretResultStatus.Missing)
                {
                    needsRepair = true;
                    continue;
                }

                return AppSurfaceLocalSecretListResult.Failed(value.Status, value.Diagnostic!, Name);
            }

            if (needsRepair)
            {
                var write = WriteIndex(applicationName, environment, keyPrefix, liveKeys);
                if (write.Status != LocalSecretResultStatus.Found)
                {
                    return AppSurfaceLocalSecretListResult.Failed(write.Status, write.Diagnostic!, Name);
                }
            }

            return AppSurfaceLocalSecretListResult.Found(liveKeys, Name);
        }

        public AppSurfaceLocalSecretResult Doctor(string applicationName, string environment, string? keyPrefix) =>
            DoctorStore(applicationName, environment, keyPrefix);

        protected abstract AppSurfaceLocalSecretResult ReadStoredValue(AppSurfaceLocalSecretIdentity identity);

        protected abstract AppSurfaceLocalSecretResult WriteStoredValue(AppSurfaceLocalSecretIdentity identity, string value);

        protected abstract AppSurfaceLocalSecretResult DeleteStoredValue(AppSurfaceLocalSecretIdentity identity);

        protected abstract AppSurfaceLocalSecretResult DoctorStore(string applicationName, string environment, string? keyPrefix);

        private static bool IsIndexIdentity(AppSurfaceLocalSecretIdentity identity) =>
            string.Equals(identity.Key, IndexKey, StringComparison.Ordinal);

        private AppSurfaceLocalSecretResult AddToIndex(AppSurfaceLocalSecretIdentity identity)
        {
            var index = ReadIndex(identity.ApplicationName, identity.Environment, identity.KeyPrefix);
            if (index.Status != LocalSecretResultStatus.Found)
            {
                return AppSurfaceLocalSecretResult.NotFound(index.Status, index.Diagnostic!, Name);
            }

            var keys = index.Keys.ToHashSet(StringComparer.Ordinal);
            keys.Add(identity.Key);
            return WriteIndex(identity.ApplicationName, identity.Environment, identity.KeyPrefix, keys);
        }

        private AppSurfaceLocalSecretResult WriteIndex(
            string applicationName,
            string environment,
            string? keyPrefix,
            IEnumerable<string> keys)
        {
            var index = JsonSerializer.Serialize(keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            var write = WriteStoredValue(IndexIdentity(applicationName, environment, keyPrefix), index);
            return write.Status == LocalSecretResultStatus.Found
                ? AppSurfaceLocalSecretResult.Found(string.Empty, Name)
                : write;
        }

        private IndexReadResult ReadIndex(string applicationName, string environment, string? keyPrefix)
        {
            var result = ReadStoredValue(IndexIdentity(applicationName, environment, keyPrefix));
            if (result.Status == LocalSecretResultStatus.Missing)
            {
                return IndexReadResult.Found([], needsRepair: false);
            }

            if (result.Status != LocalSecretResultStatus.Found || result.Value == null)
            {
                return IndexReadResult.Failed(result.Status, result.Diagnostic!);
            }

            string?[] indexedKeys;
            try
            {
                indexedKeys = JsonSerializer.Deserialize<string?[]>(result.Value) ?? [];
            }
            catch (JsonException)
            {
                return IndexReadResult.Failed(LocalSecretResultStatus.ProviderFailed, InvalidIndexDiagnostic("The platform store index entry could not be parsed."));
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var needsRepair = false;
            foreach (var key in indexedKeys)
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

            return IndexReadResult.Found(keys, needsRepair);
        }

        private static AppSurfaceLocalSecretIdentity IndexIdentity(string applicationName, string environment, string? keyPrefix) =>
            new(applicationName, environment, keyPrefix, IndexKey, $"appsurface:{applicationName}:{environment}:{keyPrefix}:{IndexKey}");

        private static AppSurfaceLocalSecretIdentityResult KeyIdentity(string applicationName, string environment, string? keyPrefix, string key) =>
            new AppSurfaceLocalSecretIdentityNormalizer().Normalize(applicationName, environment, keyPrefix, key);

        private static AppSurfaceLocalSecretDiagnostic InvalidIndexDiagnostic(string cause) =>
            new(
                "local-secret-index-invalid",
                "Local secret index is invalid.",
                cause,
                "Remove the invalid platform index entry, then set the intended LocalSecrets keys again.",
                "local-secrets-troubleshooting");

        private sealed record IndexReadResult(
            LocalSecretResultStatus Status,
            IReadOnlyCollection<string> Keys,
            bool NeedsRepair,
            AppSurfaceLocalSecretDiagnostic? Diagnostic)
        {
            public static IndexReadResult Found(IReadOnlyCollection<string> keys, bool needsRepair) =>
                new(LocalSecretResultStatus.Found, keys, needsRepair, null);

            public static IndexReadResult Failed(LocalSecretResultStatus status, AppSurfaceLocalSecretDiagnostic diagnostic) =>
                new(status, [], false, diagnostic);
        }
    }

    internal abstract class CommandBackedLocalSecretStore : IndexedLocalSecretStore
    {
        private readonly IPlatformSecretCommandRunner _commandRunner;

        protected CommandBackedLocalSecretStore(IPlatformSecretCommandRunner commandRunner)
        {
            ArgumentNullException.ThrowIfNull(commandRunner);

            _commandRunner = commandRunner;
        }

        protected AppSurfaceLocalSecretResult MapCommandFailure(PlatformSecretCommandResult result, string operation)
        {
            var status = result.Error.Contains("User interaction is not allowed", StringComparison.OrdinalIgnoreCase)
                         || result.Error.Contains("locked", StringComparison.OrdinalIgnoreCase)
                         || result.Error.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? LocalSecretResultStatus.Locked
                : LocalSecretResultStatus.Unavailable;
            return AppSurfaceLocalSecretResult.NotFound(
                status,
                new AppSurfaceLocalSecretDiagnostic(
                    status == LocalSecretResultStatus.Locked ? "local-secret-store-locked" : "local-secret-store-unavailable",
                    "Local secret store could not complete the request.",
                    $"The platform store failed during `{operation}` with exit code {result.ExitCode}.",
                    "Unlock the user secret store, run in an interactive desktop session, or use environment variables/key-per-file for this environment.",
                    "local-secrets-platform-compatibility",
                    retryable: true),
                Name);
        }

        protected PlatformSecretCommandResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput) =>
            _commandRunner.Run(fileName, arguments, standardInput);
    }

    internal interface IPlatformSecretCommandRunner
    {
        PlatformSecretCommandResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput);
    }

    internal sealed class DefaultPlatformSecretCommandRunner : IPlatformSecretCommandRunner
    {
        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);
        private readonly TimeSpan _commandTimeout;

        public static DefaultPlatformSecretCommandRunner Instance { get; } = new(DefaultCommandTimeout);

        internal DefaultPlatformSecretCommandRunner(TimeSpan commandTimeout)
        {
            if (commandTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(commandTimeout), commandTimeout, "Command timeout must be positive.");
            }

            _commandTimeout = commandTimeout;
        }

        public PlatformSecretCommandResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput)
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = standardInput != null;
            process.StartInfo.UseShellExecute = false;
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (standardInput != null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(_commandTimeout))
            {
                KillTimedOutProcess(process);
                return PlatformSecretCommandResult.TimedOut;
            }

            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
            var output = outputTask.GetAwaiter().GetResult().TrimEnd('\r', '\n');
            var error = errorTask.GetAwaiter().GetResult();
            return new PlatformSecretCommandResult(process.ExitCode, output, error);
        }

        private static void KillTimedOutProcess(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException ex)
            {
                TraceTimedOutProcessCleanupFailure(ex);
            }
            catch (NotSupportedException ex)
            {
                TraceTimedOutProcessCleanupFailure(ex);
            }
            catch (Win32Exception ex)
            {
                TraceTimedOutProcessCleanupFailure(ex);
            }
        }

        private static void TraceTimedOutProcessCleanupFailure(Exception exception) =>
            Trace.TraceWarning(
                "AppSurface LocalSecrets could not terminate a timed-out platform secret command. Exception={0}; HResult={1}.",
                exception.GetType().FullName,
                exception.HResult);
    }

    internal sealed record PlatformSecretCommandResult(int ExitCode, string Output, string Error)
    {
        internal const int TimedOutExitCode = -1;

        public static PlatformSecretCommandResult TimedOut { get; } =
            new(TimedOutExitCode, string.Empty, "Timed out waiting for the platform secret command to finish.");
    }
}
