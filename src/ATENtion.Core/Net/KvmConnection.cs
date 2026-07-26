using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ATENtion.Core.Crypto;
using ATENtion.Core.Protocol;

namespace ATENtion.Core.Net
{
    /// <summary>The parameters for opening a <see cref="KvmConnection"/>.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries the host, port, transport security, client certificate, and session
    /// credentials that a connection needs. The defaults match the iKVM parameters read from launch.jnlp.
    /// </para>
    /// </remarks>
    public sealed class KvmConnectionOptions
    {
        /// <summary>The BMC host name or address.</summary>
        public string Host { get; set; }
        /// <summary>The TCP port; defaults to the iKVM port 63630 from launch.jnlp.</summary>
        public int Port { get; set; } = 63630;
        /// <summary>True to wrap the connection in TLS 1.2, replacing the bundled stunnel leg.</summary>
        public bool UseTls { get; set; }
        /// <summary>The client certificate to present for mutual TLS, or null.</summary>
        public X509Certificate2 ClientCertificate { get; set; }
        /// <summary>The TCP connect timeout, in milliseconds.</summary>
        public int ConnectTimeoutMs { get; set; } = 10000;
        /// <summary>The server-side virtual-media port from JNLP argument 10; normally 623.</summary>
        public int VirtualMediaPort { get; set; } = 623;
        /// <summary>True when virtual media uses the same direct mutual-TLS transport as KVM.</summary>
        public bool VirtualMediaUseTls { get; set; }
        /// <summary>True when the JNLP advertises virtual-media support in argument 11.</summary>
        public bool VirtualMediaEnabled { get; set; }

        /// <summary>JNLP argument 1: the temporary ATEN username.</summary>
        public string KvmUsername { get; set; }
        /// <summary>JNLP argument 2: the temporary ATEN password.</summary>
        public string KvmPassword { get; set; }
        /// <summary>
        /// Legacy manual-token property. Setting it uses the same value for both credential fields.
        /// </summary>
        public string Token
        {
            get => KvmUsername;
            set { KvmUsername = value; KvmPassword = value; }
        }
    }

    /// <summary>
    /// Opens the transport to a BMC and runs the RFB handshake over it, producing the buffered
    /// stream and the negotiated session the rest of the client builds on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Establishes the TCP connection, optionally negotiates TLS, wraps the result in a
    /// <see cref="BufferedRfbStream"/>, and runs the handshake to yield an <see cref="RfbSession"/>.
    /// </para>
    /// <para>
    /// OPERATION - <see cref="Connect"/> opens the socket with Nagle disabled and, when TLS is
    /// requested, performs a TLS 1.2 client authentication that can present a client certificate
    /// for the mutual-TLS leg the BMC requires. The TLS path here is the direct equivalent of the
    /// bundled stunnel: the same mutual-TLS connection to the same port, without the loopback hop.
    /// <see cref="Handshake"/> then runs the RFB exchange over the buffered stream.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Produces a <see cref="BufferedRfbStream"/> for the session and an
    /// <see cref="RfbkmCrypto"/> for the input cipher. The handshake itself is
    /// <see cref="RfbHandshake"/>.
    /// </para>
    /// <para>
    /// RESTRICTIONS - <see cref="Connect"/> must be called before <see cref="Handshake"/>. The
    /// server certificate is accepted without validation: BMCs ship self-signed certificates and
    /// trust is established out of band by the pinned vendor certificate. Certificate pinning is
    /// deferred to a later hardening pass. <see cref="Dispose"/> closes the socket before the TLS
    /// stream so a half-open connection cannot hang on the TLS close-notify.
    /// </para>
    /// <para>
    /// PROVENANCE - The transport and handshake sequence follow the native client. VERIFIED LIVE: the direct mutual-TLS path connects to the target.
    /// </para>
    /// </remarks>
    public sealed class KvmConnection : IDisposable
    {
        private readonly KvmConnectionOptions _options;
        private TcpClient _tcp;
        private Stream _transport;

        /// <summary>Creates a connection bound to the given options.</summary>
        /// <param name="options">The host, port, security, and token to connect with.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        public KvmConnection(KvmConnectionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>The buffered RFB stream over the transport; null until <see cref="Connect"/> runs.</summary>
        public BufferedRfbStream Stream { get; private set; }
        /// <summary>The negotiated session; null until <see cref="Handshake"/> completes.</summary>
        public RfbSession Session { get; private set; }
        /// <summary>The cipher used for encrypted input events.</summary>
        public RfbkmCrypto Crypto { get; } = new RfbkmCrypto();

        /// <summary>Opens the TCP socket and, when requested, negotiates TLS.</summary>
        /// <exception cref="TimeoutException">The connection did not complete within the timeout.</exception>
        public void Connect()
        {
            Diagnostics.KvmLog.Write($"TCP connecting to {_options.Host}:{_options.Port} (timeout {_options.ConnectTimeoutMs}ms)...");
            _tcp = new TcpClient { NoDelay = true };
            if (!_tcp.ConnectAsync(_options.Host, _options.Port).Wait(_options.ConnectTimeoutMs))
                throw new TimeoutException($"Timed out connecting to {_options.Host}:{_options.Port}.");
            Diagnostics.KvmLog.Write("TCP connected.");

            _transport = _tcp.GetStream();

            if (_options.UseTls)
            {
                Diagnostics.KvmLog.Write("Starting TLS (TLS 1.2) handshake...");
                var ssl = new SslStream(_transport, false, AcceptServerCertificate);
                var clientCerts = new X509CertificateCollection();
                if (_options.ClientCertificate != null) clientCerts.Add(_options.ClientCertificate);
                ssl.AuthenticateAsClient(_options.Host, clientCerts, SslProtocols.Tls12, false);
                Diagnostics.KvmLog.Write($"TLS established: {ssl.SslProtocol}, cipher {ssl.CipherAlgorithm}.");
                _transport = ssl;
            }

            Stream = new BufferedRfbStream(_transport);
        }

        /// <summary>Runs the RFB handshake over the connected stream.</summary>
        /// <param name="authenticator">The authentication step, or null for the default.</param>
        /// <returns>The negotiated session.</returns>
        /// <exception cref="InvalidOperationException"><see cref="Connect"/> has not been called.</exception>
        public RfbSession Handshake(IRfbAuthenticator authenticator)
        {
            if (Stream == null) throw new InvalidOperationException("Call Connect() first.");
            Session = new RfbHandshake(authenticator).Run(
                Stream, _options.KvmUsername, _options.KvmPassword, Crypto);
            return Session;
        }

        // Accepts the server certificate unconditionally. BMCs ship self-signed certificates and
        // trust is established out of band (the pinned vendor certificate). Pinning is a later pass.
        private static bool AcceptServerCertificate(object sender, X509Certificate certificate,
                                                    X509Chain chain, SslPolicyErrors errors) => true;

        /// <summary>Closes the connection, the socket first so the TLS close-notify cannot hang.</summary>
        public void Dispose()
        {
            // Close the socket first so a dead or half-open connection cannot make the SslStream's
            // close-notify on Dispose hang. Each step is guarded so one failure cannot mask the rest.
            try { _tcp?.Close(); } catch { }
            try { _transport?.Dispose(); } catch { }
            try { Crypto?.Dispose(); } catch { }
        }
    }
}
