using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarWidget;

/// <summary>Result of an <see cref="Http"/> request.</summary>
public sealed record HttpResult(int Status, byte[] Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Minimal blocking HTTP client on top of WinHTTP (the OS stack: system proxy, TLS and gzip for free,
/// and a fraction of the binary size/memory of HttpClient). Call from a background thread.
/// </summary>
public static unsafe class Http
{
    const uint WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY = 4, WINHTTP_FLAG_SECURE = 0x00800000;
    const uint WINHTTP_QUERY_STATUS_CODE = 19, WINHTTP_QUERY_FLAG_NUMBER = 0x20000000;
    const uint WINHTTP_OPTION_DECOMPRESSION = 118, WINHTTP_DECOMPRESSION_FLAG_ALL = 3;

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr WinHttpOpen(string agent, uint accessType, string? proxy, string? bypass, uint flags);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr WinHttpConnect(IntPtr session, string server, ushort port, uint reserved);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern IntPtr WinHttpOpenRequest(IntPtr conn, string verb, string path, string? version, string? referrer, IntPtr accept, uint flags);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern bool WinHttpSendRequest(IntPtr req, string? headers, uint headersLen, void* data, uint dataLen, uint totalLen, IntPtr ctx);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] static extern bool WinHttpReceiveResponse(IntPtr req, IntPtr reserved);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] static extern bool WinHttpQueryHeaders(IntPtr req, uint level, IntPtr name, void* buf, uint* len, uint* index);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] static extern bool WinHttpQueryDataAvailable(IntPtr req, uint* avail);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] static extern bool WinHttpReadData(IntPtr req, void* buf, uint len, uint* read);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] static extern bool WinHttpSetTimeouts(IntPtr h, int resolve, int connect, int send, int receive);
    [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)] static extern bool WinHttpSetOption(IntPtr h, uint option, void* buf, uint len);
    [DllImport("winhttp.dll", ExactSpelling = true)] static extern bool WinHttpCloseHandle(IntPtr h);

    static IntPtr _session;
    static readonly object Gate = new();

    static IntPtr Session
    {
        get
        {
            lock (Gate)
            {
                if (_session == IntPtr.Zero)
                {
                    _session = WinHttpOpen("TaskbarWidget/2", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null, null, 0);
                    if (_session == IntPtr.Zero) throw new HttpRequestFailedException("WinHttpOpen", Marshal.GetLastWin32Error());
                    uint dec = WINHTTP_DECOMPRESSION_FLAG_ALL;
                    WinHttpSetOption(_session, WINHTTP_OPTION_DECOMPRESSION, &dec, sizeof(uint));
                }
                return _session;
            }
        }
    }

    public static HttpResult Get(string url, IEnumerable<KeyValuePair<string, string>>? headers = null, int timeoutMs = 8000) =>
        Send("GET", url, headers, null, null, timeoutMs);

    public static HttpResult PostJson(string url, string json, IEnumerable<KeyValuePair<string, string>>? headers = null, int timeoutMs = 8000) =>
        Send("POST", url, headers, Encoding.UTF8.GetBytes(json), "application/json", timeoutMs);

    public static HttpResult Send(string method, string url, IEnumerable<KeyValuePair<string, string>>? headers,
        byte[]? body, string? contentType, int timeoutMs = 8000)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" && uri.Scheme != "http") throw new ArgumentException("Only http/https URLs are supported", nameof(url));

        IntPtr conn = 0, req = 0;
        try
        {
            conn = WinHttpConnect(Session, uri.Host, (ushort)uri.Port, 0);
            if (conn == IntPtr.Zero) throw new HttpRequestFailedException("connect", Marshal.GetLastWin32Error());

            req = WinHttpOpenRequest(conn, method, uri.PathAndQuery, null, null, IntPtr.Zero,
                uri.Scheme == "https" ? WINHTTP_FLAG_SECURE : 0);
            if (req == IntPtr.Zero) throw new HttpRequestFailedException("open request", Marshal.GetLastWin32Error());
            WinHttpSetTimeouts(req, timeoutMs, timeoutMs, timeoutMs, timeoutMs);

            var sb = new StringBuilder("Accept: application/json\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (headers != null)
                foreach (var (k, v) in headers)
                {
                    if (k.AsSpan().IndexOfAny('\r', '\n') >= 0 || v.AsSpan().IndexOfAny('\r', '\n') >= 0)
                        throw new ArgumentException("Header names/values must not contain line breaks");
                    sb.Append(k).Append(": ").Append(v).Append("\r\n");
                }
            var hdr = sb.ToString();

            fixed (byte* data = body)
            {
                uint len = (uint)(body?.Length ?? 0);
                if (!WinHttpSendRequest(req, hdr, (uint)hdr.Length, data, len, len, IntPtr.Zero))
                    throw new HttpRequestFailedException("send", Marshal.GetLastWin32Error());
            }
            if (!WinHttpReceiveResponse(req, IntPtr.Zero))
                throw new HttpRequestFailedException("receive", Marshal.GetLastWin32Error());

            uint status = 0, size = sizeof(uint);
            WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, IntPtr.Zero, &status, &size, null);

            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            while (true)
            {
                uint avail = 0;
                if (!WinHttpQueryDataAvailable(req, &avail)) throw new HttpRequestFailedException("read", Marshal.GetLastWin32Error());
                if (avail == 0) break;
                uint read = 0;
                fixed (byte* b = buf)
                    if (!WinHttpReadData(req, b, (uint)Math.Min(avail, buf.Length), &read))
                        throw new HttpRequestFailedException("read", Marshal.GetLastWin32Error());
                if (read == 0) break;
                ms.Write(buf, 0, (int)read);
                if (ms.Length > 16 * 1024 * 1024) throw new HttpRequestFailedException("response too large", 0);
            }
            return new HttpResult((int)status, ms.ToArray());
        }
        finally
        {
            if (req != IntPtr.Zero) WinHttpCloseHandle(req);
            if (conn != IntPtr.Zero) WinHttpCloseHandle(conn);
        }
    }
}

public sealed class HttpRequestFailedException(string stage, int win32Error)
    : Exception(Describe(stage, win32Error))
{
    public int Win32Error { get; } = win32Error;

    static string Describe(string stage, int err) => err switch
    {
        12002 => "Request timed out",
        12007 => "Server name could not be resolved (offline?)",
        12029 or 12030 => "Could not connect to server",
        12175 => "TLS/certificate error",
        0 => $"HTTP {stage} failed",
        _ => $"HTTP {stage} failed (error {err})",
    };
}
