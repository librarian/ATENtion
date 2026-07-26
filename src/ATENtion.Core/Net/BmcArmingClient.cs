using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ATENtion.Core.Diagnostics;

namespace ATENtion.Core.Net
{
    /// <summary>The parsed outcome of arming a KVM session through the BMC web API.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Holds the per-session values the JNLP arming response carries: the credentials, the
    /// connection ports, the company and board identifiers, the TLS flag, and the server
    /// certificate, plus the derived preferred port and TLS decision.
    /// </para>
    /// <para>
    /// OPERATION - The fields map one-to-one onto the JNLP &lt;argument&gt; positions. The original
    /// viewer connects, when stunnel TLS is enabled, to the server TLS port (argument 9); otherwise
    /// it uses the plaintext iKVM port (argument 4). <see cref="PreferredPort"/> and
    /// <see cref="UseTls"/> reproduce that choice.
    /// </para>
    /// <para>
    /// PROVENANCE - Argument semantics recovered from the ATEN viewer's KVMMain constructor (javap of
    /// iKVM__V1.69.39.0x0.jar); the positional layout is argument 0 = server IP, 1 = user token,
    /// 2 = password token, 3 = host name, 4 = iKVM/stunnel-local port,
    /// 5 = virtual-media/stunnel-local port, 6 = company id, 7 = board id,
    /// 8 = stunnel-enable, 9 = server TLS KVM port, 10 = server TLS virtual-media port,
    /// and 11 = virtual-media enabled.
    /// </para>
    /// </remarks>
    public sealed class ArmingResult
    {
        /// <summary>Argument 1: the temporary username sent in the first ATEN credential field.</summary>
        public string KvmUsername { get; set; }
        /// <summary>Argument 2: the temporary password sent in the second ATEN credential field.</summary>
        public string KvmPassword { get; set; }
        /// <summary>
        /// Legacy single-token view. Setting it applies the same value to both credential fields;
        /// reading it returns the temporary username.
        /// </summary>
        public string Token
        {
            get => KvmUsername;
            set { KvmUsername = value; KvmPassword = value; }
        }
        /// <summary>Argument 3: the session display name.</summary>
        public string HostName { get; set; }
        /// <summary>
        /// Argument 4: the iKVM port. In the original viewer this is the stunnel local accept port,
        /// replaced at runtime by a free port; with stunnel off it is the plaintext RFB port.
        /// </summary>
        public int KvmPort { get; set; }
        /// <summary>Argument 9: the server-side mutual-TLS RFB port (stunnel's connect target, e.g. 5900).</summary>
        public int VncPort { get; set; }
        /// <summary>Argument 8: stunnel-enable. 1 means the original wraps the link in TLS to <see cref="VncPort"/>.</summary>
        public int StunEnable { get; set; }
        /// <summary>Argument 5: stunnel's local plaintext accept port for virtual media.</summary>
        public int VirtualMediaLocalPort { get; set; }
        /// <summary>Argument 10: the server-side virtual-media TLS port (normally 623).</summary>
        public int VirtualMediaPort { get; set; }
        /// <summary>Argument 11: 1 when the BMC exposes virtual media for this session.</summary>
        public int VirtualMediaEnabled { get; set; }
        /// <summary>Argument 6: the company identifier.</summary>
        public int CompanyId { get; set; }
        /// <summary>Argument 7: the board identifier.</summary>
        public int BoardId { get; set; }
        /// <summary>The BMC's server certificate in PEM form, when the JNLP embeds one.</summary>
        public string ServerCertificatePem { get; set; }
        /// <summary>True when the JNLP requested the BlowFish cipher.</summary>
        public bool BlowFish { get; set; }
        /// <summary>The raw JNLP text, retained for diagnostics.</summary>
        public string RawJnlp { get; set; }
        /// <summary>The full ordered list of JNLP arguments, retained for diagnostics.</summary>
        public System.Collections.Generic.List<string> Arguments { get; set; }

        /// <summary>
        /// The port to connect to, mirroring the original: the server TLS port (argument 9) when
        /// stunnel TLS is enabled, otherwise the plaintext iKVM port (argument 4), falling back to
        /// whichever is present.
        /// </summary>
        public int PreferredPort =>
            (StunEnable == 1 && VncPort > 0) ? VncPort : (KvmPort > 0 ? KvmPort : VncPort);

        /// <summary>True when the original would tunnel via TLS (stunnel-enable = 1, the X9/ATEN default).</summary>
        public bool UseTls => StunEnable == 1;
    }

