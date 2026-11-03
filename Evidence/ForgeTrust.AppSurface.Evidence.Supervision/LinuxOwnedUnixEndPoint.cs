using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Serializes a UNIX pathname while leaving its filesystem lifetime with the actual workspace.</summary>
/// <remarks>
/// This sealed internal data adapter derives directly from <see cref="EndPoint"/>. Bind must receive this
/// adapter, rather than the delegated <see cref="UnixDomainSocketEndPoint"/> returned by <see cref="Create"/>.
/// The pinned .NET 10.0.12 Socket implementation registers pathname deletion only for a bound endpoint whose
/// runtime type is UnixDomainSocketEndPoint. This adapter therefore prevents Socket disposal from deleting
/// either the workspace's retained name or an object subsequently substituted at that name. It creates no
/// socket, performs no filesystem operation, authenticates no peer and grants no custody or admission.
/// The actual listener retains every existing collision, owner, descriptor and inode check.
/// <para>
/// The standard endpoint supplies the address layout, address family and kernel-address reconstruction;
/// reconstructed endpoints are ordinary unbound UNIX endpoint data. Do not use a reconstructed endpoint to
/// rebind the workspace name. Path ownership is established by the native workspace, never by this object.
/// </para>
/// <para>
/// Runtime contract: <see href="https://github.com/dotnet/runtime/blob/4271d88e0aebf3d04f188f1334c2220d80555ef6/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L789-L826">Bind and DoBind</see>,
/// <see href="https://github.com/dotnet/runtime/blob/4271d88e0aebf3d04f188f1334c2220d80555ef6/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L3331-L3347">endpoint serialization</see>
/// and <see href="https://github.com/dotnet/runtime/blob/4271d88e0aebf3d04f188f1334c2220d80555ef6/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs#L3513-L3523">disposal</see>.
/// A runtime upgrade requires the local socket regression and the separate native retained-name checks.
/// </para>
/// </remarks>
internal sealed class LinuxOwnedUnixEndPoint : EndPoint
{
    private readonly UnixDomainSocketEndPoint _endpoint;

    /// <summary>Creates pathname data using the existing absolute, normalized, 100 UTF-8 byte control grammar.</summary>
    /// <param name="path">A nonabstract pathname with no empty/dot components, control characters, dollars or percents.</param>
    /// <exception cref="ArgumentNullException">The pathname is null.</exception>
    /// <exception cref="ArgumentException">The pathname violates the fixed control-path grammar.</exception>
    /// <exception cref="PlatformNotSupportedException">The standard endpoint cannot support UNIX sockets.</exception>
    internal LinuxOwnedUnixEndPoint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            if (path.Length is 0 or > 100 || path[0] != '/' || path.Any(char.IsControl)
                || path.Contains('$') || path.Contains('%')
                || new UTF8Encoding(false, true).GetByteCount(path) > 100
                || path.Split('/').Skip(1).Any(part => part.Length == 0 || part is "." or ".."))
                throw InvalidPath();
        }
        catch (EncoderFallbackException) { throw InvalidPath(); }
        _endpoint = new UnixDomainSocketEndPoint(path);
    }

    /// <summary>Gets the standard UNIX address family as data, without checking native ownership.</summary>
    public override AddressFamily AddressFamily => _endpoint.AddressFamily;

    /// <summary>Returns a fresh standard UNIX socket address; callers cannot mutate this adapter's pathname.</summary>
    /// <returns>The exact standard endpoint serialization for the validated pathname.</returns>
    public override SocketAddress Serialize() => _endpoint.Serialize();

    /// <summary>Reconstructs ordinary UNIX endpoint data from the kernel address using the standard implementation.</summary>
    /// <param name="socketAddress">An actual or sampled UNIX socket address; no identity is authenticated.</param>
    /// <returns>An ordinary <see cref="UnixDomainSocketEndPoint"/>, including an unnamed UNIX peer when supplied.</returns>
    /// <exception cref="ArgumentNullException">The address is null.</exception>
    /// <exception cref="ArgumentException">The address family is not UNIX.</exception>
    public override EndPoint Create(SocketAddress socketAddress)
    {
        ArgumentNullException.ThrowIfNull(socketAddress);
        if (socketAddress.Family != AddressFamily.Unix)
            throw new ArgumentException("A UNIX socket address is required.", nameof(socketAddress));
        return _endpoint.Create(socketAddress);
    }

    /// <summary>Returns standard pathname data; it must not be emitted in closed public failure diagnostics.</summary>
    public override string ToString() => _endpoint.ToString();

    private static ArgumentException InvalidPath() => new("The control socket pathname is invalid.", "path");
}
