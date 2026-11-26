using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>Runs bounded, read-only Docker inventory commands against an explicitly approved local socket.</summary>
/// <remarks>Failures are deliberately generic and never include command arguments, daemon output, or inspected JSON.</remarks>
internal sealed class SupervisedDocker
{
    private const int MaximumOutputBytes = 1024 * 1024;
    private const int MaximumArgumentCount = 128;
    private const int MaximumArgumentBytes = 64 * 1024;
    private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _endpoint;
    private readonly string _clientExecutable = "docker";
    private readonly Func<CancellationToken, string[], Task<string>>? _inventoryCommand;

    /// <summary>Uses the approved local socket and can accept deterministic command responses for inventory tests.</summary>
    /// <param name="endpoint">The exact approved Docker Unix endpoint.</param>
    /// <param name="inventoryCommand">Test transport. Production calls use the bounded Docker client process.</param>
    internal SupervisedDocker(string endpoint, Func<CancellationToken, string[], Task<string>>? inventoryCommand = null)
    {
        _endpoint = endpoint;
        _inventoryCommand = inventoryCommand;
    }

    /// <summary>Runs the production command supervision path with a synthetic client executable.</summary>
    /// <param name="endpoint">The exact approved Docker Unix endpoint.</param>
    /// <param name="clientExecutable">Absolute executable path owned by the test.</param>
    internal SupervisedDocker(string endpoint, string clientExecutable) : this(endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientExecutable);
        if (!Path.IsPathFullyQualified(clientExecutable))
            throw new ArgumentException("The synthetic Docker client path must be absolute.", nameof(clientExecutable));
        _clientExecutable = clientExecutable;
    }

    /// <summary>Runs one Docker CLI argument vector without a shell and returns bounded, trimmed standard output.</summary>
    /// <param name="ct">Cancellation token for the command; cancellation kills the local Docker client.</param>
    /// <param name="args">Docker subcommand and arguments, passed individually through <see cref="ProcessStartInfo.ArgumentList"/>.</param>
    /// <returns>Standard output, limited to one mebibyte before UTF-8 decoding.</returns>
    /// <remarks>After caller cancellation the client is killed without waiting to confirm its exit. Internal deadline cleanup is bounded to one second and is interrupted immediately if the caller cancels.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancelled the command.</exception>
    /// <exception cref="IOException">The endpoint, process, output, exit status, or command deadline could not be verified.</exception>
    internal async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsSupportedEndpoint(_endpoint) || args is null || args.Length > MaximumArgumentCount
            || args.Any(argument => argument is null || argument.IndexOf('\0') >= 0)
            || ArgumentBytes(args) > MaximumArgumentBytes)
            throw Failure();

        if (_inventoryCommand is not null)
        {
            try
            {
                var output = await _inventoryCommand(ct, args).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (StrictUtf8.GetByteCount(output) > MaximumOutputBytes)
                    throw Failure();
                return output.Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch { throw Failure(); }
        }

        var start = new ProcessStartInfo(_clientExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add(_endpoint);
        foreach (var argument in args)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(CommandDeadline);
        using var command = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        Task<byte[]>? stdout = null;
        Task? stderr = null;
        try
        {
            if (!process.Start())
                throw Failure();

            stdout = ReadStdoutAsync(process.StandardOutput.BaseStream, command);
            stderr = DiscardStderrAsync(process.StandardError.BaseStream, command);
            await process.WaitForExitAsync(command.Token).ConfigureAwait(false);
            var bytes = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw Failure();

            return StrictUtf8.GetString(bytes).Trim();
        }
        catch
        {
            if (ct.IsCancellationRequested)
            {
                KillWithoutWaiting(process, command, stdout, stderr);
                throw new OperationCanceledException(ct);
            }
            await StopAndReapAsync(process, command, stdout, stderr, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw Failure();
        }
    }

    /// <summary>Reads the selected local daemon's identifier using Docker's informational command.</summary>
    /// <param name="ct">Cancellation token for the bounded command.</param>
    /// <returns>The nonempty daemon identifier reported by the selected local socket.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled the command.</exception>
    /// <exception cref="IOException">The daemon identifier could not be verified.</exception>
    internal async Task<string> DaemonAsync(CancellationToken ct)
    {
        try
        {
            var id = await RunAsync(ct, "info", "--format", "{{.ID}}").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl))
                throw Failure();
            return id;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Failure(); }
    }

    /// <summary>Inspects one full native container identifier and returns its complete Docker JSON document.</summary>
    /// <param name="id">The 64-character lowercase hexadecimal container identifier.</param>
    /// <param name="ct">Cancellation token for the bounded Docker command.</param>
    /// <returns>A caller-owned document containing exactly one inspected container; the caller must dispose it.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled an inventory command.</exception>
    /// <exception cref="IOException">The container inspection could not be verified.</exception>
    internal async Task<JsonDocument> InspectAsync(string id, CancellationToken ct)
    {
        try
        {
            if (!IsNativeId(id))
                throw Failure();

            var json = await RunAsync(ct, "inspect", id).ConfigureAwait(false);
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            try
            {
                var container = SingleInspectObject(document);
                if (RequiredString(container, "Id") != id)
                    throw Failure();
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Failure(); }
    }

    /// <summary>Returns a local Docker volume only after an exact list snapshot and matching inspect.</summary>
    /// <param name="name">The exact Docker volume name to inventory.</param>
    /// <param name="ct">Cancellation token for each bounded Docker command.</param>
    /// <returns><see langword="null"/> only when the exact name is absent from a successful list snapshot.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled an inventory command.</exception>
    /// <exception cref="IOException">The list, inspection, identity, local-volume profile, or mountpoint could not be verified.</exception>
    internal async Task<SupervisedVolume?> VolumeAsync(string name, CancellationToken ct)
    {
        try
        {
            if (!IsDockerName(name))
                throw Failure();

            var snapshot = await RunAsync(ct, "volume", "ls", "--format", "{{.Name}}").ConfigureAwait(false);
            if (!snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal))
                return null;

            var json = await RunAsync(ct, "volume", "inspect", name).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var volume = SingleInspectObject(document);
            var observedName = RequiredString(volume, "Name");
            var driver = RequiredString(volume, "Driver");
            var scope = RequiredString(volume, "Scope");
            var createdAt = RequiredString(volume, "CreatedAt");
            var mountpoint = RequiredString(volume, "Mountpoint");
            var options = RequiredProperty(volume, "Options");
            var labels = RequiredProperty(volume, "Labels");

            if (!string.Equals(observedName, name, StringComparison.Ordinal)
                || !string.Equals(driver, "local", StringComparison.Ordinal)
                || !string.Equals(scope, "local", StringComparison.Ordinal)
                || string.IsNullOrEmpty(createdAt)
                || !IsValidMountpoint(mountpoint)
                || options.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)
                || options.ValueKind == JsonValueKind.Object && options.EnumerateObject().Any()
                || labels.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                throw Failure();

            return new SupervisedVolume(observedName, driver, scope, createdAt, mountpoint);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Failure(); }
    }

    /// <summary>Lists full native identifiers for all containers attached to the exact named volume.</summary>
    /// <param name="volumeName">The exact Docker volume name whose attachments are observed.</param>
    /// <param name="ct">Cancellation token for the bounded Docker command.</param>
    /// <returns>Distinct full native container identifiers, or an empty array for a successful empty snapshot.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled the command.</exception>
    /// <exception cref="IOException">The attachment snapshot or any returned identifier could not be verified.</exception>
    internal async Task<string[]> AttachmentsAsync(string volumeName, CancellationToken ct)
    {
        try
        {
            if (!IsDockerName(volumeName))
                throw Failure();

            var output = await RunAsync(ct, "ps", "-aq", "--no-trunc", "--filter", "volume=" + volumeName).ConfigureAwait(false);
            return ParseIds(output);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Failure(); }
    }

    /// <summary>Observes a container by exact name, optional full identifier, and exact run-ownership label.</summary>
    /// <param name="name">The exact Docker container name, without Docker inspect's leading slash.</param>
    /// <param name="expectedId">The required full native identifier, or <see langword="null"/> when only the name is known.</param>
    /// <param name="expectedRun">The exact value required for the <c>appsurface.run</c> configuration label.</param>
    /// <param name="ct">Cancellation token for each bounded Docker command.</param>
    /// <returns>A detached, immutable observation, or <see langword="null"/> only when the required name and identifier are both absent.</returns>
    /// <exception cref="OperationCanceledException">The caller cancelled an inventory command.</exception>
    /// <exception cref="IOException">The name, identifier, inspect snapshot, run label, or required field shape could not be verified.</exception>
    internal async Task<SupervisedObservedContainer?> ContainerAsync(string name, string? expectedId, string expectedRun, CancellationToken ct)
    {
        try
        {
            if (!IsDockerName(name) || string.IsNullOrEmpty(expectedRun) || expectedRun.Any(char.IsControl)
                || expectedId is not null && !IsNativeId(expectedId))
                throw Failure();

            var byName = ParseIds(await RunAsync(ct, "ps", "-aq", "--no-trunc", "--filter", "name=^/" + name + "$").ConfigureAwait(false));
            if (byName.Length > 1)
                throw Failure();

            if (byName.Length == 0)
            {
                if (expectedId is null)
                    return null;

                var byId = ParseIds(await RunAsync(ct, "ps", "-aq", "--no-trunc", "--filter", "id=" + expectedId).ConfigureAwait(false));
                if (byId.Length == 0)
                    return null;

                // An ID still present without its exact recorded name is conflicting evidence.
                throw Failure();
            }
            if (expectedId is not null && byName[0] != expectedId)
                throw Failure();

            var id = expectedId ?? byName[0];
            using var document = await InspectAsync(id, ct).ConfigureAwait(false);
            var observed = ReadObservedContainer(SingleInspectObject(document), name, id, expectedRun);
            return observed;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw Failure(); }
    }

    private static async Task<byte[]> ReadStdoutAsync(Stream stream, CancellationTokenSource command)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, command.Token).ConfigureAwait(false);
            if (count == 0)
                return output.ToArray();
            if (output.Length + count > MaximumOutputBytes)
            {
                command.Cancel();
                throw Failure();
            }
            output.Write(buffer, 0, count);
        }
    }

    private static async Task DiscardStderrAsync(Stream stream, CancellationTokenSource command)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, command.Token).ConfigureAwait(false);
            if (count == 0)
                return;
            total += count;
            if (total > MaximumOutputBytes)
            {
                command.Cancel();
                throw Failure();
            }
        }
    }

    private static void KillWithoutWaiting(Process process, CancellationTokenSource command, Task? stdout, Task? stderr)
    {
        try { command.Cancel(); } catch { }
        KillClient(process);
        ObserveFailure(stdout);
        ObserveFailure(stderr);
    }

    private static async Task StopAndReapAsync(Process process, CancellationTokenSource command, Task? stdout, Task? stderr,
        CancellationToken callerToken)
    {
        try { command.Cancel(); } catch { }
        KillClient(process);

        using var deadline = new CancellationTokenSource(CleanupDeadline);
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        if (!callerToken.IsCancellationRequested)
        {
            try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); } catch { }
            if (!callerToken.IsCancellationRequested && stdout is not null && stderr is not null)
            {
                try { await Task.WhenAll(stdout, stderr).WaitAsync(cleanup.Token).ConfigureAwait(false); } catch { }
            }
        }
        ObserveFailure(stdout);
        ObserveFailure(stderr);
    }

    private static void KillClient(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch { }
    }

    private static void ObserveFailure(Task? task)
    {
        if (task is not null)
            _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static bool IsSupportedEndpoint(string value)
    {
        try
        {
            if (string.IsNullOrEmpty(value))
                return false;

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userSocket = string.IsNullOrEmpty(profile)
                ? string.Empty
                : "unix://" + Path.GetFullPath(Path.Combine(profile, ".docker", "run", "docker.sock")).Replace('\\', '/');
            return string.Equals(value, "unix:///var/run/docker.sock", StringComparison.Ordinal)
                || string.Equals(value, userSocket, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static long ArgumentBytes(string[] args)
    {
        try { return args.Sum(argument => (long)StrictUtf8.GetByteCount(argument)); }
        catch { return long.MaxValue; }
    }

    private static bool IsDockerName(string value) => !string.IsNullOrEmpty(value)
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    private static bool IsNativeId(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidMountpoint(string value) => Path.IsPathFullyQualified(value)
        && value.StartsWith("/", StringComparison.Ordinal)
        && !value.Contains('\n') && !value.Contains('\r') && !value.Contains('\0') && !value.Contains(',');

    private static string[] ParseIds(string output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var ids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Any(id => !IsNativeId(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw Failure();
        return ids;
    }

    private static JsonElement SingleInspectObject(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1 || root[0].ValueKind != JsonValueKind.Object)
            throw Failure();
        return root[0];
    }

    private static SupervisedObservedContainer ReadObservedContainer(JsonElement item, string expectedName, string expectedId, string expectedRun)
    {
        var id = RequiredString(item, "Id");
        var nativeName = RequiredString(item, "Name");
        var image = RequiredString(RequiredObject(item, "Config"), "Image");
        var imageId = RequiredString(item, "Image");
        var configuration = RequiredObject(item, "Config");
        var labels = RequiredObject(configuration, "Labels");
        var run = RequiredString(labels, "appsurface.run");
        var host = RequiredObject(item, "HostConfig");
        var pidMode = RequiredString(host, "PidMode");
        var networkMode = RequiredString(host, "NetworkMode");
        var init = TryGetOptionalProperty(host, "Init", out var initElement)
            ? initElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False or JsonValueKind.Null => false,
                _ => throw Failure()
            }
            : false;
        var restartPolicy = RequiredString(RequiredObject(host, "RestartPolicy"), "Name");
        var privileged = RequiredBoolean(host, "Privileged");
        var readonlyRoot = RequiredBoolean(host, "ReadonlyRootfs");
        var user = RequiredString(configuration, "User");
        var state = RequiredObject(item, "State");
        var running = RequiredBoolean(state, "Running");
        var paused = RequiredBoolean(state, "Paused");
        var restarting = RequiredBoolean(state, "Restarting");
        var hostPid = RequiredInt32(state, "Pid");
        var status = RequiredString(state, "Status");

        if (id != expectedId || nativeName != "/" + expectedName || run != expectedRun
            || !IsNativeId(id) || string.IsNullOrEmpty(image) || string.IsNullOrEmpty(imageId) || string.IsNullOrEmpty(status))
            throw Failure();

        return new SupervisedObservedContainer(id, expectedName, image, imageId, pidMode, networkMode, init,
            running, paused, restarting, hostPid, status, restartPolicy, privileged, readonlyRoot, user, item.Clone());
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Object)
            throw Failure();
        return value;
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (!TryGetOptionalProperty(parent, name, out var value))
            throw Failure();
        return value;
    }

    private static bool TryGetOptionalProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw Failure();

        var found = false;
        value = default;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name))
                continue;
            if (found)
                throw Failure();
            found = true;
            value = property.Value;
        }
        return found;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
            throw Failure();
        return text;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Failure()
        };
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw Failure();
        return result;
    }

    private static IOException Failure() => new("Docker inventory could not be verified.");
}