    /// <summary>
    /// Reproduces the Supermicro web-API session "arming" flow, which logs in to the BMC and
    /// requests the launch JNLP so the BMC opens its KVM data port and mints a fresh token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Performs the same sequence IPMIView's KVMSessionConnection does: a form login that
    /// yields a session cookie, an optional switch of the KVM interface to the Java plug-in, and a
    /// request for the iKVM launch JNLP. The JNLP it returns carries the per-session token, the
    /// connection ports, and the server certificate, and the request itself arms the KVM port.
    /// </para>
    /// <para>
    /// OPERATION - Login posts the credentials to /cgi/login.cgi, which sets a session cookie. When
    /// the BMC is in HTML5 mode the Java/RFB port is never opened, so the interface is first switched
    /// to the Java plug-in by scraping the CSRF token from the top menu and posting /cgi/op.cgi. That
    /// step is best-effort, because some firmware needs no switch. The JNLP is then fetched from
    /// /cgi/url_redirect.cgi and parsed for its arguments and certificate.
    /// </para>
    /// <para>
    /// WIRE FORMAT - The web exchange:
    /// <code>
    ///   POST /cgi/login.cgi          name=&lt;user&gt;&amp;pwd=&lt;pass&gt;   (form-urlencoded) -> SID cookie
    ///   GET  /cgi/url_redirect.cgi?url_name=ikvm&amp;url_type=jwsk         -> launch JNLP (arms the KVM port)
    /// </code>
    /// </para>
    /// <para>
    /// DEPENDENCIES - Uses HttpWebRequest with a shared cookie container. It trusts the BMC's
    /// self-signed web certificate through <see cref="RemoteCertificateValidationGuard"/>.
    /// </para>
    /// <para>
    /// RESTRICTIONS - Two cases are not yet handled: the Redfish session fallback for firmware that
    /// rejects login.cgi with HTTP 400, and any firmware whose CSRF or interface-switch flow differs
    /// from the one scraped here. Both are noted.
    /// </para>
    /// <para>
    /// PROVENANCE - Reversed from IPMIView20.jar KVMSessionConnection. Argument layout from the ATEN
    /// viewer's KVMMain. VERIFIED LIVE: login, the JNLP fetch, and token parsing
    /// succeed against the target.
    /// </para>
    /// </remarks>
    public sealed class BmcArmingClient
    {
        private const string UserAgent = "Mozilla/5.0";

        /// <summary>Logs in, optionally switches the interface, fetches the JNLP, and parses it.</summary>
        /// <param name="host">The BMC host name or address.</param>
        /// <param name="username">The BMC web user name.</param>
        /// <param name="password">The BMC web password.</param>
        /// <param name="useHttps">True to use HTTPS (the default), false for plain HTTP.</param>
        /// <param name="webPort">The web port, or 0 for the scheme default (443 or 80).</param>
        /// <returns>The parsed arming result, including the session token and ports.</returns>
        /// <exception cref="RfbProtocolExceptionShim">login.cgi returned HTTP 400, which indicates a
        /// firmware that needs the Redfish session path.</exception>
        public ArmingResult Arm(string host, string username, string password, bool useHttps = true, int webPort = 0)
        {
            string scheme = useHttps ? "https" : "http";
            int port = webPort != 0 ? webPort : (useHttps ? 443 : 80);
            string baseUrl = $"{scheme}://{host}:{port}";
            var cookies = new CookieContainer();

            // Trust the BMC's self-signed web certificate (the equivalent of IPMIView's TrustAllManager).
            RemoteCertificateValidationGuard.Enable();

            KvmLog.Write($"Arming: POST {baseUrl}/cgi/login.cgi ...");
            string loginBody = "name=" + Uri.EscapeDataString(username) + "&pwd=" + Uri.EscapeDataString(password);
            int loginStatus = Post(baseUrl + "/cgi/login.cgi", loginBody, cookies, baseUrl, out _);
            bool hasSessionCookie = !string.IsNullOrEmpty(cookies.GetCookieHeader(new Uri(baseUrl)));
            KvmLog.Write($"Arming: login.cgi HTTP {loginStatus}; session cookie received={hasSessionCookie}.");
            if (loginStatus == 400)
                throw new RfbProtocolExceptionShim("login.cgi returned 400 - this firmware likely needs the Redfish session path.");

            // Ensure the KVM interface is the Java plug-in (0), not HTML5 (1). In HTML5 mode the
            // Java/RFB port is never opened, so the jwsk request alone does not arm it.
            TrySwitchToJavaInterface(baseUrl, cookies);

            KvmLog.Write("Arming: GET /cgi/url_redirect.cgi?url_name=ikvm&url_type=jwsk ...");
            string jnlp = Get(baseUrl + "/cgi/url_redirect.cgi?url_name=ikvm&url_type=jwsk", cookies, baseUrl);
            KvmLog.Write($"Arming: jwsk response {jnlp?.Length ?? 0} bytes.");

            var result = ParseJnlp(jnlp);
            if (result.Arguments != null)
                for (int i = 0; i < result.Arguments.Count; i++)
                {
                    // Arguments 1 and 2 are temporary credentials. Never place them in a log file.
                    string value = (i == 1 || i == 2)
                        ? $"<redacted; length {result.Arguments[i]?.Length ?? 0}>"
                        : result.Arguments[i];
                    KvmLog.Write($"Arming: JNLP arg[{i}] = {value}");
                }
            KvmLog.Write($"Arming: parsed credentials (lengths {result.KvmUsername?.Length ?? 0}/" +
                         $"{result.KvmPassword?.Length ?? 0}), host '{result.HostName}', " +
                         $"iKVM/stunnel port {result.KvmPort}, server TLS port {result.VncPort}, stunEnable {result.StunEnable}, " +
                         $"vmedia local/server {result.VirtualMediaLocalPort}/{result.VirtualMediaPort}, " +
                         $"vmedia enabled {result.VirtualMediaEnabled}, company {result.CompanyId}, board {result.BoardId} -> " +
                         $"connect {result.PreferredPort} TLS={result.UseTls}, blowfish {result.BlowFish}, " +
                         $"server cert {(result.ServerCertificatePem != null ? "present" : "absent")}.");
            return result;
        }

