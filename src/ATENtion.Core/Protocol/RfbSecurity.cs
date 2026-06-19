using System;
using ATENtion.Core.Net;

namespace ATENtion.Core.Protocol
{
    /// <summary>
    /// Performs the RFB security-type negotiation: reads the list the server offers and replies
    /// with the one type the client will use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Carries out the second step of the handshake, returning the chosen security
    /// type so the authenticator knows which exchange to run.
    /// </para>
    /// <para>
    /// OPERATION - The server sends a count followed by that many type bytes. The native client
    /// keeps the LAST type in the list rather than the first, and this preserves that behaviour:
    /// the loop overwrites the choice on each iteration, so the value that survives is the final
    /// one offered. A count of zero is not an empty menu but a refusal - the server is about to
    /// send a failure reason - so it is reported as a protocol error.
    /// </para>
    /// <para>
    /// WIRE FORMAT -
    /// <code>
    ///   server -> client : [count : u8][type : u8] x count
    ///   client -> server : [chosen : u8]
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Reads and writes through a <see cref="BufferedRfbStream"/>.
    /// </para>
    /// <para>
    /// PROVENANCE - Native security negotiation iKVM64.dll FUN_180012430.
    /// VERIFIED LIVE: the target offers security type 16 and the client selects it.
    /// </para>
    /// </remarks>
    public static class RfbSecurity
    {
        /// <summary>Reads the offered security types and writes back the chosen one.</summary>
        /// <param name="stream">The handshake stream, positioned at the security count byte.</param>
        /// <returns>The security type the client selected (the last one offered).</returns>
        /// <exception cref="RfbProtocolException">The server offered no security types, which in
        /// RFB signals a refused connection.</exception>
        public static byte Negotiate(BufferedRfbStream stream)
        {
            byte count = stream.ReadU8();
            Diagnostics.KvmLog.Write($"Security: server offered {count} type(s).");
            if (count == 0)
            {
                // A count of zero means the server is about to send a failure reason rather than a
                // menu of types. Surface it as a refused connection.
                throw new RfbProtocolException("Server offered no security types (connection refused).");
            }

            byte chosen = 0;
            var offered = new byte[count];
            for (int i = 0; i < count; i++)
            {
                offered[i] = stream.ReadU8();
                chosen = offered[i]; // keep the last type, matching the native selection loop
            }
            Diagnostics.KvmLog.Write("Security: offered types = " + Diagnostics.KvmLog.Hex(offered) + $"; choosing {chosen}.");

            stream.WriteU8(chosen);
            stream.Flush();
            return chosen;
        }
    }

    /// <summary>Raised when the peer violates the RFB handshake or message protocol.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Marks a failure that is the protocol's fault rather than the local code's: a
    /// refused connection, an unknown message type, or a malformed field.
    /// </para>
    /// </remarks>
    public sealed class RfbProtocolException : Exception
    {
        /// <summary>Creates the exception with a description of the violation.</summary>
        /// <param name="message">What was malformed or unexpected.</param>
        public RfbProtocolException(string message) : base(message) { }
    }
}
