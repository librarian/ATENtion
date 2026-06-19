using System;
using System.Text;
using ATENtion.Core.Crypto;
using ATENtion.Core.Net;

namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// The iKVM credential exchange: reads the server challenge, sends the session token as both
    /// the username and password fields, and checks the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Implements <see cref="IRfbAuthenticator"/> for the token-challenge security
    /// type the target BMC uses. It authenticates the session using the single-use token minted
    /// when the session was armed.
    /// </para>
    /// <para>
    /// OPERATION - The server opens with a twenty-four-byte challenge, which the native client
    /// reads but is not observed to fold into the credentials. The client then writes the token
    /// twice, once as the username field and once as the password field, each twenty-four bytes,
    /// ASCII, and NUL-padded. The server replies with a SecurityResult word. Zero is success: the
    /// client writes the ClientInit shared-flag (0), and ServerInit follows. A non-zero result is
    /// a rejection, optionally followed by a length-prefixed UTF-8 reason.
    /// </para>
    /// <para>
    /// The token is sent the same way in both fields because the armed session presents identical
    /// username and password token arguments. Sending it twice reproduces that.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   read  24 bytes   server challenge (read, but not seen to transform the credentials)
    ///   write 24 bytes   username field (token, ASCII, NUL-padded)
    ///   write 24 bytes   password field (token, ASCII, NUL-padded)
    ///   read  u32 BE     SecurityResult (0 = OK)
    ///     OK   : write u8 0  (ClientInit shared-flag); ServerInit follows
    ///     fail : read u32 BE reason length, then that many UTF-8 bytes
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads and writes through a <see cref="BufferedRfbStream"/>. The
    /// <see cref="RfbkmCrypto"/> argument is unused here: the credentials are sent in the clear
    /// because the channel is already the TLS leg and the token is single-use. The AES cipher
    /// applies only to mouse events.
    /// </para>
    /// <para>
    /// PROVENANCE - Native login iKVM64.dll FUN_1800120f0 (RFBProtocol vtable+0x10), reached via
    /// RMConnection.login (FUN_180007220). VERIFIED LIVE: token auth
    /// reaches SecurityResult 0 against the target.
    /// </para>
    /// </remarks>
    public sealed class TokenChallengeAuthenticator : IRfbAuthenticator
    {
        /// <summary>The length, in bytes, of the server challenge read at the start of the exchange.</summary>
        public const int ChallengeLength = 24;
        /// <summary>The fixed length, in bytes, of each credential field written to the server.</summary>
        public const int CredentialFieldLength = 24;

        /// <summary>The twenty-four-byte challenge the server most recently sent, kept for diagnostics.</summary>
        public byte[] LastChallenge { get; private set; }

        /// <summary>Runs the token-challenge exchange against the stream.</summary>
        /// <param name="stream">The handshake stream, positioned at the server challenge.</param>
        /// <param name="securityType">The negotiated security type (logged for diagnostics).</param>
        /// <param name="token">The per-session token, sent as both credential fields. Null is treated as empty.</param>
        /// <param name="crypto">Unused by this exchange (the credentials are sent in the clear).</param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="RfbAuthException">The server returned a non-zero SecurityResult.</exception>
        public void Authenticate(BufferedRfbStream stream, byte securityType, string token, RfbkmCrypto crypto)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            token = token ?? string.Empty;

            LastChallenge = stream.ReadExact(ChallengeLength);
            Diagnostics.KvmLog.Write("Auth: 24-byte challenge = " + Diagnostics.KvmLog.Hex(LastChallenge));
            Diagnostics.KvmLog.Write($"Auth: sending credentials (token length {token.Length}, security type {securityType}).");

            stream.WriteBytes(FixedField(token, CredentialFieldLength)); // username field
            stream.WriteBytes(FixedField(token, CredentialFieldLength)); // password field (same token)
            stream.Flush();

            uint result = stream.ReadU32BE();
            Diagnostics.KvmLog.Write($"Auth: SecurityResult = {result} ({(result == 0 ? "OK" : "FAIL")}).");
            if (result != 0)
            {
                string reason = string.Empty;
                try
                {
                    uint len = stream.ReadU32BE();
                    if (len > 0 && len < 64 * 1024)
                        reason = Encoding.UTF8.GetString(stream.ReadExact((int)len));
                }
                catch { /* the failure reason is best-effort. The result code is what matters. */ }
                throw new RfbAuthException(result, reason);
            }

            // Success: the client writes the ClientInit shared-flag, which the native sets to 0.
            stream.WriteU8(0);
            stream.Flush();
        }

        // Packs a token into a fixed-length field, ASCII-encoded and NUL-padded to length. A token
        // longer than the field is truncated to fit.
        private static byte[] FixedField(string token, int length)
        {
            var field = new byte[length];
            byte[] bytes = Encoding.ASCII.GetBytes(token);
            Buffer.BlockCopy(bytes, 0, field, 0, Math.Min(bytes.Length, length));
            return field; // any remaining bytes stay zero (NUL padding)
        }
    }

    /// <summary>Raised when the server rejects the credentials with a non-zero SecurityResult.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Reports an authentication failure, carrying the server's result code and any
    /// human-readable reason it supplied.
    /// </para>
    /// </remarks>
    public sealed class RfbAuthException : Exception
    {
        /// <summary>Creates the exception from the server's result code and reason text.</summary>
        /// <param name="resultCode">The non-zero SecurityResult value.</param>
        /// <param name="reason">The server's reason text, or empty if none was sent.</param>
        public RfbAuthException(uint resultCode, string reason)
            : base($"iKVM authentication failed (result={resultCode}): {reason}")
        {
            ResultCode = resultCode;
            Reason = reason;
        }

        /// <summary>The SecurityResult value the server returned.</summary>
        public uint ResultCode { get; }
        /// <summary>The reason text the server supplied, or empty.</summary>
        public string Reason { get; }
    }
}