        // Scrapes the CSRF token from the top menu and posts op.cgi to set ikvm_setting=0 (the Java
        // plug-in). Best-effort: failures are logged and ignored, since some firmware needs no switch.
        private void TrySwitchToJavaInterface(string baseUrl, CookieContainer cookies)
        {
            try
            {
                string topmenu = Get(baseUrl + "/cgi/url_redirect.cgi?url_name=topmenu", cookies, baseUrl);
                LogCsrfContext(topmenu);
                string csrf = ExtractCsrfToken(topmenu);
                if (string.IsNullOrEmpty(csrf))
                {
                    KvmLog.Write("Arming: no CSRF token found on topmenu; skipping interface switch.");
                    return;
                }
                KvmLog.Write($"Arming: got CSRF token (len {csrf.Length}); POST /cgi/op.cgi op=remote_console&ikvm_setting=0 (JavaPlugIn)...");
                int status = Post(baseUrl + "/cgi/op.cgi", "op=remote_console&ikvm_setting=0", cookies, baseUrl, out string resp, csrf);
                KvmLog.Write($"Arming: op.cgi HTTP {status}, result: {(resp ?? "").Trim()}");
            }
            catch (Exception ex) { KvmLog.Error("interface switch (continuing)", ex); }
        }

        // Logs only that a marker exists. The surrounding text contains the live token and must
        // never be copied into a diagnostic log.
        private static void LogCsrfContext(string page)
        {
            if (string.IsNullOrEmpty(page)) { KvmLog.Write("Arming: topmenu empty."); return; }
            int i = page.IndexOf("CSRF", StringComparison.Ordinal);
            if (i < 0) { KvmLog.Write($"Arming: topmenu has no 'CSRF' (len {page.Length})."); return; }
            KvmLog.Write($"Arming: topmenu contains a CSRF marker (page length {page.Length}); token value redacted.");
        }

