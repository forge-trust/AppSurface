using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;

namespace ForgeTrust.AppSurface.Auth.Keycloak;
/// <summary>One exact host-approved material association. URLs do not authorize one another.</summary>
internal sealed record ApprovedMaterial(string Id, string Sha256, long Length, ImmutableArray<Uri> PermittedUris, string Coordinate, bool ExactLength = true);
/// <summary>Records an origin authorization for portable plan provenance.</summary>
internal sealed record AcquiredMaterial(string Id, string Sha256, long Length, string FinalUri, string Coordinate, int Redirects);
/// <summary>A safe acquisition classification; no untrusted response text or URI reaches the exception.</summary>
internal sealed class MaterialAcquisitionException(bool transient, string code) : Exception(code)
{
    internal bool Transient { get; } = transient;
}

/// <summary>Acquires one approved material once, with exact redirect matching and bounded hashing.</summary>
internal static class MaterialAcquisition
{
    internal static async Task<AcquiredMaterial> DownloadAsync(ApprovedMaterial approved, string absentDestination, TimeSpan deadline, CancellationToken cancellationToken, InvocationLimits? limits = null)
    {
        limits ??= new InvocationLimits(Deadline: deadline);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        return await DownloadCoreAsync(client, approved, absentDestination, deadline, cancellationToken, limits).ConfigureAwait(false);
    }

    // Intentionally internal transport seam permits protocol controls without changing the public production trust boundary.
    internal static async Task<AcquiredMaterial> DownloadCoreAsync(HttpClient transport, ApprovedMaterial approved, string absentDestination, TimeSpan deadline, CancellationToken cancellationToken, InvocationLimits? limits = null)
    {
        limits ??= new InvocationLimits(Deadline: deadline);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(300))
            throw new MaterialAcquisitionException(false, "MATERIAL_POLICY_INVALID");
        if (limits.Deadline is null || limits.EffectiveDeadline > deadline)
            limits = limits with
            {
                Deadline = deadline
            };
        ValidateLimits(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (approved.PermittedUris.IsDefaultOrEmpty || approved.PermittedUris.Length > limits.MaxRedirects + 1 || approved.Length <= 0 || approved.Length > limits.MaxFileBytes || approved.Sha256.Length != 64 || approved.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new MaterialAcquisitionException(false, "MATERIAL_POLICY_INVALID");
        var allowed = approved.PermittedUris.Select(CanonicalExactUri).ToImmutableHashSet(StringComparer.Ordinal);
        if (allowed.Count != approved.PermittedUris.Length)
            throw new MaterialAcquisitionException(false, "MATERIAL_POLICY_AMBIGUOUS");
        var destination = Path.GetFullPath(absentDestination);
        if (Path.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
            throw new MaterialAcquisitionException(false, "MATERIAL_DESTINATION_CONFLICT");
        var parent = Path.GetDirectoryName(destination)!;
        RequireSafeAncestors(parent);
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        var stage = Path.Combine(parent, ".acquire-" + Guid.NewGuid().ToString("N"));
        using var stop = CreateDeadline(cancellationToken, limits.EffectiveDeadline);
        var current = approved.PermittedUris[0];
        try
        {
            for (var hops = 0; hops <= limits.MaxRedirects; hops++)
            {
                var exact = CanonicalExactUri(current);
                if (!allowed.Contains(exact))
                    throw new MaterialAcquisitionException(false, "MATERIAL_ORIGIN_REJECTED");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is not Uri final || CanonicalExactUri(final) != exact)
                    throw new MaterialAcquisitionException(false, "MATERIAL_REDIRECT_BYPASS");
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hops == limits.MaxRedirects || response.Headers.Location is not Uri location)
                        throw new MaterialAcquisitionException(false, "MATERIAL_REDIRECT_LIMIT");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    _ = CanonicalExactUri(current);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    var status = (int)response.StatusCode;
                    throw new MaterialAcquisitionException(status == 429 || status >= 500 && status <= 599, "MATERIAL_HTTP_FAILED");
                }

                if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength is long declared && (declared > approved.Length || approved.ExactLength && declared != approved.Length))
                    throw new MaterialAcquisitionException(false, "MATERIAL_LENGTH_INVALID");
                await using var input = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[65536];
                    long length = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        length = checked(length + read);
                        if (length > approved.Length)
                            throw new MaterialAcquisitionException(false, "MATERIAL_LENGTH_INVALID");
                        hash.AppendData(buffer, 0, read);
                        TemporaryDiskBudget.Account(read);
                        await output.WriteAsync(buffer.AsMemory(0, read), stop.Token).ConfigureAwait(false);
                    }

                    if (approved.ExactLength && length != approved.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != approved.Sha256)
                        throw new MaterialAcquisitionException(false, "MATERIAL_DIGEST_MISMATCH");
                    output.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                RejectLinks(parent);
                if (Path.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
                    throw new MaterialAcquisitionException(false, "MATERIAL_DESTINATION_CONFLICT");
                AtomicFilePublication.Publish(stage, destination);
                return new AcquiredMaterial(approved.Id, approved.Sha256, new FileInfo(destination).Length, exact, approved.Coordinate, hops);
            }

            throw new MaterialAcquisitionException(false, "MATERIAL_REDIRECT_LIMIT");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MaterialAcquisitionException(true, "MATERIAL_TIMEOUT");
        }
        catch (HttpRequestException)
        {
            throw new MaterialAcquisitionException(true, "MATERIAL_NETWORK_FAILED");
        }
        finally
        {
            try
            {
                if (File.Exists(stage))
                    File.Delete(stage);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string CanonicalExactUri(Uri uri)
    {
        var raw = uri.OriginalString;
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || uri.Port is <= 0 or > 65535 || raw.Any(c => c < 33 || c > 126) || raw.Contains('\\') || raw.Contains('%') || uri.Host.EndsWith('.') || !string.Equals(uri.DnsSafeHost, uri.IdnHost, StringComparison.OrdinalIgnoreCase))
            throw new MaterialAcquisitionException(false, "MATERIAL_URI_INVALID");
        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        var pathOffset = raw.IndexOf('/', scheme + 3);
        var rawPath = pathOffset < 0 ? "/" : raw[pathOffset..];
        if (rawPath.Split('/').Any(segment => segment is "." or "..") || rawPath.Contains("//", StringComparison.Ordinal))
            throw new MaterialAcquisitionException(false, "MATERIAL_URI_AMBIGUOUS");
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "https://" + uri.IdnHost.ToLowerInvariant() + port + uri.AbsolutePath;
    }

    private static void RejectLinks(string parent)
    {
        for (var current = new DirectoryInfo(parent); current is not null; current = current.Parent)
            if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new MaterialAcquisitionException(false, "MATERIAL_OUTPUT_LINK_REJECTED");
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken caller, TimeSpan deadline)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(caller);
        source.CancelAfter(deadline);
        return source;
    }

    private static void RequireSafeAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 || new DirectoryInfo(current).LinkTarget is not null))
                throw new MaterialAcquisitionException(false, "MATERIAL_OUTPUT_LINK_REJECTED");
        }
    }

    private static void ValidateLimits(InvocationLimits limits)
    {
        if (limits.MaxFileBytes <= 0 || limits.MaxRedirects is < 0 or > 32 || limits.EffectiveDeadline <= TimeSpan.Zero || limits.EffectiveDeadline > TimeSpan.FromSeconds(300))
            throw new MaterialAcquisitionException(false, "MATERIAL_POLICY_INVALID");
    }
}