/// <summary>Immutable, locally scoped volume identity returned by <see cref="SupervisedDocker.VolumeAsync"/>.</summary>
/// <param name="Name">Exact Docker volume name from the verified inspect object.</param>
/// <param name="Driver">Verified volume driver; only <c>local</c> is accepted.</param>
/// <param name="Scope">Verified volume scope; only <c>local</c> is accepted.</param>
/// <param name="CreatedAt">Creation timestamp string returned by Docker.</param>
/// <param name="Mountpoint">Absolute local mountpoint, excluding comma, NUL, and line breaks.</param>
/// <remarks>Record equality compares all five verified values using their normal ordinal string equality.</remarks>
internal sealed record SupervisedVolume(string Name, string Driver, string Scope, string CreatedAt, string Mountpoint);

/// <summary>Detached immutable view of a container inspect snapshot observed by <see cref="SupervisedDocker.ContainerAsync"/>.</summary>
/// <param name="Id">Full native 64-character lowercase hexadecimal container identifier.</param>
/// <param name="Name">Exact container name without Docker inspect's leading slash.</param>
/// <param name="Image">Configured image reference from <c>Config.Image</c>.</param>
/// <param name="ImageId">Native image identifier from Docker's top-level <c>Image</c> field.</param>
/// <param name="PidMode">Host configuration PID mode, including an empty default value.</param>
/// <param name="NetworkMode">Host configuration network mode.</param>
/// <param name="Init">Host configuration init setting; a missing or explicit null value is represented as false.</param>
/// <param name="Running">Container running state from the inspect snapshot.</param>
/// <param name="Paused">Container paused state from the inspect snapshot.</param>
/// <param name="Restarting">Container restarting state from the inspect snapshot.</param>
/// <param name="HostPid">Host process identifier reported by Docker, including zero when not assigned.</param>
/// <param name="Status">Human-readable Docker state status.</param>
/// <param name="RestartPolicy">Restart policy name from the host configuration.</param>
/// <param name="Privileged">Whether Docker reports privileged mode.</param>
/// <param name="ReadonlyRoot">Whether Docker reports a read-only root filesystem.</param>
/// <param name="User">Configured container user from <c>Config.User</c>.</param>
/// <param name="Snapshot">Detached full inspect object for parent-side validation; it is never written to diagnostics.</param>
internal sealed record SupervisedObservedContainer(string Id, string Name, string Image, string ImageId, string PidMode,
    string NetworkMode, bool Init, bool Running, bool Paused, bool Restarting, int HostPid, string Status,
    string RestartPolicy, bool Privileged, bool ReadonlyRoot, string User, JsonElement Snapshot);