        /// <summary>Extracts the CSRF token value from a BMC page, or null when none is present.</summary>
        /// <param name="page">The page HTML to scan.</param>
        /// <returns>The token, or null.</returns>
        internal static string ExtractCsrfToken(string page)
        {
            if (string.IsNullOrEmpty(page)) return null;
            // Firmware emits SmcCsrfInsert("CSRF_TOKEN", "value"). Tokens may contain Base64
            // punctuation such as '/', '+', and '=', so capture everything up to the matching quote.
            var m = Regex.Match(page,
                "SmcCsrfInsert\\s*\\(\\s*[\"']CSRF_TOKEN[\"']\\s*,\\s*[\"']([^\"']+)[\"']",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Parses a launch JNLP into an <see cref="ArmingResult"/> by argument position.</summary>
        /// <param name="jnlp">The JNLP text.</param>
        /// <returns>The parsed result; empty fields when the JNLP is missing or short.</returns>
        public static ArmingResult ParseJnlp(string jnlp)
        {
            var r = new ArmingResult { RawJnlp = jnlp };
            if (string.IsNullOrEmpty(jnlp)) return r;

            // The <argument> tags, in order, the same positional layout as launch.jnlp.
            var args = new System.Collections.Generic.List<string>();
            foreach (Match m in Regex.Matches(jnlp, "<argument>(.*?)</argument>", RegexOptions.Singleline))
                args.Add(m.Groups[1].Value.Trim());

            // Positional layout, faithful to the ATEN viewer's KVMMain:
            //   0 = serverIp, 1 = user (token), 2 = password (token), 3 = hostName,
            //   4 = iKVM/stunnel-local port, 5 = vmedia/stunnel-local port, 6 = companyId,
            //   7 = boardId, 8 = stunEnable, 9 = server TLS KVM port (5900),
            //   10 = server TLS virtual-media port (623), 11 = virtual-media enabled.
            r.Arguments = args;
            int Int(int i) => (i < args.Count && int.TryParse(args[i], out int v)) ? v : 0;
            if (args.Count > 1) r.KvmUsername = args[1];
            if (args.Count > 2) r.KvmPassword = args[2];
            if (args.Count > 3) r.HostName = args[3];
            r.KvmPort = Int(4);
            r.VirtualMediaLocalPort = Int(5);
            r.CompanyId = Int(6);
            r.BoardId = Int(7);
            r.StunEnable = Int(8);
            r.VncPort = Int(9);
            r.VirtualMediaPort = Int(10);
            r.VirtualMediaEnabled = Int(11);

            // The embedded server certificate (BEGIN/END CERTIFICATE), when present.
            var certMatch = Regex.Match(jnlp, "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", RegexOptions.Singleline);
            if (certMatch.Success) r.ServerCertificatePem = certMatch.Value;

            return r;
        }

        // Issues a form POST, optionally with a CSRF header, and returns the HTTP status.
        private static int Post(string url, string body, CookieContainer cookies, string referer, out string response, string csrfToken = null)
        {
            var req = NewRequest(url, cookies, referer);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            if (!string.IsNullOrEmpty(csrfToken)) req.Headers["CSRF_TOKEN"] = csrfToken;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            return ReadResponse(req, out response);
        }

        // Issues a GET and returns the response body.
        private static string Get(string url, CookieContainer cookies, string referer)
        {
            var req = NewRequest(url, cookies, referer);
            req.Method = "GET";
            ReadResponse(req, out string response);
            return response;
        }

        // Builds a request that carries the shared cookie container and the browser-like headers the BMC expects.
        private static HttpWebRequest NewRequest(string url, CookieContainer cookies, string referer)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.CookieContainer = cookies;
            req.UserAgent = UserAgent;
            req.Accept = "text/html,application/xhtml+xml,application/xml";
            req.Referer = referer;
            req.AllowAutoRedirect = true;
            req.Timeout = 15000;
            req.ServicePoint.Expect100Continue = false;
            return req;
        }

        // Reads the response body and status, treating an HTTP error response as a normal status to report.
        private static int ReadResponse(HttpWebRequest req, out string body)
        {
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    body = sr.ReadToEnd();
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr)
            {
                using (var sr = new StreamReader(hr.GetResponseStream())) body = sr.ReadToEnd();
                return (int)hr.StatusCode;
            }
        }
    }

    /// <summary>An arming-failure exception kept local to avoid a dependency cycle on the Protocol layer.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Reports a web-arming failure (such as a firmware that needs the Redfish path)
    /// without referencing the Protocol assembly's RfbProtocolException.
    /// </para>
    /// </remarks>
    public sealed class RfbProtocolExceptionShim : Exception
    {
        /// <summary>Creates the exception with a description of the arming failure.</summary>
        /// <param name="message">What went wrong.</param>
        public RfbProtocolExceptionShim(string message) : base(message) { }
    }

    /// <summary>Installs a process-wide trust-all policy for the BMC's self-signed web certificate.</summary>
    /// <remarks>
    /// <para>
    /// FUNCTION - Enables, once, a server-certificate validation callback that accepts the BMC's
    /// self-signed certificate, matching IPMIView's TrustAllManager, and pins the protocol to
    /// TLS 1.2.
    /// </para>
    /// <para>
    /// RESTRICTIONS - The callback is process-wide and accepts any certificate. It is appropriate
    /// only for reaching a BMC whose trust is established out of band.
    /// </para>
    /// </remarks>
    internal static class RemoteCertificateValidationGuard
    {
        private static bool _enabled;

        /// <summary>Installs the trust-all callback the first time it is called; later calls do nothing.</summary>
        public static void Enable()
        {
            if (_enabled) return;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback += (s, cert, chain, errors) => true;
            _enabled = true;
        }
    }
}
