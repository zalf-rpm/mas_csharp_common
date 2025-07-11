﻿using Capnp.Rpc;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Mas.Infrastructure.Common
{
    public class ConnectionManager : IDisposable
    {
        private ConcurrentDictionary<string, TcpRpcClient> _connections = new();
        private TcpRpcServer _server;

        public Restorer Restorer { get; set; }

        public ushort Port => (ushort)_server.Port;

        public ConnectionManager(Restorer restorer = null) 
        {
            Restorer = restorer;
            //Console.WriteLine("ConnectionManager created");
        }

        public void Dispose() => Dispose(true);

        public static string GetLocalIPAddress(string connectToHost = "dns.google", int connectToPort = 443)
        {
            var localIP = "127.0.0.1";
            try
            {
                using Socket socket = new (AddressFamily.InterNetwork, SocketType.Stream, 0);
                socket.Connect(connectToHost, connectToPort);
                var endPoint = socket.LocalEndPoint as IPEndPoint;
                localIP = endPoint.Address.ToString();
            } catch(System.Exception e) { 
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
                catch(System.Exception e)
                {
                    Console.WriteLine("Exception thrown while disposing connection (TcpRpcClient): " + key + " Exception: " + e.Message);
                }
            }
            
            try
            {
                //Console.WriteLine("ConnectionManager: Disposing Capnp.Rpc.TcpRpcServer");
                _server?.Dispose();
            }
            catch(System.Exception e)
            {
                Console.WriteLine("Exception thrown while disposing TcpRpcServer. Exception: " + e.Message);
            }
        }


        public async Task<TRemoteInterface> Connect<TRemoteInterface>(string sturdyRef) where TRemoteInterface : class, IDisposable
        {
            // we assume that a sturdy ref url looks always like 
            // capnp://vat-id_base64-curve25519-public-key@host:port/sturdy-ref-token
            if (!sturdyRef.StartsWith("capnp://")) return null;
            var vatIdBase64Url = "";
            var addressPort = "";
            var address = "";
            var port = 0;
            var srToken = "";

            var rest = sturdyRef[8..];
            // is unix domain socket
            if (rest.StartsWith("/")) rest = rest[1..];  
            else {
                var vatIdAndRest = rest.Split("@");
                if (vatIdAndRest.Length > 0) vatIdBase64Url = vatIdAndRest[0];
                if (vatIdAndRest[^1].Contains('/')) {
                    var addressPortAndRest = vatIdAndRest[^1].Split("/");
                    if (addressPortAndRest.Length > 0) {
                        addressPort = addressPortAndRest[0];
                        addressPort = addressPort.Replace("localhost", "127.0.0.1");
                        var addressAndPort = addressPort.Split(":");
                        if (addressAndPort.Length > 0) address = addressAndPort[0];
                        if (addressAndPort.Length > 1) port = Int32.Parse(addressAndPort[1]);
                    }
                    if (addressPortAndRest.Length > 1) srToken = addressPortAndRest[1];
                }
            }

            if (addressPort.Length <= 0) return null;
            try {
                //Console.WriteLine("ConnectionManager: ThreadId: " + Thread.CurrentThread.ManagedThreadId);
                //var con = new TcpRpcClient(address, port);
                var con = _connections.GetOrAdd(addressPort, new TcpRpcClient(address, port));
                await con.WhenConnected;
                if (!string.IsNullOrEmpty(srToken)) {
                    var restorer = con.GetMain<Schema.Persistence.IRestorer>();
                    //var srTokenArr = Convert.FromBase64String(Restorer.FromBase64Url(srToken));
                    //var srToken = System.Text.Encoding.UTF8.GetString(srTokenArr);
                    var cap = await restorer.Restore(new Schema.Persistence.Restorer.RestoreParams { 
                        LocalRef=new Schema.Persistence.SturdyRef.Token { Text = srToken }});
                    return cap.Cast<TRemoteInterface>(true);
                } else {
                    var bootstrap = con.GetMain<TRemoteInterface>();
                    //Console.WriteLine("ConnectionManager: current TaskSchedulerId: " + TaskScheduler.Current.Id);
                    return bootstrap;
                }
            }
            catch(ArgumentOutOfRangeException) {
                _connections.TryRemove(addressPort, out _);
            }
            catch(System.Exception e) {
                _connections.TryRemove(addressPort, out _);
                throw;
            }
            return null;
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
