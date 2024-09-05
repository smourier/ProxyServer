using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace ProxyServer
{
    public class Server : IDisposable
    {
        private static readonly HttpClient _client = new();
        private readonly HttpListener _listener;
        private Uri? _targetUrl;
        private int? _targetPort;
        private string? _targetHost;
        private bool _disposedValue;

        public Server(params string[] prefixes)
        {
            ArgumentNullException.ThrowIfNull(prefixes);
            if (prefixes.Length == 0)
                throw new ArgumentException(null, nameof(prefixes));

            RewriteTargetInText = true;
            RewriteHost = true;
            RewriteReferer = true;
            LogResponseBody = true;
            LogRequestBody = true;
            Prefixes = prefixes;

            _listener = new HttpListener();
            foreach (var prefix in prefixes)
            {
                _listener.Prefixes.Add(prefix);
            }
        }

        public string[] Prefixes { get; }
        public virtual bool RewriteTargetInText { get; set; }
        public virtual bool RewriteHost { get; set; }
        public virtual bool RewriteReferer { get; set; } // this can have performance impact...
        public virtual bool LogResponseBody { get; set; }
        public virtual bool LogRequestBody { get; set; }

        public Uri? TargetUrl
        {
            get => _targetUrl;
            set
            {
                if (_targetUrl == value)
                    return;

                _targetUrl = value;
                _targetHost = _targetUrl?.Host;
                _targetPort = _targetUrl?.Port;
            }
        }

        public virtual void Start()
        {
            _listener.Start();
            _listener.BeginGetContext(ProcessRequest, null);
        }

        private async void ProcessRequest(IAsyncResult result)
        {
            if (!_listener.IsListening)
                return;

            try
            {
                var ctx = _listener.EndGetContext(result);
                _listener.BeginGetContext(ProcessRequest, null);

                try
                {
                    await ProcessRequest(ctx).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    HandleError(ctx, e);
                }
            }
            catch (Exception e2)
            {
                Log("Error(" + e2.GetType().FullName + "): " + e2);
            }
        }

        protected virtual void HandleError(HttpListenerContext context, Exception error)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (error == null)
                return;

            Log("Error(" + error.GetType().FullName + "): " + error);
            try
            {
                if (error is HttpRequestException && error.InnerException is WebException we)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                    var text = we.GetAllMessagesWithDots();
                    var bytes = Encoding.UTF8.GetBytes(text);
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        protected virtual bool IsTextContent(string? contentType)
        {
            if (contentType == null)
                return false;

            return contentType.Contains("text/", StringComparison.OrdinalIgnoreCase) || contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        }

        protected virtual async Task ProcessRequest(HttpListenerContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var log = new StringBuilder();
            log.AppendLine(context.Request.HttpMethod + " " + context.Request.UserHostName + " (" + context.Request.UserHostAddress + ") " + context.Request.Url + " " + context.Request.RawUrl);
            if (TargetUrl == null)
            {
                using var os2 = context.Response.OutputStream;
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.StatusDescription = "OK";
                log.AppendLine(context.Request.HttpMethod + " " + context.Request.UserHostName + " (" + context.Request.UserHostAddress + ") " + context.Request.Url + " " + context.Request.RawUrl);
                Log(log.ToString(), string.Empty);
                return;
            }

            HttpStatusCode statusCode;
            var url = TargetUrl.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped);
            using var msg = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), url + context.Request.RawUrl);
            msg.Version = context.Request.ProtocolVersion;

            if (context.Request.HasEntityBody)
            {
                var ct2 = context.Request.ContentType;
                if (LogRequestBody && IsTextContent(ct2))
                {
                    var ms = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(ms).ConfigureAwait(false);
                    ms.Position = 0;
                    var enc = context.Request.ContentEncoding ?? Encoding.UTF8;
                    var body = enc.GetString(ms.ToArray());
                    if (LogResponseBody)
                    {
                        log.AppendLine("Request Body:");
                        log.AppendLine(body);
                        log.AppendLine();
                    }

                    ms.Position = 0;
                    msg.Content = new StreamContent(ms);
                }
                else
                {
                    msg.Content = new StreamContent(context.Request.InputStream); // disposed with msg
                }
            }

            log.AppendLine("Request Headers: ");
            string? host = null;
            foreach (string headerName in context.Request.Headers)
            {
                var headerValue = context.Request.Headers[headerName];
                if (headerName == "Content-Length" && headerValue == "0") // useless plus don't send if we have no entity body
                    continue;

                var contentHeader = false;
                switch (headerName)
                {
                    // some headers go to content...
                    case "Allow":
                    case "Content-Disposition":
                    case "Content-Encoding":
                    case "Content-Language":
                    case "Content-Length":
                    case "Content-Location":
                    case "Content-MD5":
                    case "Content-Range":
                    case "Content-Type":
                    case "Expires":
                    case "Last-Modified":
                        contentHeader = true;
                        break;

                    case "Referer":
                        if (RewriteReferer && Uri.TryCreate(headerValue, UriKind.Absolute, out var referer)) // if relative, don't handle
                        {
                            var builder = new UriBuilder(referer)
                            {
                                Host = TargetUrl.Host,
                                Port = TargetUrl.Port
                            };
                            headerValue = builder.ToString();
                        }
                        break;

                    case "Host":
                        host = headerValue;
                        if (RewriteHost)
                        {
                            headerValue = TargetUrl.Host + ":" + TargetUrl.Port;
                        }
                        break;
                }

                log.Append(' ');
                log.Append(headerName);
                log.Append(':');
                log.AppendLine(headerValue);

                if (contentHeader)
                {
                    if (msg.Content == null)
                    {
                        // huh? bogus header was sent
                    }
                    else
                    {
                        msg.Content.Headers.Add(headerName, headerValue);
                    }
                }
                else
                {
                    msg.Headers.Add(headerName, headerValue);
                }
            }
            log.AppendLine();

            using var response = await _client.SendAsync(msg).ConfigureAwait(false);
            statusCode = response.StatusCode;
            log.Append("Response: ");
            log.Append(response.StatusCode);
            log.Append(' ');
            log.AppendLine(response.ReasonPhrase);

            using var os = context.Response.OutputStream;
            context.Response.ProtocolVersion = response.Version;
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.StatusDescription = response.ReasonPhrase ?? "Unknown";

            log.AppendLine("Response Headers: ");
            foreach (var header2 in response.Headers)
            {
                log.Append(' ');
                log.Append(header2.Key);
                log.Append(':');
                log.AppendLine(string.Join(", ", header2.Value));
                context.Response.Headers.Add(header2.Key, string.Join(", ", header2.Value));
            }

            context.Response.Headers.Add("X-BackOffice-Proxy", DateTime.Now.ToString());

            foreach (var header2 in response.Content.Headers)
            {
                if (header2.Key == "Content-Length") // this will be set automatically at dispose time
                    continue;

                log.Append(' ');
                log.Append(header2.Key);
                log.Append(':');
                log.AppendLine(string.Join(", ", header2.Value));
                context.Response.Headers.Add(header2.Key, string.Join(", ", header2.Value));
            }
            log.AppendLine();

            var ct = context.Response.ContentType;
            if (LogResponseBody || (RewriteTargetInText && host != null && IsTextContent(ct)))
            {
                using var ms = new MemoryStream();
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await stream.CopyToAsync(ms).ConfigureAwait(false);
                var enc = context.Response.ContentEncoding ?? Encoding.UTF8;
                var body = enc.GetString(ms.ToArray());

                if (LogResponseBody && IsTextContent(ct))
                {
                    log.AppendLine("Response Body:");
                    log.AppendLine(body);
                }

                if (RewriteTargetInText && TryReplace(body, "//" + _targetHost + ":" + _targetPort + "/", "//" + host + "/", out var replaced))
                {
                    var bytes = enc.GetBytes(replaced);
                    using var ms2 = new MemoryStream(bytes);
                    ms2.Position = 0;
                    context.Response.ContentLength64 = bytes.Length;
                    await ms2.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
                }
                else
                {
                    ms.Position = 0;
                    if (response.Content.Headers.ContentLength.HasValue)
                    {
                        context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
                    }

                    await ms.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
                }
            }
            else
            {
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    context.Response.ContentLength64 = response.Content.Headers.ContentLength.Value;
                }
                await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
            }

            log.AppendLine(context.Request.HttpMethod + " " + context.Request.UserHostName + " (" + context.Request.UserHostAddress + ") " + context.Request.Url + " " + context.Request.RawUrl);
            var header = ((int)statusCode).ToString() + " " + context.Request.HttpMethod + " " + context.Request.UserHostName + " " + context.Request.RawUrl;
            Log(header + Environment.NewLine + log.ToString(), string.Empty);
        }

        public static void Log(string message, [CallerMemberName] string? methodName = null) => Log(TraceLevel.Info, message, methodName);
        public static void Log(TraceLevel level, string message, [CallerMemberName] string? methodName = null) => Console.WriteLine(level + ":" + methodName + ":" + message);

        public void Stop() => _listener.Stop();
        public override string ToString() => string.Join(", ", Prefixes) + " => " + TargetUrl;

        // out-of-the-box Replace doesn't tell if something *was* replaced or not
        public static bool TryReplace(string input, string oldValue, string newValue, out string result)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
            {
                result = input;
                return false;
            }

            var oldLen = oldValue.Length;
            var sb = new StringBuilder(input.Length);
            var changed = false;
            var offset = 0;
            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];

                if (offset > 0)
                {
                    if (c == oldValue[offset])
                    {
                        offset++;
                        if (oldLen == offset)
                        {
                            changed = true;
                            sb.Append(newValue);
                            offset = 0;
                        }
                        continue;
                    }

                    for (var j = 0; j < offset; j++)
                    {
                        sb.Append(input[i - offset + j]);
                    }

                    sb.Append(c);
                    offset = 0;
                }
                else
                {
                    if (c == oldValue[0])
                    {
                        if (oldLen == 1)
                        {
                            changed = true;
                            sb.Append(newValue);
                        }
                        else
                        {
                            offset = 1;
                        }
                        continue;
                    }

                    sb.Append(c);
                }
            }

            if (changed)
            {
                result = sb.ToString();
                return true;
            }

            result = input;
            return false;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects)
                    ((IDisposable)_listener)?.Dispose();
                }

                // free unmanaged resources (unmanaged objects) and override finalizer
                // set large fields to null
                _disposedValue = true;
            }
        }

        // // override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~ProxyServer()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
