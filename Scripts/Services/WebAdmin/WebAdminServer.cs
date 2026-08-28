using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web;

namespace Server.Engines.WebAdmin
{
    /// <summary>
    ///     A small HTTP service for account administration, reachable only from the local network.
    ///     It performs no authentication: the source-address check is the whole access control, so
    ///     the port must never be published to a public interface.
    /// </summary>
    public static class WebAdminServer
    {
        private static readonly TimeSpan GameThreadTimeout = TimeSpan.FromSeconds(15.0);
        private static readonly ConcurrentQueue<Action> _Pending = new ConcurrentQueue<Action>();

        private const string StaticRoot = "WebAdmin";

        private static readonly string[] _StaticFiles = { "homelab.css", "accounts.css" };

        private static HttpListener _Listener;

        public static void Initialize()
        {
            if (!Config.Get("Server.WebAdmin", true))
            {
                return;
            }

            if (!HttpListener.IsSupported)
            {
                Utility.PushColor(ConsoleColor.DarkYellow);
                Console.WriteLine("WebAdmin: HttpListener is unavailable on this runtime; account admin disabled.");
                Utility.PopColor();
                return;
            }

            var port = Config.Get("Server.WebAdminPort", 2594);

            try
            {
                _Listener = new HttpListener();
                _Listener.Prefixes.Add(String.Format("http://*:{0}/", port));
                _Listener.Start();
            }
            catch (Exception e)
            {
                Utility.PushColor(ConsoleColor.Red);
                Console.WriteLine("WebAdmin: could not bind port {0}: {1}", port, e.Message);
                Utility.PopColor();

                _Listener = null;
                return;
            }

            Core.Slice += DrainPending;

            _Listener.BeginGetContext(OnContext, null);

            Utility.PushColor(ConsoleColor.Green);
            Console.WriteLine("WebAdmin: account admin listening on port {0}, local network only.", port);
            Utility.PopColor();
        }

        #region Game thread

