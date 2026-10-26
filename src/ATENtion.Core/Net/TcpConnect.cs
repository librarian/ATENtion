using System;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace ATENtion.Core.Net
{
    /// <summary>Opens a TCP connection with a timeout, reporting the real cause when it fails.</summary>
    internal static class TcpConnect
    {
        /// <summary>Connects, or throws the underlying socket error or a <see cref="TimeoutException"/>.</summary>
        /// <remarks>
        /// Waiting on ConnectAsync wraps a refusal or a DNS failure in an AggregateException, whose
        /// message on .NET Framework is only "One or more errors occurred". The inner exception is
        /// rethrown instead, and the client is closed on every failure path rather than leaked.
        /// </remarks>
        internal static TcpClient Open(string host, int port, int timeoutMs, Action<TcpClient> configure = null)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                configure?.Invoke(tcp);
                bool connected;
                try { connected = tcp.ConnectAsync(host, port).Wait(timeoutMs); }
                catch (AggregateException ex) when (ex.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;   // unreachable
                }
                if (!connected)
                    throw new TimeoutException($"Timed out connecting to {host}:{port}.");
                return tcp;
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }
    }
}
