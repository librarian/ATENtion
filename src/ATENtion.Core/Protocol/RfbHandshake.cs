using System;
using ATENtion.Core.Crypto;
using ATENtion.Core.Net;

namespace ATENtion.Core.Protocol
{
    /// <summary>The negotiated outcome of a completed RFB handshake.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Holds the three facts the handshake establishes and the rest of the session
    /// needs: the agreed protocol version, the chosen security type, and the ServerInit block
    /// (framebuffer geometry, pixel format, desktop name).
    /// </para>
    /// </remarks>
    public sealed class RfbSession
    {
        /// <summary>The protocol version both ends agreed on.</summary>
        public ProtocolVersion Version { get; internal set; }
        /// <summary>The security type the client selected during negotiation.</summary>
        public byte SecurityType { get; internal set; }
        /// <summary>The ServerInit block read after authentication.</summary>
        public ServerInit ServerInit { get; internal set; }
    }

    /// <summary>
    /// The authentication step of the handshake, factored out so the chosen security type's
    /// challenge/response can be supplied independently of the handshake driver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Performs whatever exchange the negotiated security type requires, between
    /// security negotiation and ServerInit. The default implementation is
    /// <see cref="TokenChallengeAuthenticator"/>.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Writes and reads through the supplied <see cref="BufferedRfbStream"/>. For
    /// security types that involve the AES key, it may also use the session token and the
    /// <see cref="RfbkmCrypto"/> instance.
    /// </para>
    /// <para>
    /// PROVENANCE - The general challenge/response shape is recovered, but the key
    /// derivation for the cipher-based types is not fully reversed. The seam isolates that gap.
    /// </para>
    /// </remarks>
    public interface IRfbAuthenticator
    {
        /// <summary>Runs the authentication exchange for the negotiated security type.</summary>
        /// <param name="stream">The handshake stream, positioned after security negotiation.</param>
        /// <param name="securityType">The security type the client selected.</param>
        /// <param name="token">The per-session token minted when the BMC armed the session.</param>
        /// <param name="crypto">The cipher used by security types that involve the AES key.</param>
        void Authenticate(BufferedRfbStream stream, byte securityType, string token, RfbkmCrypto crypto);
    }

    /// <summary>
    /// A placeholder authenticator that fails loudly, so a half-implemented authentication is
    /// never mistaken for a working connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Stands in for a security type whose login bytes are not yet reversed. It throws.
    /// Returning would let the handshake proceed on a false success.
    /// </para>
    /// <para>
    /// PROVENANCE - Retained against the security types not covered by
    /// <see cref="TokenChallengeAuthenticator"/>.
    /// </para>
    /// </remarks>
    public sealed class ProvisionalAuthenticator : IRfbAuthenticator
    {
        /// <summary>Always throws, because no real exchange is implemented for this path.</summary>
        /// <exception cref="NotImplementedException">Always, naming the unsupported security type.</exception>
        public void Authenticate(BufferedRfbStream stream, byte securityType, string token, RfbkmCrypto crypto)
        {
            throw new NotImplementedException(
                $"RFB security type {securityType} auth not yet reversed. " +
                "Supply a real IRfbAuthenticator to complete the connection.");
        }
    }

    /// <summary>
    /// Drives the RFB handshake from the opening version banner through to ServerInit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Runs the four-step opening exchange in order and returns the
    /// <see cref="RfbSession"/> describing what was negotiated.
    /// </para>
    /// <para>
    /// OPERATION - The steps run strictly in sequence, because each consumes a fixed span of the
    /// stream that the next depends on:
    /// </para>
    /// <para>
    /// 1. ProtocolVersion: read the server's version banner and echo it back.
    /// </para>
    /// <para>
    /// 2. Security: read the offered types and reply with the chosen one
    /// (<see cref="RfbSecurity"/>).
    /// </para>
    /// <para>
    /// 3. Authentication: hand off to the <see cref="IRfbAuthenticator"/>, which reads the
    /// challenge, sends credentials, reads SecurityResult, and writes ClientInit.
    /// </para>
    /// <para>
    /// 4. ServerInit: read the framebuffer geometry, pixel format, and desktop name.
    /// </para>
    /// <para>
    /// DEPENDENCIES - Operates on a <see cref="BufferedRfbStream"/>. It delegates step 3 to the
    /// authenticator supplied at construction, or to <see cref="TokenChallengeAuthenticator"/> by
    /// default.
    /// </para>
    /// <para>
    /// RESTRICTIONS - ClientInit and the precise post-authentication ordering follow standard
    /// RFB and are to be re-confirmed against the native login in a later pass.
    /// </para>
    /// <para>
    /// PROVENANCE - Native handshake driver iKVM64.dll FUN_180012030, with the login at
    /// FUN_1800120f0. VERIFIED LIVE against the target BMC.
    /// </para>
    /// </remarks>
    public sealed class RfbHandshake
    {
        private readonly IRfbAuthenticator _authenticator;

        /// <summary>Creates a handshake driver using the given authenticator.</summary>
        /// <param name="authenticator">The authentication step; null selects
        /// <see cref="TokenChallengeAuthenticator"/>.</param>
        public RfbHandshake(IRfbAuthenticator authenticator = null)
        {
            _authenticator = authenticator ?? new TokenChallengeAuthenticator();
        }

        /// <summary>Runs the full handshake and returns the negotiated session.</summary>
        /// <param name="stream">The connected RFB stream, positioned at the server's version banner.</param>
        /// <param name="token">The per-session token for the authentication step.</param>
        /// <param name="crypto">The cipher used by cipher-based security types.</param>
        /// <returns>The negotiated <see cref="RfbSession"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public RfbSession Run(BufferedRfbStream stream, string token, RfbkmCrypto crypto)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var session = new RfbSession();

            // 1. ProtocolVersion: read the server banner, echo it back.
            Diagnostics.KvmLog.Write("Handshake: reading ProtocolVersion...");
            session.Version = ProtocolVersion.Read(stream);
            Diagnostics.KvmLog.Write($"Handshake: server version RFB {session.Version.Major:000}.{session.Version.Minor:000}; echoing.");
            session.Version.Write(stream);

            // 2. Security type negotiation.
            session.SecurityType = RfbSecurity.Negotiate(stream);

            // 3. Authentication: read challenge, send credentials, read SecurityResult. On success
            //    the authenticator also writes the ClientInit shared-flag (0), matching the native
            //    login (FUN_1800120f0).
            Diagnostics.KvmLog.Write("Handshake: authenticating...");
            _authenticator.Authenticate(stream, session.SecurityType, token, crypto);

            // 4. ServerInit (framebuffer dimensions, pixel format, name).
            Diagnostics.KvmLog.Write("Handshake: reading ServerInit...");
            session.ServerInit = ServerInit.Read(stream);
            Diagnostics.KvmLog.Write($"Handshake: ServerInit {session.ServerInit.Width}x{session.ServerInit.Height}, " +
                                     $"bpp {session.ServerInit.PixelFormat.BitsPerPixel}, name '{session.ServerInit.Name}'. Handshake complete.");

            return session;
        }
    }
}