        /// <summary>
        ///     Runs <paramref name="action" /> on the game loop and blocks the calling HTTP thread until
        ///     it has finished. Accounts, mobiles and houses are only safe to touch from that thread.
        /// </summary>
        private static void RunOnGameThread(Action action)
        {
            // Deliberately not disposed: on a timeout the queued action still runs later and signals.
            var done = new ManualResetEventSlim(false);

            Exception error = null;

            _Pending.Enqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    done.Set();
                }
            });

            Core.Set();

            if (!done.Wait(GameThreadTimeout))
            {
                throw new TimeoutException("The server did not process the request in time.");
            }

            if (error != null)
            {
                throw error;
            }
        }

        private static void DrainPending()
        {
            Action action;

            while (_Pending.TryDequeue(out action))
            {
                action();
            }
        }

        #endregion

        #region Listener

        private static void OnContext(IAsyncResult result)
        {
            HttpListenerContext context;

            try
            {
                context = _Listener.EndGetContext(result);
            }
            catch
            {
                return;
            }

            if (_Listener.IsListening)
            {
                _Listener.BeginGetContext(OnContext, null);
            }

            try
            {
                Dispatch(context);
            }
            catch (Exception e)
            {
                TryRespond(context, 500, "text/plain; charset=utf-8", "The server failed to handle the request: " + e.Message);
            }
        }

        private static void Dispatch(HttpListenerContext context)
        {
            var request = context.Request;

            if (!IsLocalNetwork(request.RemoteEndPoint == null ? null : request.RemoteEndPoint.Address))
            {
                Utility.PushColor(ConsoleColor.DarkYellow);
                Console.WriteLine("WebAdmin: refused {0} from {1}", request.RawUrl, request.RemoteEndPoint);
                Utility.PopColor();

                Respond(context, 403, "text/plain; charset=utf-8", "This service only answers the local network.");
                return;
            }

            var path = request.Url.AbsolutePath.TrimEnd('/');

            if (path.Length == 0)
            {
                path = "/";
            }

            if (request.HttpMethod == "GET")
            {
                Get(context, path);
                return;
            }

            if (request.HttpMethod == "POST")
            {
                if (!IsSameOrigin(request))
                {
                    Respond(context, 403, "text/plain; charset=utf-8", "Cross-origin form submissions are not accepted.");
                    return;
                }

                Post(context, path);
                return;
            }

            Respond(context, 405, "text/plain; charset=utf-8", "Method not allowed.");
        }

        private static void Get(HttpListenerContext context, string path)
        {
            var query = HttpUtility.ParseQueryString(context.Request.Url.Query);

            switch (path)
            {
                case "/":
                {
                    var notice = query["msg"];
                    var failed = query["err"] == "1";

                    string page = null;
                    RunOnGameThread(() => page = AccountPage.Index(notice, failed));

                    Html(context, page);
                    return;
                }
                case "/account":
                {
                    var username = query["u"];
                    var notice = query["msg"];
                    var failed = query["err"] == "1";

                    string page = null;
                    var found = false;

                    RunOnGameThread(() =>
                    {
                        var account = AccountAdmin.Find(username);
                        found = account != null;
                        page = found ? AccountPage.Detail(account, notice, failed) : AccountPage.NotFound(username);
                    });

                    Respond(context, found ? 200 : 404, "text/html; charset=utf-8", page);
                    return;
                }
                case "/account/delete":
                {
                    var username = query["u"];

                    string page = null;
                    var found = false;

                    RunOnGameThread(() =>
                    {
                        var account = AccountAdmin.Find(username);
                        found = account != null;
                        page = found ? AccountPage.DeleteConfirm(account) : AccountPage.NotFound(username);
                    });

                    Respond(context, found ? 200 : 404, "text/html; charset=utf-8", page);
                    return;
                }
            }

            if (path.StartsWith("/") && Array.IndexOf(_StaticFiles, path.Substring(1)) >= 0)
            {
                Static(context, path.Substring(1));
                return;
            }

            Respond(context, 404, "text/plain; charset=utf-8", "Not found.");
        }

        private static void Post(HttpListenerContext context, string path)
        {
            var form = ReadForm(context.Request);
            var username = form["username"];

            switch (path)
            {
                case "/create":
                {
                    var level = ParseAccessLevel(form["level"]);

                    AdminResult created = null;
                    RunOnGameThread(() => created = AccountAdmin.Create(username, form["password"], form["confirm"], level));

                    Redirect(context, "/", created);
                    return;
                }
                case "/password":
                {
                    AdminResult changed = null;
                    RunOnGameThread(() => changed = AccountAdmin.SetPassword(username, form["password"], form["confirm"]));

                    Redirect(context, AccountUrl(username), changed);
                    return;
                }
                case "/privileges":
                {
                    var level = ParseAccessLevel(form["level"]);
                    var banned = form["banned"] == "1";

                    AdminResult updated = null;
                    RunOnGameThread(() => updated = AccountAdmin.SetPrivileges(username, level, banned, form["ips"]));

                    Redirect(context, AccountUrl(username), updated);
                    return;
                }
                case "/delete":
                {
                    AdminResult deleted = null;
                    RunOnGameThread(() => deleted = AccountAdmin.Delete(username));

                    Redirect(context, deleted.Ok ? "/" : AccountUrl(username), deleted);
                    return;
                }
            }

            Respond(context, 404, "text/plain; charset=utf-8", "Not found.");
        }

        #endregion

        #region Access control

        /// <summary>
        ///     True for loopback, RFC 1918, link-local and IPv6 unique-local addresses.
        /// </summary>
        private static bool IsLocalNetwork(IPAddress address)
        {
            if (address == null)
            {
                return false;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return bytes[0] == 10
                       || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                       || (bytes[0] == 192 && bytes[1] == 168)
                       || (bytes[0] == 169 && bytes[1] == 254);
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
            }

            return false;
        }

        /// <summary>
        ///     Without authentication, a page on another host could otherwise make a LAN browser post
        ///     here. Browsers always send Origin on cross-origin form posts.
        /// </summary>
        private static bool IsSameOrigin(HttpListenerRequest request)
        {
            var origin = request.Headers["Origin"];

            if (String.IsNullOrEmpty(origin))
            {
                return true;
            }

            Uri parsed;

            return Uri.TryCreate(origin, UriKind.Absolute, out parsed)
                   && String.Equals(parsed.Authority, request.Url.Authority, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Responses

        private static NameValueCollection ReadForm(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return HttpUtility.ParseQueryString(reader.ReadToEnd(), Encoding.UTF8);
            }
        }

        private static AccessLevel ParseAccessLevel(string text)
        {
            AccessLevel level;

            return Enum.TryParse(text, true, out level) && Enum.IsDefined(typeof(AccessLevel), level)
                       ? level
                       : AccessLevel.Player;
        }

        private static string AccountUrl(string username)
        {
            return "/account?u=" + HttpUtility.UrlEncode(username ?? String.Empty);
        }

        private static void Redirect(HttpListenerContext context, string location, AdminResult result)
        {
            var separator = location.IndexOf('?') >= 0 ? "&" : "?";

            location += separator + "msg=" + HttpUtility.UrlEncode(result.Message);

            if (!result.Ok)
            {
                location += "&err=1";
            }

            context.Response.StatusCode = 303;
            context.Response.RedirectLocation = location;
            context.Response.Close();
        }

        private static void Html(HttpListenerContext context, string body)
        {
            Respond(context, 200, "text/html; charset=utf-8", body);
        }

        private static void Static(HttpListenerContext context, string name)
        {
            var path = Path.Combine(StaticRoot, name);

            if (!File.Exists(path))
            {
                Respond(context, 404, "text/plain; charset=utf-8", "Not found.");
                return;
            }

            Respond(context, 200, "text/css; charset=utf-8", File.ReadAllText(path));
        }

        private static void Respond(HttpListenerContext context, int status, string contentType, string body)
        {
            var buffer = Encoding.UTF8.GetBytes(body);

            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();
        }

        private static void TryRespond(HttpListenerContext context, int status, string contentType, string body)
        {
            try
            {
                Respond(context, status, contentType, body);
            }
            catch
            { }
        }

        #endregion
    }
}
