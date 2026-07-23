using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lorerim.Gui.Services.Nexus;

/// <summary>
/// Single-instance IPC for OAuth callbacks. The browser launches a second instance of this
/// app with the jackify:// URL as an argument; that instance forwards the URL over a unix
/// socket to the running instance and exits (see Program.cs).
/// </summary>
public class OAuthCallbackListener : IDisposable
{
    public event Action<Uri>? CallbackReceived;

    public static string SocketPath
    {
        get
        {
            var runtimeDir =
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                ?? Path.GetTempPath();
            return Path.Join(runtimeDir, "lorerim-autoinstall.sock");
        }
    }

    /// <summary>Starts listening. Returns false if another instance already owns the socket.</summary>
    public bool TryStart()
    {
        try
        {
            if (File.Exists(SocketPath))
            {
                // Either a live instance or a stale socket from a crash. Probe it.
                if (TrySend("ping"))
                {
                    return false;
                }
                File.Delete(SocketPath);
            }
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(4);
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_cts.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Sends a message (usually the callback URL) to the running instance.</summary>
    public static bool TrySend(string message)
    {
        try
        {
            using var client = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send(Encoding.UTF8.GetBytes(message));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            using (client)
            {
                int n;
                try
                {
                    n = await client.ReceiveAsync(buffer, ct);
                }
                catch (Exception)
                {
                    continue;
                }
                if (n <= 0)
                {
                    continue;
                }
                var message = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                if (
                    Uri.TryCreate(message, UriKind.Absolute, out var uri)
                    && uri.Scheme == ProtocolHandlerRegistrar.Scheme
                )
                {
                    CallbackReceived?.Invoke(uri);
                }
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        try
        {
            File.Delete(SocketPath);
        }
        catch (IOException)
        {
            // fine
        }
    }

    private Socket? _listener;
    private CancellationTokenSource? _cts;
}
