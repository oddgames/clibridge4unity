using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace clibridge4unity;

static class ReportServer
{
    static readonly string BaseDir = Path.GetFullPath(Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
        "clibridge4unity"));

    static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "application/javascript",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".xml"] = "application/xml",
        [".ico"] = "image/x-icon",
        [".webp"] = "image/webp",
    };

    public static int Run(string args)
    {
        int port = 8420;
        int ttlMinutes = 60;
        bool publicBind = false;
        bool enableCors = false;

        // Parse args
        if (!string.IsNullOrWhiteSpace(args))
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "--port" && i + 1 < parts.Length)
                    int.TryParse(parts[++i], out port);
                else if (parts[i] == "--ttl" && i + 1 < parts.Length)
                    int.TryParse(parts[++i], out ttlMinutes);
                else if (parts[i] == "--public")
                    publicBind = true;
                else if (parts[i] == "--cors")
                    enableCors = true;
            }
        }

        // Ensure base directories exist
        Directory.CreateDirectory(Path.Combine(BaseDir, "screenshots"));
        Directory.CreateDirectory(Path.Combine(BaseDir, "sessions"));

        var listener = new TcpListener(publicBind ? IPAddress.Any : IPAddress.Loopback, port);
        listener.Start();

        string url = publicBind ? $"http://0.0.0.0:{port}/" : $"http://localhost:{port}/";
        Console.WriteLine($"[SERVE] Listening on {url}");
        Console.WriteLine($"[SERVE] Serving files from: {BaseDir}");
        Console.WriteLine($"[SERVE] TTL: {ttlMinutes} min (files older are cleaned up)");
        if (publicBind) Console.WriteLine("[SERVE] Public bind enabled. Only use this on trusted networks.");
        if (enableCors) Console.WriteLine("[SERVE] CORS enabled.");
        Console.WriteLine($"[SERVE] Press Ctrl+C to stop");

        // Graceful shutdown
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            listener.Stop();
        };

        // Background cleanup thread
        var cleanupThread = new Thread(() => CleanupLoop(ttlMinutes, cts.Token))
        {
            IsBackground = true
        };
        cleanupThread.Start();

        // Request loop
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cts.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    HandleRequest(client, enableCors);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SERVE] Error: {ex.Message}");
            }
        }

        Console.WriteLine("[SERVE] Stopped.");
        return 0;
    }

    static void HandleRequest(TcpClient client, bool enableCors)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);

        string requestLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(requestLine))
            return;

        string line;
        while (!string.IsNullOrEmpty(line = reader.ReadLine()))
        {
            // Drain request headers. The server only supports simple GET/OPTIONS requests.
        }

        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            WriteText(stream, 400, "Bad Request", "Bad Request", enableCors);
            return;
        }

        string method = parts[0].ToUpperInvariant();
        string target = parts[1];
        if (method == "OPTIONS")
        {
            WriteBytes(stream, 204, "No Content", "text/plain", Array.Empty<byte>(), enableCors);
            return;
        }
        if (method != "GET")
        {
            WriteText(stream, 405, "Method Not Allowed", "Method Not Allowed", enableCors);
            return;
        }

        string absolutePath;
        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
            absolutePath = absoluteUri.AbsolutePath;
        else
            absolutePath = target.Split('?')[0];

        // Decode and sanitize path
        string urlPath = Uri.UnescapeDataString(absolutePath).TrimStart('/');
        string fsPath = Path.GetFullPath(Path.Combine(BaseDir, urlPath));

        // Security: ensure resolved path is under BaseDir
        if (!IsUnderBaseDir(fsPath))
        {
            WriteText(stream, 403, "Forbidden", "Forbidden", enableCors);
            return;
        }

        // Directory
        if (Directory.Exists(fsPath))
        {
            // Auto-index
            string indexPath = Path.Combine(fsPath, "index.html");
            if (File.Exists(indexPath))
            {
                ServeFile(stream, indexPath, enableCors);
                return;
            }

            ServeDirectoryListing(stream, fsPath, urlPath, enableCors);
            return;
        }

        // File
        if (File.Exists(fsPath))
        {
            ServeFile(stream, fsPath, enableCors);
            return;
        }

        WriteText(stream, 404, "Not Found", "Not Found", enableCors);
    }

    static bool IsUnderBaseDir(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = BaseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root, StringComparison.OrdinalIgnoreCase))
            return true;

        string rootWithSeparator = root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    static void ServeFile(Stream stream, string filePath, bool enableCors)
    {
        string ext = Path.GetExtension(filePath);
        var fi = new FileInfo(filePath);
        WriteHeaders(stream, 200, "OK", MimeTypes.GetValueOrDefault(ext, "application/octet-stream"), fi.Length, enableCors);

        using var fs = fi.OpenRead();
        fs.CopyTo(stream);
    }

    static void ServeDirectoryListing(Stream stream, string dirPath, string urlPath, bool enableCors)
    {
        string displayPath = "/" + urlPath.TrimEnd('/');
        if (displayPath == "/") displayPath = "/";

        var sb = new System.Text.StringBuilder();
        sb.Append($@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<meta http-equiv=""refresh"" content=""5"">
<title>{WebEncode(displayPath)} - clibridge4unity</title>
<style>
  body {{ background: #1e1e1e; color: #d4d4d4; font-family: 'Segoe UI', Consolas, monospace; margin: 0; padding: 20px; }}
  h1 {{ color: #569cd6; font-size: 1.2em; border-bottom: 1px solid #333; padding-bottom: 8px; }}
  a {{ color: #4ec9b0; text-decoration: none; }}
  a:hover {{ text-decoration: underline; }}
  table {{ border-collapse: collapse; width: 100%; }}
  th {{ text-align: left; color: #808080; font-weight: normal; padding: 4px 12px 4px 0; border-bottom: 1px solid #333; }}
  td {{ padding: 4px 12px 4px 0; border-bottom: 1px solid #2a2a2a; }}
  .size {{ color: #808080; text-align: right; }}
  .time {{ color: #666; }}
  .thumb {{ max-width: 120px; max-height: 80px; margin: 4px 0; border-radius: 3px; }}
  .dir {{ color: #dcdcaa; }}
</style>
</head>
<body>
<h1>{WebEncode(displayPath)}</h1>
<table>
<tr><th>Name</th><th>Size</th><th>Modified</th></tr>
");

        // Parent link
        if (!string.IsNullOrEmpty(urlPath) && urlPath.Trim('/').Length > 0)
        {
            string parent = "/" + string.Join("/", urlPath.TrimEnd('/').Split('/')[..^1]);
            if (parent == "/") parent = "/";
            sb.Append($"<tr><td><a href=\"{parent}\">../</a></td><td></td><td></td></tr>\n");
        }

        // Directories first
        foreach (var dir in Directory.GetDirectories(dirPath))
        {
            var di = new DirectoryInfo(dir);
            string href = "/" + (string.IsNullOrEmpty(urlPath) ? "" : urlPath.TrimEnd('/') + "/") + di.Name + "/";
            sb.Append($"<tr><td class=\"dir\"><a href=\"{WebEncode(href)}\">{WebEncode(di.Name)}/</a></td>");
            sb.Append("<td></td>");
            sb.Append($"<td class=\"time\">{di.LastWriteTime:yyyy-MM-dd HH:mm}</td></tr>\n");
        }

        // Files
        foreach (var file in Directory.GetFiles(dirPath))
        {
            var fi = new FileInfo(file);
            string href = "/" + (string.IsNullOrEmpty(urlPath) ? "" : urlPath.TrimEnd('/') + "/") + fi.Name;
            string ext = fi.Extension.ToLowerInvariant();

            sb.Append($"<tr><td><a href=\"{WebEncode(href)}\">{WebEncode(fi.Name)}</a>");

            // Inline thumbnail for images
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
            {
                sb.Append($"<br><a href=\"{WebEncode(href)}\"><img class=\"thumb\" src=\"{WebEncode(href)}\" loading=\"lazy\"></a>");
            }

            sb.Append("</td>");
            sb.Append($"<td class=\"size\">{FormatSize(fi.Length)}</td>");
            sb.Append($"<td class=\"time\">{fi.LastWriteTime:yyyy-MM-dd HH:mm}</td></tr>\n");
        }

        sb.Append("</table>\n</body>\n</html>");

        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
        WriteBytes(stream, 200, "OK", "text/html; charset=utf-8", data, enableCors);
    }

    static void WriteText(Stream stream, int statusCode, string reason, string body, bool enableCors)
    {
        WriteBytes(stream, statusCode, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(body), enableCors);
    }

    static void WriteBytes(Stream stream, int statusCode, string reason, string contentType, byte[] body, bool enableCors)
    {
        WriteHeaders(stream, statusCode, reason, contentType, body.Length, enableCors);
        if (body.Length > 0)
            stream.Write(body, 0, body.Length);
    }

    static void WriteHeaders(Stream stream, int statusCode, string reason, string contentType, long contentLength, bool enableCors)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: no-cache, no-store, must-revalidate\r\n");
        if (!string.IsNullOrEmpty(contentType))
            sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {contentLength}\r\n");
        if (enableCors)
        {
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: *\r\n");
        }
        sb.Append("\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
    }

    static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    static string WebEncode(string s) =>
        System.Net.WebUtility.HtmlEncode(s);

    static void CleanupLoop(int ttlMinutes, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromMinutes(5));
                if (ct.IsCancellationRequested) break;

                var cutoff = DateTime.Now.AddMinutes(-ttlMinutes);
                CleanupDir(BaseDir, cutoff, isRoot: true);
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore cleanup errors */ }
        }
    }

    static void CleanupDir(string dir, DateTime cutoff, bool isRoot)
    {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch { }
        }

        foreach (var sub in Directory.GetDirectories(dir))
        {
            CleanupDir(sub, cutoff, isRoot: false);

            // Remove empty subdirectories (but not the root or its immediate children)
            if (!isRoot)
            {
                try
                {
                    if (Directory.GetFileSystemEntries(sub).Length == 0)
                        Directory.Delete(sub);
                }
                catch { }
            }
        }
    }
}
