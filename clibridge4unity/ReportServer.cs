using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace clibridge4unity;

static class ReportServer
{
    static readonly string BaseDir = Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
        "clibridge4unity");

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
            }
        }

        // Ensure base directories exist
        Directory.CreateDirectory(Path.Combine(BaseDir, "screenshots"));
        Directory.CreateDirectory(Path.Combine(BaseDir, "sessions"));

        var listener = new HttpListener();
        bool wildcard = false;

        // Try wildcard binding first, fall back to localhost
        try
        {
            listener.Prefixes.Add($"http://+:{port}/");
            listener.Start();
            wildcard = true;
        }
        catch
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
        }

        string url = wildcard ? $"http://+:{port}/" : $"http://localhost:{port}/";
        Console.WriteLine($"[SERVE] Listening on {url}");
        Console.WriteLine($"[SERVE] Serving files from: {BaseDir}");
        Console.WriteLine($"[SERVE] TTL: {ttlMinutes} min (files older are cleaned up)");
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
            HttpListenerContext ctx;
            try
            {
                ctx = listener.GetContext();
            }
            catch
            {
                break;
            }

            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SERVE] Error: {ex.Message}");
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.Close();
                }
                catch { }
            }
        }

        Console.WriteLine("[SERVE] Stopped.");
        return 0;
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        // CORS headers
        resp.Headers.Set("Access-Control-Allow-Origin", "*");
        resp.Headers.Set("Access-Control-Allow-Methods", "GET, OPTIONS");
        resp.Headers.Set("Access-Control-Allow-Headers", "*");
        resp.Headers.Set("Cache-Control", "no-cache, no-store, must-revalidate");

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        // Decode and sanitize path
        string urlPath = Uri.UnescapeDataString(req.Url!.AbsolutePath).TrimStart('/');
        string fsPath = Path.GetFullPath(Path.Combine(BaseDir, urlPath));

        // Security: ensure resolved path is under BaseDir
        if (!fsPath.StartsWith(BaseDir, StringComparison.OrdinalIgnoreCase))
        {
            resp.StatusCode = 403;
            resp.Close();
            return;
        }

        // Directory
        if (Directory.Exists(fsPath))
        {
            // Auto-index
            string indexPath = Path.Combine(fsPath, "index.html");
            if (File.Exists(indexPath))
            {
                ServeFile(resp, indexPath);
                return;
            }

            ServeDirectoryListing(resp, fsPath, urlPath);
            return;
        }

        // File
        if (File.Exists(fsPath))
        {
            ServeFile(resp, fsPath);
            return;
        }

        resp.StatusCode = 404;
        byte[] msg = System.Text.Encoding.UTF8.GetBytes("Not Found");
        resp.ContentType = "text/plain";
        resp.ContentLength64 = msg.Length;
        resp.OutputStream.Write(msg, 0, msg.Length);
        resp.Close();
    }

    static void ServeFile(HttpListenerResponse resp, string filePath)
    {
        string ext = Path.GetExtension(filePath);
        resp.ContentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");

        var fi = new FileInfo(filePath);
        resp.ContentLength64 = fi.Length;

        using var fs = fi.OpenRead();
        fs.CopyTo(resp.OutputStream);
        resp.Close();
    }

    static void ServeDirectoryListing(HttpListenerResponse resp, string dirPath, string urlPath)
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

        resp.ContentType = "text/html; charset=utf-8";
        byte[] data = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        resp.ContentLength64 = data.Length;
        resp.OutputStream.Write(data, 0, data.Length);
        resp.Close();
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
