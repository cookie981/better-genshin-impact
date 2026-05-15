using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Service.Interface;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace BetterGenshinImpact.Service;

public class HttpLogServerService : ILogEventSink, IDisposable
{
    private readonly IConfigService _configService;
    private HttpListener _listener;
    private CancellationTokenSource _cts;
    private readonly ConcurrentQueue<string> _logBuffer = new();
    private const int MaxLogLines = 1000;
    private readonly MessageTemplateTextFormatter _formatter;
    private readonly ConcurrentDictionary<HttpListenerResponse, bool> _clients = new();

    public HttpLogServerService(IConfigService configService)
    {
        _configService = configService;
        _formatter = new MessageTemplateTextFormatter("[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}");

        _configService.Get().OtherConfig.HttpLogServerConfig.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(OtherConfig.HttpLogServer.Enabled) ||
                e.PropertyName == nameof(OtherConfig.HttpLogServer.Port) ||
                e.PropertyName == nameof(OtherConfig.HttpLogServer.ListenAddress))
            {
                RestartServer();
            }
        };

        StartServer();
    }

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        var logMessage = writer.ToString().Replace("\r\n", "\n");

        _logBuffer.Enqueue(logMessage);
        while (_logBuffer.Count > MaxLogLines)
        {
            _logBuffer.TryDequeue(out _);
        }

        BroadcastLog(logMessage);
    }

    private void BroadcastLog(string rawText)
    {
        // 每行都加 data: 前缀，SSE 协议要求
        var lines = rawText.Split('\n');
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            sb.Append("data: ").Append(l).Append('\n');
        }
        sb.Append('\n'); // 事件结束
        var buffer = Encoding.UTF8.GetBytes(sb.ToString());

        var clients = _clients.Keys.ToList();
        foreach (var client in clients)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    await client.OutputStream.FlushAsync();
                }
                catch
                {
                    _clients.TryRemove(client, out _);
                }
            });
        }
    }

    private void StartServer()
    {
        var config = _configService.Get().OtherConfig.HttpLogServerConfig;
        if (!config.Enabled) return;

        if (config.Port is < 1 or > 65535)
        {
            Log.Error("Invalid port: {Port}. Expected range: 1-65535.", config.Port);
            return;
        }

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();

        var address = string.IsNullOrWhiteSpace(config.ListenAddress) ? "0.0.0.0" : config.ListenAddress;
        var prefix = address == "0.0.0.0" ? "+" : address;

        if (address != "0.0.0.0" && address != "localhost" && address != "127.0.0.1" && !IPAddress.TryParse(address, out _))
        {
            Log.Error("Invalid listen address: {Address}. Using 0.0.0.0 instead.", address);
            prefix = "+";
            address = "0.0.0.0";
        }

        try
        {
            _listener.Prefixes.Add($"http://{prefix}:{config.Port}/");
            _listener.Start();
            Task.Run(() => ListenAsync(_cts.Token));
            Log.Information("日志服务器已启动 {Address}:{Port}", address, config.Port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动日志服务器失败。请检查端口是否被占用，或是否需要 0.0.0.0 的管理员权限。");
        }
    }

    private void StopServer()
    {
        _cts?.Cancel();
        if (_listener != null && _listener.IsListening)
        {
            _listener.Stop();
            _listener.Close();
        }

        foreach (var client in _clients.Keys)
        {
            try { client.Close(); } catch { }
        }
        _clients.Clear();
    }

    private void RestartServer()
    {
        StopServer();
        StartServer();
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context), token);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error accepting HTTP request");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // 对 /logs 和 /screenshot 做可选 token 鉴权
            var path = request.Url.AbsolutePath;
            if (path == "/logs" || path == "/screenshot")
            {
                if (!ValidateAccess(request))
                {
                    response.StatusCode = 403;
                    response.Close();
                    return;
                }
            }

            if (path == "/")
            {
                ServeHtml(response);
            }
            else if (path == "/logs")
            {
                await ServeLogsSseAsync(response);
            }
            else if (path == "/screenshot")
            {
                ServeScreenshot(response);
            }
            else
            {
                response.StatusCode = 404;
                response.Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling HTTP request");
            try { response.StatusCode = 500; response.Close(); } catch { }
        }
    }

    private bool ValidateAccess(HttpListenerRequest request)
    {
        var token = _configService.Get().OtherConfig.HttpLogServerConfig.AccessToken;
        // 未配置 token 则允许所有访问（保持向后兼容）
        if (string.IsNullOrWhiteSpace(token))
            return true;

        // 支持 ?token=xxx 或 Bearer header
        if (request.QueryString["token"] == token)
            return true;

        var authHeader = request.Headers["Authorization"];
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            if (authHeader.Substring(7) == token)
                return true;
        }

        return false;
    }

    private void ServeHtml(HttpListenerResponse response)
    {
        var html = @"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>BetterGI Log Server</title>
    <style>
        body { font-family: Consolas, monospace; background: #1e1e1e; color: #d4d4d4; margin: 0; padding: 10px; display: flex; flex-direction: column; height: 100vh; box-sizing: border-box; }
        #controls { margin-bottom: 10px; }
        button { padding: 5px 15px; background: #0e639c; color: white; border: none; cursor: pointer; }
        button:hover { background: #1177bb; }
        #log-container { flex: 1; overflow-y: auto; background: #252526; padding: 10px; border: 1px solid #333; white-space: pre-wrap; word-wrap: break-word; }
        #screenshot-container { margin-top: 10px; text-align: center; }
        #screenshot { max-width: 100%; max-height: 400px; }
    </style>
</head>
<body>
    <div id='controls'>
        <button onclick='getScreenshot()'>截图</button>
        <label><input type='checkbox' id='auto-scroll' checked> 自动滚动</label>
        <label><input type='checkbox' id='hide-debug' checked> 隐藏调试</label>
        <label>
            <input type='checkbox' id='auto-refresh-ss'> 定时刷新
            <input type='number' id='ss-interval' value='5' min='1' max='60' style='width:40px'>秒
        </label>
        <label><input type='checkbox' id='show-screenshot'> 显示截图</label>
        <button onclick='toggleControls()' style='float:right'>−</button>
    </div>
    <div id='log-container'></div>
    <div id='screenshot-container' style='display:none'>
        <img id='screenshot' alt='Screenshot'/>
    </div>

    <script>
        var logContainer = document.getElementById('log-container');
        var autoScroll = document.getElementById('auto-scroll');
        var hideDebug = document.getElementById('hide-debug');
        var showSs = document.getElementById('show-screenshot');
        var autoRefresh = document.getElementById('auto-refresh-ss');
        var ssInterval = document.getElementById('ss-interval');
        var screenshot = document.getElementById('screenshot');
        var ssTimer = null;

        function isDebug(text) {
            return text.indexOf('[DBG]') !== -1 || text.indexOf('[VRB]') !== -1;
        }
        function getColor(text) {
            if (text.indexOf('[ERR]') !== -1 || text.indexOf('[FTL]') !== -1) return '#ff3333';
            if (text.indexOf('[WRN]') !== -1) return '#ffcc00';
            if (isDebug(text)) return '#808080';
            return '#d4d4d4';
        }

        var evtSource = new EventSource('/logs');
        evtSource.onmessage = function(e) {
            var span = document.createElement('span');
            span.style.color = getColor(e.data);
            span.textContent = e.data + '\n';
            if (isDebug(e.data)) span.classList.add('debug');
            if (hideDebug.checked && isDebug(e.data)) span.style.display = 'none';
            logContainer.appendChild(span);
            if (autoScroll.checked) logContainer.scrollTop = logContainer.scrollHeight;
        };
        hideDebug.onclick = function() {
            var ds = document.querySelectorAll('.debug');
            for (var i = 0; i < ds.length; i++) ds[i].style.display = hideDebug.checked ? 'none' : '';
        };

        showSs.onchange = function() {
            document.getElementById('screenshot-container').style.display = showSs.checked ? '' : 'none';
        };
        function getScreenshot() {
            showSs.checked = true;
            var sc = document.getElementById('screenshot-container');
            sc.style.display = '';
            var img = document.getElementById('screenshot');
            img.src = '/screenshot?t=' + new Date().getTime();
        }
        autoRefresh.onchange = function() {
            clearInterval(ssTimer);
            if (autoRefresh.checked) {
                ssTimer = setInterval(getScreenshot, (parseInt(ssInterval.value) || 5) * 1000);
                getScreenshot();
            }
        };
        ssInterval.onchange = function() {
            if (autoRefresh.checked) {
                clearInterval(ssTimer);
                ssTimer = setInterval(getScreenshot, (parseInt(ssInterval.value) || 5) * 1000);
            }
        };
    </script>
</body>
</html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    private async Task ServeLogsSseAsync(HttpListenerResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");

        _clients.TryAdd(response, true);

        foreach (var log in _logBuffer)
        {
            var lines = log.Split('\n');
            var sb2 = new StringBuilder();
            foreach (var l in lines)
            {
                sb2.Append("data: ").Append(l).Append('\n');
            }
            sb2.Append('\n');
            var buffer = Encoding.UTF8.GetBytes(sb2.ToString());
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
        await response.OutputStream.FlushAsync();
    }

    private void ServeScreenshot(HttpListenerResponse response)
    {
        try
        {
            using var mat = TaskTriggerDispatcher.GlobalGameCapture?.Capture();
            if (mat != null && !mat.Empty())
            {
                var bytes = mat.ImEncode(".jpg");
                response.ContentType = "image/jpeg";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error capturing screenshot for HTTP server");
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }

    public void Dispose()
    {
        StopServer();
    }
}
