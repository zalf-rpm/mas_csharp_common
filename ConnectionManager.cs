using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Capnp.FrameTracing;
using Capnp.Rpc;
using Mas.Schema.Persistence;
using Exception = System.Exception;

// added

namespace Mas.Infrastructure.Common;

public class ConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, TcpRpcClient> _connections = new();
    private TcpRpcServer _server;

    // Diagnostic: set CAPNP_TRACE_DIR to an existing/creatable directory to have every
    // outgoing TcpRpcClient connection dump its raw Tx/Rx frames to a file there.
    private static readonly string TraceDir = Environment.GetEnvironmentVariable("CAPNP_TRACE_DIR");

    private static void MaybeAttachTracer(TcpRpcClient con, string label)
    {
        if (string.IsNullOrWhiteSpace(TraceDir))
            return;

        try
        {
            Directory.CreateDirectory(TraceDir);
            var fileName =
                $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}_{SanitizeForFileName(label)}_{Guid.NewGuid():N}.trace.log";
            var path = Path.Combine(TraceDir, fileName);
            var writer = new StreamWriter(path, false) { AutoFlush = true };
            con.AttachTracer(new RpcFrameTracer(writer, true));
            Console.WriteLine($"ConnectionManager: tracing '{label}' to {path}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"ConnectionManager: failed to attach tracer for '{label}': {e.Message}");
        }
    }

    private static string SanitizeForFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    /// <summary>
    ///     Diagnostic helper: renders a SturdyRef's vat address/id and token so it can be logged
    ///     and compared at different points in its lifetime (e.g. right after it was received
    ///     from one connection vs. right before it is forwarded as a parameter on another).
    /// </summary>
    public static string DescribeSturdyRef(SturdyRef sr)
    {
        if (sr == null)
            return "<null>";

        var addr = sr.Vat?.Address;
        var addrDesc =
            addr == null ? "<null>"
            : addr.which == Address.WHICH.Host ? $"{addr.Host}:{addr.Port}"
            : addr.which == Address.WHICH.Ip6 ? $"[ip6 {addr.Ip6?.Lower64:x}{addr.Ip6?.Upper64:x}]:{addr.Port}"
            : $"<undefined which, port {addr.Port}>";

        var vatId = sr.Vat?.Id;
        var vatIdDesc =
            vatId == null
                ? "<null>"
                : $"{vatId.PublicKey0:x16}{vatId.PublicKey1:x16}{vatId.PublicKey2:x16}{vatId.PublicKey3:x16}";

        var tokenDesc =
            sr.LocalRef == null ? "<null>"
            : sr.LocalRef.which == SturdyRef.Token.WHICH.Text ? $"text:{sr.LocalRef.Text}"
            : sr.LocalRef.which == SturdyRef.Token.WHICH.Data
                ? $"data:{Convert.ToBase64String((sr.LocalRef.Data ?? Array.Empty<byte>()).ToArray())}"
            : "<undefined which>";

        return $"vat={addrDesc} vatId={vatIdDesc} token={tokenDesc}";
    }

    public int DefaultSslPort { get; set; } = 443;

    public Restorer Restorer { get; set; }

    public bool NoConnectionCaching { get; set; } = true;

    public ushort Port => (ushort)_server.Port;

    public void Dispose()
    {
        Dispose(true);
    }

    public static string GetLocalIPAddress(
        string connectToHost = "dns.google",
        int connectToPort = 443
    )
    {
        var localIP = "127.0.0.1";
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, 0);
            socket.Connect(connectToHost, connectToPort);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            localIP = endPoint.Address.ToString();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }

        return localIP;
    }

    protected virtual void Dispose(bool disposing)
    {
        //Console.WriteLine("Disposing ConnectionManager");

        //if(_Connections.Any()) Console.WriteLine("ConnectionManager: Disposing connections");
        foreach (var (key, con) in _connections)
            try
            {
                con?.Dispose();
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    "Exception thrown while disposing connection (TcpRpcClient): "
                        + key
                        + " Exception: "
                        + e.Message
                );
            }

        try
        {
            //Console.WriteLine("ConnectionManager: Disposing Capnp.Rpc.TcpRpcServer");
            _server?.Dispose();
        }
        catch (Exception e)
        {
            Console.WriteLine(
                "Exception thrown while disposing TcpRpcServer. Exception: " + e.Message
            );
        }
    }

    public async Task<TRemoteInterface> Connect<TRemoteInterface>(SturdyRef sturdyRef)
        where TRemoteInterface : class, IDisposable
    {
        // We assume that a sturdy ref url looks always like
        // capnp://vat-id_base64-curve25519-public-key@host:port/sturdy-ref-token
        var vatId = sturdyRef.Vat.Id;
        var port = sturdyRef?.Vat?.Address?.Port ?? 0;
        var srToken =
            (
                sturdyRef?.LocalRef.which == SturdyRef.Token.WHICH.Text
                    ? sturdyRef.LocalRef.Text
                    : sturdyRef?.LocalRef.Data.ToString()
            ) ?? "";
        var host = sturdyRef?.Vat?.Address?.Host ?? ""; // Hostname to use for TLS/SNI
        var addressPort = $"{host}:{port}";

        Console.WriteLine(
            $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} Connect(SturdyRef): {DescribeSturdyRef(sturdyRef)}"
        );

        if (string.IsNullOrWhiteSpace(host))
            return null;

        // Resolve to a single IP to avoid multi-endpoint connect path on Linux
        var connectHost = await ResolveConnectHostAsync(host);
        var attemptTls = !IPAddress.TryParse(host, out _); // try TLS only if we have a hostname

        var retryCount = 3;
        while (retryCount > 0)
        {
            try
            {
                TcpRpcClient con = null;

                if (attemptTls)
                    con = await TryTlsConnectAsync(
                        host,
                        connectHost,
                        port == 0 ? DefaultSslPort : port
                    );

                if (con == null)
                {
                    // Fallback or direct: Plain TCP (preserve original caching behavior)
                    con = NoConnectionCaching
                        ? new TcpRpcClient()
                        : _connections.GetOrAdd(addressPort, new TcpRpcClient());
                    MaybeAttachTracer(con, $"plain_{addressPort}");
                    con.Connect(connectHost, port);
                    if (con.WhenConnected == null)
                        return null;
                    await con.WhenConnected;
                }

                Console.WriteLine(
                    $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} connected"
                );
                Console.WriteLine(
                    $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} trying to restore srToken: {srToken}"
                );
                if (!string.IsNullOrEmpty(srToken))
                {
                    var restorer = con.GetMain<IRestorer>();
                    //var srTokenArr = Convert.FromBase64String(Restorer.FromBase64Url(srToken));
                    //var srToken = System.Text.Encoding.UTF8.GetString(srTokenArr);
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter((4 - retryCount) * 1000);
                    var cap = await restorer.Restore(
                        new Schema.Persistence.Restorer.RestoreParams
                        {
                            LocalRef = new SturdyRef.Token { Text = srToken },
                        },
                        cts.Token
                    );
                    Console.WriteLine(
                        $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} received restorer cap"
                    );
                    var cast_cap = cap.Cast<TRemoteInterface>(true);
                    Console.WriteLine(
                        $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} casted cap to requested interface"
                    );
                    return cast_cap;
                }

                var bootstrap = con.GetMain<TRemoteInterface>();
                Console.WriteLine(
                    $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} returning bootstrap cap"
                );
                return bootstrap;
            }
            catch (ArgumentOutOfRangeException aoore)
            {
                Console.WriteLine(
                    $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} ArgumentOutOfRangeException: {aoore.Message}"
                );
                _connections.TryRemove(addressPort, out _);
            }
            catch (RpcException rpce)
            {
                Console.WriteLine(
                    $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} RpcException: {rpce.Message}"
                );
                _connections.TryRemove(addressPort, out _);
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} System.Exception: {e.Message}"
                );
                _connections.TryRemove(addressPort, out _);
                throw;
            }

            retryCount--;
            Console.WriteLine(
                $"ConnectionManager: ThreadId: {Environment.CurrentManagedThreadId} retrying to connect for {retryCount} more times"
            );
        }

        return null;
    }

    public async Task<TRemoteInterface> Connect<TRemoteInterface>(string sturdyRef)
        where TRemoteInterface : class, IDisposable
    {
        // We assume that a sturdy ref url looks always like
        // capnp://vat-id_base64-curve25519-public-key@host:port/sturdy-ref-token
        if (!sturdyRef.StartsWith("capnp://"))
            return null;
        var vatIdBase64Url = "";
        ushort port = 0;
        var srToken = "";
        var host = ""; // Hostname to use for TLS/SNI

        var rest = sturdyRef[8..];
        // is Unix domain socket
        if (rest.StartsWith("/"))
        {
            rest = rest[1..];
        }
        else
        {
            var vatIdAndRest = rest.Split("@");
            if (vatIdAndRest.Length > 1)
                vatIdBase64Url = vatIdAndRest[0];
            if (vatIdAndRest[^1].Contains('/'))
            {
                var addressPortAndRest = vatIdAndRest[^1].Split("/");
                if (addressPortAndRest.Length > 0)
                {
                    var addressPortRaw = addressPortAndRest[0];

                    // capture hostname for TLS BEFORE any replacement
                    var rawHostPort = addressPortRaw.Split(":");
                    if (rawHostPort.Length > 0)
                        host = rawHostPort[0];
                    if (rawHostPort.Length > 1)
                        port = ushort.Parse(rawHostPort[1]);
                }

                if (addressPortAndRest.Length > 1)
                    srToken = addressPortAndRest[1];
            }
        }

        return await Connect<TRemoteInterface>(
            Restorer.SturdyRef(vatIdBase64Url, host, port, srToken)
        );
    }

    // Resolves the hostname to a single IP (prefers IPv4), falling back to the original host on failure.
    private async Task<string> ResolveConnectHostAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return host;

        var connectHost = host;
        if (!IPAddress.TryParse(connectHost, out _))
            try
            {
                var ips = await Dns.GetHostAddressesAsync(connectHost);
                var ipv4 = Array.Find(ips, ip => ip.AddressFamily == AddressFamily.InterNetwork);
                connectHost = (ipv4 ?? ips[0]).ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"ConnectionManager: DNS resolve failed for '{connectHost}': {ex.Message}. Using hostname directly."
                );
                connectHost = host;
            }

        return connectHost;
    }

    // Attempt a TLS connection; returns a connected client on success, null to fallback, rethrows on unknown errors.
    private async Task<TcpRpcClient> TryTlsConnectAsync(
        string sniHost,
        string connectHost,
        int port
    )
    {
        var tlsCon = new TcpRpcClient();
        try
        {
            MaybeAttachTracer(tlsCon, $"tls_{connectHost}_{port}");
            tlsCon.InjectMidlayer(inner =>
            {
                var ssl = new SslStream(inner, false);
                ssl.AuthenticateAsClient(sniHost);
                return ssl;
            });
            Console.WriteLine($"TLS attempt (SNI host: {sniHost}, connect: {connectHost}:{port})");
            tlsCon.Connect(connectHost, port);
            if (tlsCon.WhenConnected == null)
            {
                tlsCon.Dispose();
                return null;
            }

            await tlsCon.WhenConnected;
            return tlsCon;
        }
        catch (AuthenticationException aex)
        {
            Console.WriteLine(
                $"TLS not supported or failed auth: {aex.Message}. Falling back to plain TCP."
            );
            tlsCon.Dispose();
            return null;
        }
        catch (IOException ioex)
        {
            Console.WriteLine($"TLS I/O failure: {ioex.Message}. Falling back to plain TCP.");
            tlsCon.Dispose();
            return null;
        }
        catch
        {
            tlsCon.Dispose();
            throw;
        }
    }

    public void Bind(IPAddress address, int tcpPort, object bootstrap)
    {
        _server?.Dispose();

        _server = new TcpRpcServer();
        _server.AddBuffering();
        _server.Main = bootstrap;
        _server.StartAccepting(address, tcpPort);
    }
}
