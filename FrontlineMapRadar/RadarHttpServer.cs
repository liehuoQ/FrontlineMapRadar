using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FrontlineMapRadar;

/// <summary>
/// 局域网 HTTP 服务：网页雷达 + /api/state JSON。
/// 使用 TcpListener，无需 netsh urlacl。
/// </summary>
public sealed class RadarHttpServer : IDisposable
{
    private readonly Plugin plugin;
    private readonly RadarFeedService feed;
    private readonly object sync = new();
    private TcpListener? listener;
    private CancellationTokenSource? cts;
    private Task? acceptTask;
    private int boundPort;
    private string lastError = string.Empty;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
    };

    public RadarHttpServer(Plugin plugin, RadarFeedService feed)
    {
        this.plugin = plugin;
        this.feed = feed;
    }

    public bool IsRunning
    {
        get
        {
            lock (sync)
                return listener != null;
        }
    }

    public int BoundPort
    {
        get
        {
            lock (sync)
                return boundPort;
        }
    }

    public string LastError
    {
        get
        {
            lock (sync)
                return lastError;
        }
    }

    public IReadOnlyList<string> GetLanUrls()
    {
        var port = BoundPort;
        if (port <= 0)
            return Array.Empty<string>();

        var urls = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("127."))
                        continue;
                    urls.Add($"http://{ip}:{port}/");
                }
            }
        }
        catch
        {
            // ignore
        }

        urls.Add($"http://127.0.0.1:{port}/");
        return urls;
    }

    public void ApplyConfig()
    {
        if (plugin.Config.RemoteRadarEnabled)
            Start(plugin.Config.RemoteRadarPort);
        else
            Stop();
    }

    public void Start(int port)
    {
        port = Math.Clamp(port, 1024, 65535);
        lock (sync)
        {
            if (listener != null && boundPort == port)
                return;
        }

        Stop();

        try
        {
            var tcp = new TcpListener(IPAddress.Any, port);
            tcp.Start();
            var tokenSource = new CancellationTokenSource();
            lock (sync)
            {
                listener = tcp;
                cts = tokenSource;
                boundPort = port;
                lastError = string.Empty;
            }

            acceptTask = Task.Run(() => AcceptLoopAsync(tcp, tokenSource.Token));
            plugin.Log.Information($"Remote radar HTTP listening on 0.0.0.0:{port}");
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                lastError = ex.Message;
                boundPort = 0;
            }

            plugin.Log.Error(ex, $"Failed to start remote radar HTTP on port {port}");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? tokenSource;
        TcpListener? tcp;
        Task? task;
        lock (sync)
        {
            tokenSource = cts;
            tcp = listener;
            task = acceptTask;
            cts = null;
            listener = null;
            acceptTask = null;
            boundPort = 0;
        }

        try
        {
            tokenSource?.Cancel();
            tcp?.Stop();
        }
        catch
        {
            // ignore
        }

        try
        {
            tokenSource?.Dispose();
        }
        catch
        {
            // ignore
        }

        // 不等待 accept 彻底结束，避免卡 UI
        _ = task;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener tcp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    plugin.Log.Warning(ex, "Remote radar accept error");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is not { } req)
                    return;

                var path = req.Path;
                if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonConvert.SerializeObject(feed.GetSnapshot(), JsonSettings);
                    await WriteTextResponseAsync(stream, 200, "application/json; charset=utf-8", json, ct)
                        .ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/api/calibration", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCalibrationAsync(stream, req, ct).ConfigureAwait(false);
                    return;
                }

                if (path is "/" or "/index.html")
                {
                    var html = BuildIndexHtml();
                    await WriteTextResponseAsync(stream, 200, "text/html; charset=utf-8", html, ct)
                        .ConfigureAwait(false);
                    return;
                }

                if (path.StartsWith("/maps/", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeMapAsync(stream, path, ct).ConfigureAwait(false);
                    return;
                }

                if (path.StartsWith("/gc/", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeGcAssetAsync(stream, path, ct).ConfigureAwait(false);
                    return;
                }

                await WriteTextResponseAsync(stream, 404, "text/plain; charset=utf-8", "Not Found", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    plugin.Log.Verbose(ex, "Remote radar client error");
            }
        }
    }

    private async Task HandleCalibrationAsync(NetworkStream stream, HttpRequest req, CancellationToken ct)
    {
        if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            var snap = feed.GetSnapshot();
            var tid = snap.TerritoryId != 0 ? snap.TerritoryId : FrontlineMapAssets.GloryTerritoryId;
            var dto = MapCalibrationDto.From(tid, plugin.Config.GetCalibration(tid));
            var json = JsonConvert.SerializeObject(dto, JsonSettings);
            await WriteTextResponseAsync(stream, 200, "application/json; charset=utf-8", json, ct)
                .ConfigureAwait(false);
            return;
        }

        if (!req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            !req.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "text/plain; charset=utf-8", "Method Not Allowed", ct)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var dto = JsonConvert.DeserializeObject<MapCalibrationDto>(req.Body ?? "{}");
            if (dto == null)
            {
                await WriteTextResponseAsync(stream, 400, "text/plain; charset=utf-8", "Bad Request", ct)
                    .ConfigureAwait(false);
                return;
            }

            var tid = dto.TerritoryId != 0 ? dto.TerritoryId : FrontlineMapAssets.GloryTerritoryId;
            plugin.Config.SetCalibration(tid, dto.ToModel());
            var saved = MapCalibrationDto.From(tid, plugin.Config.GetCalibration(tid));
            var json = JsonConvert.SerializeObject(saved, JsonSettings);
            await WriteTextResponseAsync(stream, 200, "application/json; charset=utf-8", json, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            plugin.Log.Warning(ex, "Failed to save calibration");
            await WriteTextResponseAsync(stream, 400, "text/plain; charset=utf-8", "Bad Request", ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read <= 0)
                break;
            total += read;
            var textSoFar = Encoding.ASCII.GetString(buffer, 0, total);
            if (textSoFar.Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        if (total == 0)
            return null;

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            return null;

        var headerText = text[..headerEnd];
        var bodyStart = headerEnd + 4;
        var firstLineEnd = headerText.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLineEnd < 0)
            return null;

        var firstLine = headerText[..firstLineEnd];
        var parts = firstLine.Split(' ');
        if (parts.Length < 2)
            return null;

        var path = parts[1];
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
            path = path[..q];

        var contentLength = 0;
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var len))
            {
                contentLength = Math.Clamp(len, 0, 1_000_000);
            }
        }

        var bodyBuilder = new StringBuilder();
        if (bodyStart < text.Length)
            bodyBuilder.Append(text[bodyStart..]);

        var bodyBytesHave = Encoding.UTF8.GetByteCount(bodyBuilder.ToString());
        // 简化：若已读够 UTF8 文本长度近似即可；小 JSON 一般一次读完
        while (bodyBytesHave < contentLength && total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read <= 0)
                break;
            var chunk = Encoding.UTF8.GetString(buffer, total, read);
            bodyBuilder.Append(chunk);
            total += read;
            bodyBytesHave = Encoding.UTF8.GetByteCount(bodyBuilder.ToString());
        }

        var body = bodyBuilder.ToString();
        if (contentLength > 0 && body.Length > contentLength)
            body = body[..contentLength];

        return new HttpRequest(parts[0], path, body);
    }

    private string BuildIndexHtml()
    {
        // 默认走 HTTP 资源路径（嵌入在 DLL 内），避免把多张大图 base64 进 HTML
        return RadarWebPage.Html.Replace("__DEFAULT_MAP_URL__", "/maps/554.jpg", StringComparison.Ordinal);
    }

    private string?[] PluginSearchDirs() =>
    [
        Path.GetDirectoryName(plugin.PluginInterface.AssemblyLocation.FullName),
        plugin.PluginInterface.ConfigDirectory.FullName,
    ];

    private async Task ServeMapAsync(NetworkStream stream, string path, CancellationToken ct)
    {
        // /maps/554.jpg
        var fileName = Path.GetFileName(path);
        var okExt = fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(fileName) || fileName.Contains("..", StringComparison.Ordinal) || !okExt)
        {
            await WriteTextResponseAsync(stream, 404, "text/plain; charset=utf-8", "Not Found", ct)
                .ConfigureAwait(false);
            return;
        }

        uint territoryId = 0;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        _ = uint.TryParse(stem, out territoryId);

        var bytes = !string.IsNullOrEmpty(stem)
            ? FrontlineMapAssets.TryReadJpegByKey(stem, PluginSearchDirs())
            : null;
        if ((bytes == null || bytes.Length == 0) && territoryId != 0)
            bytes = FrontlineMapAssets.TryReadJpeg(territoryId, PluginSearchDirs());
        if (bytes == null || bytes.Length == 0)
            bytes = FrontlineMapAssets.TryReadJpeg(FrontlineMapAssets.GloryTerritoryId, PluginSearchDirs());

        if (bytes == null || bytes.Length == 0)
        {
            await WriteTextResponseAsync(stream, 404, "text/plain; charset=utf-8", "Map not found", ct)
                .ConfigureAwait(false);
            return;
        }

        var contentType = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                ? "image/webp"
                : "image/jpeg";
        await WriteBinaryResponseAsync(stream, 200, contentType, bytes, ct).ConfigureAwait(false);
    }

    private async Task ServeGcAssetAsync(NetworkStream stream, string path, CancellationToken ct)
    {
        // /gc/badge_maelstrom.png
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
            || !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 404, "text/plain; charset=utf-8", "Not Found", ct)
                .ConfigureAwait(false);
            return;
        }

        // 只允许白名单徽章文件
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "badge_maelstrom.png",
            "badge_twinadder.png",
            "badge_flames.png",
        };
        if (!allowed.Contains(fileName))
        {
            await WriteTextResponseAsync(stream, 404, "text/plain; charset=utf-8", "Not Found", ct)
                .ConfigureAwait(false);
            return;
        }

        byte[]? bytes = null;
        foreach (var dir in PluginSearchDirs())
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                var full = Path.Combine(dir, "Assets", "gc", fileName);
                if (File.Exists(full))
                {
                    bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                    break;
                }
            }
            catch
            {
                // ignore
            }
        }

        if (bytes == null || bytes.Length == 0)
        {
            await WriteTextResponseAsync(stream, 404, "text/plain; charset=utf-8", "Badge not found", ct)
                .ConfigureAwait(false);
            return;
        }

        await WriteBinaryResponseAsync(stream, 200, "image/png", bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteTextResponseAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body,
        CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        await WriteBinaryResponseAsync(stream, status, contentType, bodyBytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteBinaryResponseAsync(
        NetworkStream stream,
        int status,
        string contentType,
        byte[] bodyBytes,
        CancellationToken ct)
    {
        var statusText = status switch
        {
            200 => "OK",
            404 => "Not Found",
            _ => "Error",
        };

        var cache = contentType.StartsWith("image/", StringComparison.Ordinal)
            ? "public, max-age=3600"
            : "no-store";

        var header =
            $"HTTP/1.1 {status} {statusText}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            $"Cache-Control: {cache}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private readonly record struct HttpRequest(string Method, string Path, string Body);
}

/// <summary>平板端雷达页面（内嵌，无需额外文件）。</summary>
internal static class RadarWebPage
{
    public const string Html =
        """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no"/>
<title>前线雷达</title>
<style>
  :root {
    --bg: #0e1116;
    --panel: #171b22;
    --text: #e8ecf1;
    --muted: #8b95a5;
    --maelstrom: #e84a4a;
    --twin: #e6c933;
    --flames: #4a8cff;
    --ally: #3dcc6a;
    --self: #ffffff;
  }
  * { box-sizing: border-box; }
  html, body {
    margin: 0; height: 100%; background: var(--bg); color: var(--text);
    font-family: "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
    overflow: hidden; touch-action: manipulation;
  }
  #app { display: grid; grid-template-rows: auto 1fr auto auto; height: 100%; }
  header, footer, #calPanel {
    padding: 10px 14px; background: var(--panel);
    display: flex; flex-wrap: wrap; gap: 8px 16px; align-items: center;
  }
  header h1 { font-size: 16px; margin: 0; font-weight: 600; letter-spacing: 0.02em; }
  .pill {
    font-size: 12px; color: var(--muted);
    padding: 3px 8px; border: 1px solid #2a3140; border-radius: 999px;
  }
  .pill.on { color: #9BE67E; border-color: #3a5a34; }
  .pill.off { color: #E6A23C; border-color: #5a4630; }
  #stage {
    position: relative; margin: 10px; border-radius: 12px; overflow: hidden;
    background: #c9b896;
    border: 1px solid #8a7350;
    min-height: 0;
  }
  canvas { width: 100%; height: 100%; display: block; }
  .legend { display: flex; gap: 12px; flex-wrap: wrap; font-size: 12px; color: var(--muted); }
  .dot {
    display: inline-block; width: 10px; height: 10px; border-radius: 50%;
    margin-right: 5px; vertical-align: -1px;
  }
  #tip {
    position: absolute; pointer-events: none; display: none;
    background: rgba(10,12,16,0.92); border: 1px solid #333;
    padding: 6px 8px; border-radius: 8px; font-size: 12px; max-width: 240px;
    z-index: 2;
  }
  label.toggle { font-size: 12px; color: var(--muted); display: inline-flex; gap: 6px; align-items: center; }
  label.size-ctrl input[type=range] { width: 88px; }
  kbd {
    font-size: 10px; color: #c9d2de; background: #243044; border: 1px solid #3a465a;
    border-radius: 4px; padding: 1px 5px; font-family: inherit;
  }
  button, .btn {
    background: #243044; color: var(--text); border: 1px solid #334155;
    border-radius: 8px; padding: 6px 10px; font-size: 12px;
  }
  button:active { background: #31425c; }
  #calPanel { display: none; border-top: 1px solid #252b36; align-items: flex-end; gap: 10px 14px; }
  #calPanel.open { display: flex; }
  .cal-item { display: flex; flex-direction: column; gap: 4px; min-width: 140px; flex: 1; }
  .cal-item label { font-size: 11px; color: var(--muted); }
  .cal-item input[type=range] { width: 100%; }
  .cal-row { display: flex; gap: 6px; align-items: center; }
  .cal-val { font-size: 12px; min-width: 52px; text-align: right; font-variant-numeric: tabular-nums; }
  .cal-actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
  #calMsg { font-size: 12px; color: var(--muted); }
</style>
</head>
<body>
<div id="app">
  <header>
    <h1>前线雷达</h1>
    <span id="status" class="pill off">未连接</span>
    <span id="zone" class="pill">—</span>
    <span id="counts" class="pill">0 人</span>
    <span id="own" class="pill">本阵营 —</span>
    <label class="toggle"><input type="checkbox" id="showNames"/>显示职业 <kbd>N</kbd></label>
    <label class="toggle size-ctrl">点大小
      <button type="button" id="dotSmaller" title="缩小 [">−</button>
      <input id="dotSize" type="range" min="1" max="10" step="0.5" value="3"/>
      <button type="button" id="dotBigger" title="放大 ]">+</button>
      <span class="cal-val" id="dotSizeV">3.0</span>
      <kbd>[</kbd><kbd>]</kbd>
    </label>
    <label class="toggle size-ctrl">描边
      <button type="button" id="outlineSmaller" title="变细 ,">−</button>
      <input id="outlineSize" type="range" min="0" max="4" step="0.25" value="1.25"/>
      <button type="button" id="outlineBigger" title="变粗 .">+</button>
      <span class="cal-val" id="outlineSizeV">1.25</span>
      <kbd>,</kbd><kbd>.</kbd>
    </label>
    <label class="toggle size-ctrl">标记
      <button type="button" id="markerSmaller" title="缩小 ;">−</button>
      <input id="markerSize" type="range" min="4" max="28" step="1" value="12"/>
      <button type="button" id="markerBigger" title="放大 '">+</button>
      <span class="cal-val" id="markerSizeV">12</span>
      <kbd>;</kbd><kbd>'</kbd>
    </label>
    <label class="toggle"><input type="checkbox" id="calToggle"/>校准</label>
  </header>
  <div id="stage">
    <canvas id="cv"></canvas>
    <div id="tip"></div>
  </div>
  <div id="calPanel">
    <div class="cal-item">
      <label>偏移 X</label>
      <div class="cal-row">
        <button type="button" data-nudge="offsetX:-0.01">−</button>
        <input id="calOx" type="range" min="-0.5" max="0.5" step="0.001" value="0"/>
        <button type="button" data-nudge="offsetX:0.01">+</button>
        <span class="cal-val" id="calOxV">0.000</span>
      </div>
    </div>
    <div class="cal-item">
      <label>偏移 Y</label>
      <div class="cal-row">
        <button type="button" data-nudge="offsetY:-0.01">−</button>
        <input id="calOy" type="range" min="-0.5" max="0.5" step="0.001" value="0"/>
        <button type="button" data-nudge="offsetY:0.01">+</button>
        <span class="cal-val" id="calOyV">0.000</span>
      </div>
    </div>
    <div class="cal-item">
      <label>缩放 X</label>
      <div class="cal-row">
        <button type="button" data-nudge="scaleX:-0.01">−</button>
        <input id="calSx" type="range" min="0.5" max="1.5" step="0.001" value="1"/>
        <button type="button" data-nudge="scaleX:0.01">+</button>
        <span class="cal-val" id="calSxV">1.000</span>
      </div>
    </div>
    <div class="cal-item">
      <label>缩放 Y</label>
      <div class="cal-row">
        <button type="button" data-nudge="scaleY:-0.01">−</button>
        <input id="calSy" type="range" min="0.5" max="1.5" step="0.001" value="1"/>
        <button type="button" data-nudge="scaleY:0.01">+</button>
        <span class="cal-val" id="calSyV">1.000</span>
      </div>
    </div>
    <label class="toggle"><input type="checkbox" id="calFlipY"/>翻转 Y</label>
    <div class="cal-actions">
      <button type="button" id="calReset">推荐校准</button>
      <button type="button" id="calSave">保存到插件</button>
      <span id="calMsg"></span>
    </div>
  </div>
  <footer>
    <div class="legend">
      <span><i class="dot" style="background:var(--maelstrom)"></i>黑涡</span>
      <span><i class="dot" style="background:var(--flames)"></i>恒辉</span>
      <span><i class="dot" style="background:var(--twin)"></i>双蛇</span>
      <span><i class="dot" style="background:var(--ally)"></i>我方</span>
      <span><i class="dot" style="background:var(--ally); outline:2px solid #fff; outline-offset:1px"></i>自己（带朝向）</span>
      <span>◇ A–D / 1–4 场景标记</span>
      <span>阵营徽章=出生点（己方放大高亮）</span>
      <span style="opacity:.85">N 职业 · [ ] 点 · , . 描边 · ; ' 标记</span>
    </div>
  </footer>
</div>
<script>
const COLORS = {
  maelstrom: getCss('--maelstrom'),
  twinAdder: getCss('--twin'),
  flames: getCss('--flames'),
  ally: getCss('--ally'),
  self: getCss('--self'),
};
// 场景标记 A B C D / 1 2 3 4
const MARKER_COLORS = [
  '#ff6b6b', '#f0d44c', '#5aa7ff', '#d07cff',
  '#ff8a65', '#ffe082', '#64b5f6', '#ce93d8',
];
function getCss(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

const cv = document.getElementById('cv');
const ctx = cv.getContext('2d');
const stage = document.getElementById('stage');
const tip = document.getElementById('tip');
const showNamesEl = document.getElementById('showNames');
const calToggle = document.getElementById('calToggle');
const calPanel = document.getElementById('calPanel');
const calOx = document.getElementById('calOx');
const calOy = document.getElementById('calOy');
const calSx = document.getElementById('calSx');
const calSy = document.getElementById('calSy');
const calFlipY = document.getElementById('calFlipY');
const calMsg = document.getElementById('calMsg');
const dotSizeEl = document.getElementById('dotSize');
const dotSizeV = document.getElementById('dotSizeV');
const outlineSizeEl = document.getElementById('outlineSize');
const outlineSizeV = document.getElementById('outlineSizeV');
const markerSizeEl = document.getElementById('markerSize');
const markerSizeV = document.getElementById('markerSizeV');

const LS_DOT = 'flradar.dotSize';
const LS_OUTLINE = 'flradar.outlineSize';
const LS_MARKER = 'flradar.markerSize';
const LS_NAMES = 'flradar.showNames';

function loadUiPrefs() {
  const savedDot = parseFloat(localStorage.getItem(LS_DOT));
  if (!Number.isNaN(savedDot) && savedDot >= 1 && savedDot <= 10) {
    dotSizeEl.value = String(savedDot);
  }
  const savedOutline = parseFloat(localStorage.getItem(LS_OUTLINE));
  if (!Number.isNaN(savedOutline) && savedOutline >= 0 && savedOutline <= 4) {
    outlineSizeEl.value = String(savedOutline);
  }
  const savedMarker = parseFloat(localStorage.getItem(LS_MARKER));
  if (!Number.isNaN(savedMarker) && savedMarker >= 4 && savedMarker <= 28) {
    markerSizeEl.value = String(savedMarker);
  }
  const savedNames = localStorage.getItem(LS_NAMES);
  if (savedNames === '1' || savedNames === '0') {
    showNamesEl.checked = savedNames === '1';
  }
  dotSizeV.textContent = parseFloat(dotSizeEl.value).toFixed(1);
  outlineSizeV.textContent = parseFloat(outlineSizeEl.value).toFixed(2);
  markerSizeV.textContent = String(parseInt(markerSizeEl.value, 10) || 12);
}

function saveDotPref() {
  localStorage.setItem(LS_DOT, String(dotSizeEl.value));
  dotSizeV.textContent = parseFloat(dotSizeEl.value).toFixed(1);
}

function saveOutlinePref() {
  localStorage.setItem(LS_OUTLINE, String(outlineSizeEl.value));
  outlineSizeV.textContent = parseFloat(outlineSizeEl.value).toFixed(2);
}

function saveMarkerPref() {
  localStorage.setItem(LS_MARKER, String(markerSizeEl.value));
  markerSizeV.textContent = String(parseInt(markerSizeEl.value, 10) || 12);
}

function saveNamesPref() {
  localStorage.setItem(LS_NAMES, showNamesEl.checked ? '1' : '0');
}

function nudgeDot(delta) {
  const v = Math.min(10, Math.max(1, (parseFloat(dotSizeEl.value) || 3) + delta));
  dotSizeEl.value = String(v);
  saveDotPref();
  draw();
}

function nudgeOutline(delta) {
  const v = Math.min(4, Math.max(0, (parseFloat(outlineSizeEl.value) || 1.25) + delta));
  outlineSizeEl.value = String(v);
  saveOutlinePref();
  draw();
}

function nudgeMarker(delta) {
  const v = Math.min(28, Math.max(4, (parseInt(markerSizeEl.value, 10) || 12) + delta));
  markerSizeEl.value = String(v);
  saveMarkerPref();
  draw();
}

loadUiPrefs();

showNamesEl.addEventListener('change', () => { saveNamesPref(); draw(); });
dotSizeEl.addEventListener('input', () => { saveDotPref(); draw(); });
outlineSizeEl.addEventListener('input', () => { saveOutlinePref(); draw(); });
markerSizeEl.addEventListener('input', () => { saveMarkerPref(); draw(); });
document.getElementById('dotSmaller').addEventListener('click', () => nudgeDot(-0.5));
document.getElementById('dotBigger').addEventListener('click', () => nudgeDot(0.5));
document.getElementById('outlineSmaller').addEventListener('click', () => nudgeOutline(-0.25));
document.getElementById('outlineBigger').addEventListener('click', () => nudgeOutline(0.25));
document.getElementById('markerSmaller').addEventListener('click', () => nudgeMarker(-1));
document.getElementById('markerBigger').addEventListener('click', () => nudgeMarker(1));

window.addEventListener('keydown', (ev) => {
  const tag = (ev.target && ev.target.tagName) || '';
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
  if (ev.ctrlKey || ev.altKey || ev.metaKey) return;

  const key = ev.key;
  if (key === 'n' || key === 'N') {
    ev.preventDefault();
    showNamesEl.checked = !showNamesEl.checked;
    saveNamesPref();
    draw();
    return;
  }
  if (key === '[') {
    ev.preventDefault();
    nudgeDot(-0.5);
    return;
  }
  if (key === ']') {
    ev.preventDefault();
    nudgeDot(0.5);
    return;
  }
  if (key === ',' || key === '<') {
    ev.preventDefault();
    nudgeOutline(-0.25);
    return;
  }
  if (key === '.' || key === '>') {
    ev.preventDefault();
    nudgeOutline(0.25);
    return;
  }
  if (key === ';' || key === ':') {
    ev.preventDefault();
    nudgeMarker(-1);
    return;
  }
  if (key === "'" || key === '"') {
    ev.preventDefault();
    nudgeMarker(1);
  }
});

calToggle.addEventListener('change', () => {
  calPanel.classList.toggle('open', calToggle.checked);
  resize();
});

let state = null;
let points = [];
let mapImages = {};
let mapLoading = {};
let badgeImages = {};
let badgeLoading = {};
const BADGE_URL = {
  maelstrom: '/gc/badge_maelstrom.png',
  twinAdder: '/gc/badge_twinadder.png',
  flames: '/gc/badge_flames.png',
};
const DEFAULT_MAP = "__DEFAULT_MAP_URL__";
let currentMapUrl = DEFAULT_MAP;
let cal = { territoryId: 554, offsetX: 0, offsetY: 0, scaleX: 1, scaleY: 1, flipY: false };
let calDirty = false;
let lastCalTerritory = 0;

ensureMap(DEFAULT_MAP);
Object.values(BADGE_URL).forEach(ensureBadge);

function readCalFromUi() {
  cal.offsetX = parseFloat(calOx.value) || 0;
  cal.offsetY = parseFloat(calOy.value) || 0;
  cal.scaleX = parseFloat(calSx.value) || 1;
  cal.scaleY = parseFloat(calSy.value) || 1;
  cal.flipY = !!calFlipY.checked;
  document.getElementById('calOxV').textContent = cal.offsetX.toFixed(3);
  document.getElementById('calOyV').textContent = cal.offsetY.toFixed(3);
  document.getElementById('calSxV').textContent = cal.scaleX.toFixed(3);
  document.getElementById('calSyV').textContent = cal.scaleY.toFixed(3);
}

function writeCalToUi() {
  calOx.value = cal.offsetX;
  calOy.value = cal.offsetY;
  calSx.value = cal.scaleX;
  calSy.value = cal.scaleY;
  calFlipY.checked = !!cal.flipY;
  readCalFromUi();
}

function applyCal(x, y) {
  const sx = cal.scaleX || 1;
  const sy = cal.scaleY || 1;
  const ty = cal.flipY ? (1 - y) : y;
  return { x: x * sx + cal.offsetX, y: ty * sy + cal.offsetY };
}

function onCalInput() {
  calDirty = true;
  readCalFromUi();
  calMsg.textContent = '未保存';
  draw();
}
[calOx, calOy, calSx, calSy, calFlipY].forEach(el => el.addEventListener('input', onCalInput));

document.querySelectorAll('[data-nudge]').forEach(btn => {
  btn.addEventListener('click', () => {
    const [key, delta] = btn.getAttribute('data-nudge').split(':');
    const d = parseFloat(delta);
    cal[key] = (parseFloat(cal[key]) || 0) + d;
    if (key.startsWith('scale')) cal[key] = Math.min(1.5, Math.max(0.5, cal[key]));
    if (key.startsWith('offset')) cal[key] = Math.min(0.5, Math.max(-0.5, cal[key]));
    writeCalToUi();
    calDirty = true;
    calMsg.textContent = '未保存';
    draw();
  });
});

document.getElementById('calReset').addEventListener('click', () => {
  // 推荐校准：映射到装饰边框内侧
  cal.offsetX = 0.048; cal.offsetY = 0.048; cal.scaleX = 0.904; cal.scaleY = 0.904; cal.flipY = false;
  writeCalToUi();
  calDirty = true;
  calMsg.textContent = '已恢复推荐校准（未保存）';
  draw();
});

document.getElementById('calSave').addEventListener('click', async () => {
  readCalFromUi();
  const tid = (state && state.territoryId) || cal.territoryId || 554;
  const payload = {
    territoryId: tid,
    offsetX: cal.offsetX,
    offsetY: cal.offsetY,
    scaleX: cal.scaleX,
    scaleY: cal.scaleY,
    flipY: cal.flipY,
  };
  try {
    const res = await fetch('/api/calibration', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
    if (!res.ok) throw new Error('HTTP ' + res.status);
    const saved = await res.json();
    cal = Object.assign(cal, saved);
    writeCalToUi();
    calDirty = false;
    calMsg.textContent = '已保存';
  } catch (e) {
    calMsg.textContent = '保存失败';
  }
});

function syncCalFromState() {
  if (!state || !state.calibration) return;
  const c = state.calibration;
  const tid = c.territoryId || state.territoryId || 554;
  // 切换领地时强制同步新校准；同领地编辑中不覆盖
  if (calDirty && lastCalTerritory === tid) return;
  cal.territoryId = tid;
  cal.offsetX = c.offsetX || 0;
  cal.offsetY = c.offsetY || 0;
  cal.scaleX = c.scaleX == null ? 1 : c.scaleX;
  cal.scaleY = c.scaleY == null ? 1 : c.scaleY;
  cal.flipY = !!c.flipY;
  lastCalTerritory = tid;
  writeCalToUi();
  calDirty = false;
  calMsg.textContent = '';
}

function resize() {
  const rect = stage.getBoundingClientRect();
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  cv.width = Math.max(1, Math.floor(rect.width * dpr));
  cv.height = Math.max(1, Math.floor(rect.height * dpr));
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  draw();
}
window.addEventListener('resize', resize);

function ensureMap(url) {
  if (!url) return null;
  if (mapImages[url]) return mapImages[url];
  if (mapLoading[url]) return null;
  mapLoading[url] = true;
  const img = new Image();
  img.onload = () => { mapImages[url] = img; mapLoading[url] = false; draw(); };
  img.onerror = () => {
    mapLoading[url] = false;
    if (url !== DEFAULT_MAP && DEFAULT_MAP) ensureMap(DEFAULT_MAP);
  };
  img.src = url;
  return null;
}

function ensureBadge(url) {
  if (!url) return null;
  if (badgeImages[url]) return badgeImages[url];
  if (badgeLoading[url]) return null;
  badgeLoading[url] = true;
  const img = new Image();
  img.onload = () => { badgeImages[url] = img; badgeLoading[url] = false; draw(); };
  img.onerror = () => { badgeLoading[url] = false; };
  img.src = url;
  return null;
}

function mapRect(cssW, cssH, img) {
  const pad = 8;
  const availW = cssW - pad * 2;
  const availH = cssH - pad * 2;
  let aspect = 1;
  if (img && img.naturalWidth > 0)
    aspect = img.naturalWidth / img.naturalHeight;
  let w = availW, h = w / aspect;
  if (h > availH) { h = availH; w = h * aspect; }
  return { x: (cssW - w) / 2, y: (cssH - h) / 2, w, h };
}

function draw() {
  const cssW = cv.clientWidth;
  const cssH = cv.clientHeight;
  ctx.clearRect(0, 0, cssW, cssH);

  const mapUrl = (state && state.mapImageUrl) || currentMapUrl || DEFAULT_MAP;
  if (mapUrl) currentMapUrl = mapUrl;
  const img = ensureMap(currentMapUrl);
  const m = mapRect(cssW, cssH, img);

  ctx.save();
  roundRect(m.x, m.y, m.w, m.h, 10);
  ctx.clip();

  if (img) {
    ctx.drawImage(img, m.x, m.y, m.w, m.h);
    // 羊皮纸底图略压暗，点位更清楚
    ctx.fillStyle = 'rgba(40,28,12,0.10)';
    ctx.fillRect(m.x, m.y, m.w, m.h);
  } else {
    ctx.fillStyle = '#c9b896';
    ctx.fillRect(m.x, m.y, m.w, m.h);
    ctx.strokeStyle = 'rgba(255,255,255,0.05)';
    for (let i = 1; i < 4; i++) {
      const tx = m.x + m.w * i / 4;
      const ty = m.y + m.h * i / 4;
      ctx.beginPath();
      ctx.moveTo(tx, m.y); ctx.lineTo(tx, m.y + m.h);
      ctx.moveTo(m.x, ty); ctx.lineTo(m.x + m.w, ty);
      ctx.stroke();
    }
  }
  ctx.restore();

  ctx.strokeStyle = '#2c3442';
  ctx.lineWidth = 1;
  roundRect(m.x, m.y, m.w, m.h, 10);
  ctx.stroke();

  points = [];
  if (!state || !state.active) {
    if (!img) {
      ctx.fillStyle = 'rgba(0,0,0,0.45)';
      ctx.fillRect(0, cssH/2 - 22, cssW, 44);
      ctx.fillStyle = '#d7dde6';
      ctx.font = '14px sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const msg = state && state.inFrontline === false ? '不在纷争前线' : '等待前线数据…';
      ctx.fillText(msg, cssW/2, cssH/2);
    }
    return;
  }

  const all = [];
  if (state.self) all.push(state.self);
  if (state.players) all.push(...state.players);
  const showNames = showNamesEl.checked;
  const baseR = parseFloat(dotSizeEl.value) || 3;
  const outline = Math.max(0, parseFloat(outlineSizeEl.value) || 0);

  // 阵营出生点徽章（画在玩家点之下）
  drawSpawnBases(m);

  for (const p of all) {
    const c = applyCal(p.x, p.y);
    const px = m.x + clamp01(c.x) * m.w;
    const py = m.y + clamp01(c.y) * m.h;
    const isAlly = !!(p.isAlly || p.isSelf || (state.ownGc && p.gc === state.ownGc));
    const color = isAlly ? COLORS.ally : (COLORS[p.gc] || COLORS.ally);
    const r = p.isSelf ? baseR + 1.2 : baseR;
    const hitR = Math.max(r + outline + 2, 6);

    if (outline > 0) {
      ctx.beginPath();
      ctx.fillStyle = 'rgba(0,0,0,0.72)';
      ctx.arc(px, py, r + outline, 0, Math.PI * 2);
      ctx.fill();
    }

    ctx.beginPath();
    ctx.fillStyle = color;
    ctx.arc(px, py, r, 0, Math.PI * 2);
    ctx.fill();

    if (outline > 0) {
      ctx.beginPath();
      ctx.strokeStyle = 'rgba(0,0,0,0.55)';
      ctx.lineWidth = Math.max(0.75, outline * 0.55);
      ctx.arc(px, py, r, 0, Math.PI * 2);
      ctx.stroke();
    }

    if (p.isSelf) {
      const ring = r + Math.max(outline + 0.8, 1.5);
      ctx.beginPath();
      ctx.strokeStyle = '#1a1208';
      ctx.lineWidth = Math.max(1, outline > 0 ? outline * 0.7 : 1.2);
      ctx.arc(px, py, ring, 0, Math.PI * 2);
      ctx.stroke();
      ctx.beginPath();
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = Math.max(0.8, outline > 0 ? outline * 0.45 : 1);
      ctx.arc(px, py, ring, 0, Math.PI * 2);
      ctx.stroke();
      drawSelfFacingArrow(px, py, r, outline, m, p.rot);
    }

    if (showNames) {
      const label = p.job || p.jobAbbr || '';
      if (label) {
        const fs = Math.max(9, Math.min(12, 8 + baseR));
        ctx.font = `bold ${fs}px sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';
        ctx.lineWidth = 3;
        ctx.strokeStyle = 'rgba(255,248,230,0.92)';
        ctx.strokeText(label, px, py - r - 2);
        ctx.fillStyle = '#1a1208';
        ctx.fillText(label, px, py - r - 2);
      }
    }

    points.push({ p, px, py, r: hitR });
  }

  // 场景标记（A/B/C/D/1/2/3/4）画在玩家点之上
  if (state.markers && state.markers.length) {
    const markR = Math.max(4, parseFloat(markerSizeEl.value) || 12);
    for (const mk of state.markers) {
      const c = applyCal(mk.x, mk.y);
      const px = m.x + clamp01(c.x) * m.w;
      const py = m.y + clamp01(c.y) * m.h;
      const color = MARKER_COLORS[mk.index] || '#f0f4f8';
      drawFieldMarker(px, py, markR, mk.label || String(mk.index + 1), color);
      points.push({
        p: {
          name: '场景标记 ' + (mk.label || ''),
          job: '标记',
          role: '',
          gcName: '',
          roleAction: '',
          isMarker: true,
        },
        px, py, r: markR + 2,
      });
    }
  }
}

function drawSpawnBases(m) {
  if (!state || !state.bases || !state.bases.length) return;
  const t = Date.now() / 1000;
  // 己方最后画，压在其他徽章之上
  const ordered = state.bases.slice().sort((a, b) => (a.isOwn ? 1 : 0) - (b.isOwn ? 1 : 0));
  for (const b of ordered) {
    const url = BADGE_URL[b.gc];
    const img = ensureBadge(url);
    const c = applyCal(b.x, b.y);
    const px = m.x + clamp01(c.x) * m.w;
    const py = m.y + clamp01(c.y) * m.h;
    const own = !!b.isOwn;
    const size = own ? Math.min(m.w, m.h) * 0.078 : Math.min(m.w, m.h) * 0.052;
    const half = size / 2;

    if (own) {
      const pulse = 0.55 + 0.45 * Math.sin(t * 3.2);
      const ringR = half + 6 + pulse * 5;
      ctx.beginPath();
      ctx.arc(px, py, ringR + 3, 0, Math.PI * 2);
      ctx.strokeStyle = `rgba(255, 214, 64, ${0.35 + pulse * 0.35})`;
      ctx.lineWidth = 3 + pulse * 2;
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(px, py, ringR, 0, Math.PI * 2);
      ctx.strokeStyle = `rgba(255, 255, 255, ${0.55 + pulse * 0.35})`;
      ctx.lineWidth = 2;
      ctx.stroke();

      // 柔光底
      const grd = ctx.createRadialGradient(px, py, half * 0.2, px, py, half + 14);
      grd.addColorStop(0, 'rgba(255,220,80,0.45)');
      grd.addColorStop(1, 'rgba(255,220,80,0)');
      ctx.fillStyle = grd;
      ctx.beginPath();
      ctx.arc(px, py, half + 14, 0, Math.PI * 2);
      ctx.fill();
    } else {
      ctx.beginPath();
      ctx.arc(px, py, half + 3, 0, Math.PI * 2);
      ctx.fillStyle = 'rgba(12,14,18,0.45)';
      ctx.fill();
    }

    if (img && img.complete && img.naturalWidth > 0) {
      ctx.save();
      ctx.shadowColor = own ? 'rgba(255,210,60,0.85)' : 'rgba(0,0,0,0.55)';
      ctx.shadowBlur = own ? 12 : 4;
      ctx.drawImage(img, px - half, py - half, size, size);
      ctx.restore();
    } else {
      // 图片未加载时用色块+文字兜底
      const color = COLORS[b.gc] || '#9aa3ad';
      ctx.beginPath();
      ctx.fillStyle = color;
      ctx.arc(px, py, half * 0.85, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = '#1a1208';
      ctx.font = `bold ${Math.max(10, Math.round(half * 0.7))}px sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const short = b.gc === 'maelstrom' ? '涡' : b.gc === 'twinAdder' ? '蛇' : b.gc === 'flames' ? '辉' : '?';
      ctx.fillText(short, px, py + 0.5);
    }

    points.push({
      p: {
        name: (own ? '己方出生点 · ' : '出生点 · ') + (b.gcName || ''),
        job: '阵营基地',
        role: '',
        gcName: b.gcName || '',
        roleAction: '',
        isMarker: true,
      },
      px, py, r: half + (own ? 10 : 4),
    });
  }
}

function facingUnit(rot, m) {
  // FFXIV Rotation：0 朝 +Z（南）。地图 X←世界 X，Y←世界 Z；flipY 时反转屏幕 Y。
  let dx = Math.sin(rot);
  let dy = Math.cos(rot);
  if (cal.flipY) dy = -dy;
  // 按地图像素比例校正角度（校准缩放 × 显示矩形）
  dx *= (cal.scaleX || 1) * m.w;
  dy *= (cal.scaleY || 1) * m.h;
  const len = Math.hypot(dx, dy) || 1;
  return { x: dx / len, y: dy / len };
}

/** 方案 A：仅自己 — 圆点前方小三角箭头 */
function drawSelfFacingArrow(px, py, r, outline, m, rot) {
  if (typeof rot !== 'number' || Number.isNaN(rot)) return;
  const u = facingUnit(rot, m);
  const pxp = -u.y, pyp = u.x;
  const tipDist = r + outline + Math.max(5, r * 1.15);
  const baseDist = r + Math.max(0, outline * 0.15);
  const halfW = Math.max(3.2, r * 0.9);
  const tipX = px + u.x * tipDist;
  const tipY = py + u.y * tipDist;
  const b1x = px + u.x * baseDist + pxp * halfW;
  const b1y = py + u.y * baseDist + pyp * halfW;
  const b2x = px + u.x * baseDist - pxp * halfW;
  const b2y = py + u.y * baseDist - pyp * halfW;

  ctx.beginPath();
  ctx.moveTo(tipX, tipY);
  ctx.lineTo(b1x, b1y);
  ctx.lineTo(b2x, b2y);
  ctx.closePath();
  ctx.fillStyle = '#1a1208';
  ctx.fill();

  // 内层白三角，略小，提高对比
  const inset = 0.78;
  const midX = (tipX + b1x + b2x) / 3;
  const midY = (tipY + b1y + b2y) / 3;
  ctx.beginPath();
  ctx.moveTo(midX + (tipX - midX) * inset, midY + (tipY - midY) * inset);
  ctx.lineTo(midX + (b1x - midX) * inset, midY + (b1y - midY) * inset);
  ctx.lineTo(midX + (b2x - midX) * inset, midY + (b2y - midY) * inset);
  ctx.closePath();
  ctx.fillStyle = '#ffffff';
  ctx.fill();
}

function drawFieldMarker(px, py, r, label, color) {
  // 菱形底 + 描边，贴近游戏大地图场景标记观感
  ctx.save();
  ctx.beginPath();
  ctx.moveTo(px, py - r);
  ctx.lineTo(px + r * 0.82, py);
  ctx.lineTo(px, py + r);
  ctx.lineTo(px - r * 0.82, py);
  ctx.closePath();
  ctx.fillStyle = 'rgba(12,14,18,0.72)';
  ctx.fill();
  ctx.lineWidth = Math.max(1.5, r * 0.18);
  ctx.strokeStyle = color;
  ctx.stroke();

  ctx.beginPath();
  ctx.moveTo(px, py - r * 0.72);
  ctx.lineTo(px + r * 0.58, py);
  ctx.lineTo(px, py + r * 0.72);
  ctx.lineTo(px - r * 0.58, py);
  ctx.closePath();
  ctx.fillStyle = color;
  ctx.globalAlpha = 0.92;
  ctx.fill();
  ctx.globalAlpha = 1;

  ctx.font = `bold ${Math.max(10, Math.round(r * 1.05))}px sans-serif`;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.lineWidth = 3;
  ctx.strokeStyle = 'rgba(10,12,16,0.85)';
  ctx.strokeText(label, px, py + 0.5);
  ctx.fillStyle = '#fff';
  ctx.fillText(label, px, py + 0.5);
  ctx.restore();
}

function clamp01(v) {
  if (typeof v !== 'number' || Number.isNaN(v)) return 0.5;
  return Math.max(0, Math.min(1, v));
}

function roundRect(x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

function setTip(ev) {
  const rect = cv.getBoundingClientRect();
  const x = ev.clientX - rect.left;
  const y = ev.clientY - rect.top;
  let hit = null;
  for (let i = points.length - 1; i >= 0; i--) {
    const pt = points[i];
    const dx = x - pt.px, dy = y - pt.py;
    if (dx*dx + dy*dy <= pt.r * pt.r) { hit = pt.p; break; }
  }
  if (!hit) { tip.style.display = 'none'; return; }
  tip.style.display = 'block';
  tip.style.left = Math.min(x + 12, rect.width - 200) + 'px';
  tip.style.top = Math.min(y + 12, rect.height - 90) + 'px';
  if (hit.isMarker) {
    tip.innerHTML = `<b>${esc(hit.name)}</b>`;
    return;
  }
  const job = hit.job || hit.jobAbbr || '';
  const ra = hit.roleAction ? `<br/>职能技能：${esc(hit.roleAction)}` : '';
  tip.innerHTML = `<b>${esc(hit.name)}</b><br/>${esc(job)} · ${esc(hit.role || '')}<br/>${esc(hit.gcName || '')}${ra}`;
}

function esc(s) {
  return String(s || '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

cv.addEventListener('pointermove', setTip);
cv.addEventListener('pointerleave', () => tip.style.display = 'none');

async function tick() {
  try {
    const res = await fetch('/api/state?_=' + Date.now(), { cache: 'no-store' });
    if (!res.ok) throw new Error('HTTP ' + res.status);
    state = await res.json();
    document.getElementById('status').textContent = '已连接';
    document.getElementById('status').className = 'pill on';
    document.getElementById('zone').textContent = state.territoryName || ('Territory ' + (state.territoryId || '—'));
    document.getElementById('counts').textContent =
      `${state.loadedCount || 0} 在场 · 名册 ${state.rosterCount || 0}` +
      (state.markers && state.markers.length ? ` · 标记 ${state.markers.length}` : '');
    document.getElementById('own').textContent = '本阵营 ' + (state.ownGcName || '未知');
    syncCalFromState();
    draw();
  } catch (e) {
    document.getElementById('status').textContent = '断开';
    document.getElementById('status').className = 'pill off';
  }
}

writeCalToUi();
resize();

async function pollLoop() {
  await tick();
  setTimeout(pollLoop, 16);
}
pollLoop();
</script>
</body>
</html>
""";
}

