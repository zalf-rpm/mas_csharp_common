using Capnp.Rpc;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Security;
using System.Security.Authentication;
using System.IO; // added

namespace Mas.Infrastructure.Common
{
    public class ConnectionManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, TcpRpcClient> _connections = new();
        private TcpRpcServer _server;

        public Restorer Restorer { get; set; }

        public bool NoConnectionCaching { get; set; } = true;

        public ushort Port => (ushort)_server.Port;

        public void Dispose() => Dispose(true);

        public static string GetLocalIPAddress(string connectToHost = "dns.google", int connectToPort = 443)
        {
            var localIP = "127.0.0.1";
            try
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, 0);
                socket.Connect(connectToHost, connectToPort);
                var endPoint = socket.LocalEndPoint as IPEndPoint;
                localIP = endPoint.Address.ToString();
            }
            catch (System.Exception e)
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
            {
                try
                {
                    con?.Dispose();
                }
                catch (System.Exception e)
                {
                    Console.WriteLine("Exception thrown while disposing connection (TcpRpcClient): " + key + " Exception: " + e.Message);
                }
            }

            try
            {
                //Console.WriteLine("ConnectionManager: Disposing Capnp.Rpc.TcpRpcServer");
                _server?.Dispose();
            }
            catch (System.Exception e)
            {
                Console.WriteLine("Exception thrown while disposing TcpRpcServer. Exception: " + e.Message);
            }
        }

        public async Task<TRemoteInterface> Connect<TRemoteInterface>(string sturdyRef) where TRemoteInterface : class, IDisposable
        {
            // We assume that a sturdy ref url looks always like
            // capnp://vat-id_base64-curve25519-public-key@host:port/sturdy-ref-token
            if (!sturdyRef.StartsWith("capnp://")) return null;
            var vatIdBase64Url = "";
            var addressPort = "";
            var port = 0;
            var srToken = "";
            var host = ""; // Hostname to use for TLS/SNI

            var rest = sturdyRef[8..];
            // is Unix domain socket
            if (rest.StartsWith("/")) rest = rest[1..];
            else
            {
                var vatIdAndRest = rest.Split("@");
                if (vatIdAndRest.Length > 0) vatIdBase64Url = vatIdAndRest[0];
                if (vatIdAndRest[^1].Contains('/'))
                {
                    var addressPortAndRest = vatIdAndRest[^1].Split("/");
                    if (addressPortAndRest.Length > 0)
                    {
                        var addressPortRaw = addressPortAndRest[0];

                        // capture hostname for TLS BEFORE any replacement
                        var rawHostPort = addressPortRaw.Split(":");
                        if (rawHostPort.Length > 0) host = rawHostPort[0];
                        if (rawHostPort.Length > 1) port = Int32.Parse(rawHostPort[1]);
                    }
                    if (addressPortAndRest.Length > 1) srToken = addressPortAndRest[1];
                }
            }

            if (string.IsNullOrWhiteSpace(host)) return null;

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
                    {
                        con = await TryTlsConnectAsync(host, connectHost, port);
                    }

                    if (con == null)
                    {
                        // Fallback or direct: Plain TCP (preserve original caching behavior)
                        con = NoConnectionCaching ? new TcpRpcClient() : _connections.GetOrAdd(addressPort, new TcpRpcClient());
                        con.Connect(connectHost, port);
                        if (con.WhenConnected == null) return null;
                        await con.WhenConnected;
                    }

                    Console.WriteLine($"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} connected");
                    Console.WriteLine($"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} trying to restore srToken: {srToken}");
                    if (!string.IsNullOrEmpty(srToken))
                    {
                        var restorer = con.GetMain<Schema.Persistence.IRestorer>();
                        //var srTokenArr = Convert.FromBase64String(Restorer.FromBase64Url(srToken));
                        //var srToken = System.Text.Encoding.UTF8.GetString(srTokenArr);
                        using var cts = new CancellationTokenSource();
                        cts.CancelAfter((4 - retryCount) * 1000);
                        var cap = await restorer.Restore(new Schema.Persistence.Restorer.RestoreParams
                        {
                            LocalRef = new Schema.Persistence.SturdyRef.Token { Text = srToken }
                        }, cts.Token);
                        Console.WriteLine(
                            $"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} received restorer cap");
                        var cast_cap = cap.Cast<TRemoteInterface>(true);
                        Console.WriteLine(
                            $"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} casted cap to requested interface");
                        return cast_cap;
                    }

                    var bootstrap = con.GetMain<TRemoteInterface>();
                    Console.WriteLine(
                        $"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} returning bootstrap cap");
                    return bootstrap;
                }
                catch (ArgumentOutOfRangeException aoore)
                {
                    Console.WriteLine(
                        $"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} ArgumentOutOfRangeException: {aoore.Message}");
                    _connections.TryRemove(addressPort, out _);
                }
                catch (Capnp.Rpc.RpcException rpce)
                {
                    Console.WriteLine(
                        $"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} RpcException: {rpce.Message}");
                    _connections.TryRemove(addressPort, out _);
                }
                catch (System.Exception e)
                {
                    Console.WriteLine(
                        $"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} System.Exception: {e.Message}");
                    _connections.TryRemove(addressPort, out _);
                    throw;
                }
                retryCount--;
                Console.WriteLine(
                    $"ConnectionManager: ThreadId: {Thread.CurrentThread.ManagedThreadId} retrying to connect for {retryCount} more times");
            }
            return null;
        }

        // Resolves the hostname to a single IP (prefers IPv4), falling back to the original host on failure.
        private async Task<string> ResolveConnectHostAsync(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return host;

            string connectHost = host;
            if (!IPAddress.TryParse(connectHost, out _))
            {
                try
                {
                    var ips = await Dns.GetHostAddressesAsync(connectHost);
                    var ipv4 = Array.Find(ips, ip => ip.AddressFamily == AddressFamily.InterNetwork);
                    connectHost = (ipv4 ?? ips[0]).ToString();
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine($"ConnectionManager: DNS resolve failed for '{connectHost}': {ex.Message}. Using hostname directly.");
                    connectHost = host;
                }
            }
            return connectHost;
        }

        // Attempt a TLS connection; returns a connected client on success, null to fallback, rethrows on unknown errors.
        private async Task<TcpRpcClient> TryTlsConnectAsync(string sniHost, string connectHost, int port)
        {
            var tlsCon = new TcpRpcClient();
            try
            {
                tlsCon.InjectMidlayer(inner =>
                {
                    var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
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
                Console.WriteLine($"TLS not supported or failed auth: {aex.Message}. Falling back to plain TCP.");
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
}
