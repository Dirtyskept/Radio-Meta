using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// Loopback-only FFmpeg relay for explicitly opted-in .strm files.
/// Each request owns its FFmpeg process; disconnects/errors stop that process.
/// The current output path targets MP3-compatible sources; other formats require testing.
/// </summary>
internal sealed class RadioMetaProxyService : IDisposable
{
    private const int MaxHeaderBytes = 8192;
    internal const int ProxyPort = 18741;
    internal const int MaxConcurrentClients = 8;
    internal const int MaxConcurrentFfmpegProcesses = 4;
    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients, MaxConcurrentClients);
    private readonly SemaphoreSlim _ffmpegSlots = new(MaxConcurrentFfmpegProcesses, MaxConcurrentFfmpegProcesses);
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private readonly ConcurrentDictionary<string, CachedTokenConfig> _tokenConfigCache = new(StringComparer.OrdinalIgnoreCase);
    private long _nextClientTaskId;
    private readonly string _stationFolder;
    private readonly string _ffmpegPath;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptTask;
    private int _disposed;
    private int _activeFfmpegProcesses;
    private long _totalClientConnections;
    private long _ffmpegStartAttempts;
    private long _ffmpegStartedProcesses;
    private long _successfulAudioResponses;
    private long _ffmpegStartupFailures;

    internal int Port { get; }
    internal string BaseUrl => "http://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal bool IsRunning
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0 || _acceptTask is null || _acceptTask.IsCompleted) return false;
            try { return _listener.Server.IsBound; }
            catch { return false; }
        }
    }
    internal bool FfmpegExecutableAvailable => IsExecutableAvailable(_ffmpegPath);
    internal int ActiveClientCount
    {
        get
        {
            try { return Math.Clamp(MaxConcurrentClients - _clientSlots.CurrentCount, 0, MaxConcurrentClients); }
            catch { return 0; }
        }
    }
    internal int ActiveFfmpegProcessCount => Math.Clamp(Volatile.Read(ref _activeFfmpegProcesses), 0, MaxConcurrentFfmpegProcesses);
    internal long TotalClientConnections => Interlocked.Read(ref _totalClientConnections);
    internal long FfmpegStartAttempts => Interlocked.Read(ref _ffmpegStartAttempts);
    internal long FfmpegStartedProcesses => Interlocked.Read(ref _ffmpegStartedProcesses);
    internal long SuccessfulAudioResponses => Interlocked.Read(ref _successfulAudioResponses);
    internal long FfmpegStartupFailures => Interlocked.Read(ref _ffmpegStartupFailures);

    internal RadioMetaProxyService(string stationFolder, string ffmpegPath, ILogger logger)
    {
        _stationFolder = Path.GetFullPath(stationFolder);
        _ffmpegPath = ffmpegPath;
        _logger = logger;
        _listener = new TcpListener(IPAddress.Loopback, ProxyPort);
        _listener.Start(16);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = Task.Run(AcceptLoopAsync);
        _logger.Info("Radio Meta proxy listener started on loopback port {0}.", Port);
    }

    internal static string SidecarPath(string stationFilePath) => Path.ChangeExtension(stationFilePath, ".radiometa-proxy.json");

    internal static bool TryGetUpstreamUrl(string stationFilePath, out string upstreamUrl)
    {
        upstreamUrl = string.Empty;
        try
        {
            var sidecar = SidecarPath(stationFilePath);
            if (!File.Exists(sidecar)) return false;
            if (!TryReadProxyConfig(sidecar, out var data)) return false;
            if (data == null || string.IsNullOrWhiteSpace(data.Token) || string.IsNullOrWhiteSpace(data.UpstreamUrl)) return false;
            if (data.UpstreamUrl.Length > 8192 || !Uri.TryCreate(data.UpstreamUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
            upstreamUrl = uri.ToString();
            return true;
        }
        catch { return false; }
    }

    internal static bool TryGetProxyDetails(string stationFilePath, out string upstreamUrl, out string token)
    {
        upstreamUrl = string.Empty; token = string.Empty;
        try
        {
            var sidecar = SidecarPath(stationFilePath);
            if (!File.Exists(sidecar)) return false;
            if (!TryReadProxyConfig(sidecar, out var data)) return false;
            if (data == null || string.IsNullOrWhiteSpace(data.Token) || string.IsNullOrWhiteSpace(data.UpstreamUrl)) return false;
            if (data.UpstreamUrl.Length > 8192 || !Uri.TryCreate(data.UpstreamUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
            upstreamUrl = uri.ToString(); token = data.Token; return true;
        }
        catch { return false; }
    }

    internal static void WriteProxySidecar(string stationFilePath, string upstreamUrl, string token)
    {
        if (token.Length != 32 || token.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Proxy tokens must be 32 hexadecimal characters.", nameof(token));
        if (string.IsNullOrWhiteSpace(upstreamUrl) || upstreamUrl.Length > 8192 ||
            !Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var upstreamUri) ||
            (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Proxy upstream URLs must use HTTP or HTTPS.", nameof(upstreamUrl));

        var config = new ProxyStationConfig { Token = token, UpstreamUrl = upstreamUri.ToString() };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        var path = SidecarPath(stationFilePath);
        var createdByThisCall = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            createdByThisCall = true;
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            writer.WriteLine(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            // If CreateNew failed because an older sidecar existed, never remove that file.
            if (createdByThisCall) { try { File.Delete(path); } catch { } }
            throw;
        }
    }

    internal static string ResolveFfmpegPath(string? ffprobePath)
    {
        var candidates = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(ffprobePath))
        {
            try { candidates.Add(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ffprobePath))!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")); } catch { }
        }
        try { candidates.Add(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")); } catch { }
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate)) return candidate;
        // A bare executable name intentionally permits PATH resolution by Process.Start.
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    /// <summary>Checks availability without launching the executable or exposing its path in diagnostics.</summary>
    internal static bool IsExecutableAvailable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            var candidate = Environment.ExpandEnvironmentVariables(executable.Trim());
            if (Path.IsPathRooted(candidate) || candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
                return File.Exists(candidate);

            var names = OperatingSystem.IsWindows()
                ? (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new[] { candidate } : new[] { candidate, candidate + ".exe" })
                : new[] { candidate };
            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in names)
                {
                    try { if (File.Exists(Path.Combine(directory.Trim(), name))) return true; }
                    catch { }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Confirms that the bounded .strm content points at this plugin's loopback proxy endpoint and expected token.</summary>
    internal static bool IsProxyStationFile(string stationFilePath, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)) return false;
        try
        {
            if (!RadioStationFileStore.TryReadCappedFile(stationFilePath, 8192, out var bytes)) return false;
            using var input = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line) || line.Length > 8192 ||
                !Uri.TryCreate(line.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback || uri.Port != ProxyPort ||
                !uri.AbsolutePath.StartsWith("/proxy/", StringComparison.Ordinal) ||
                uri.AbsolutePath.Length != "/proxy/".Length + 32) return false;
            var fileToken = uri.AbsolutePath.Substring("/proxy/".Length);
            return string.Equals(fileToken, expectedToken, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool TryGetConfigForToken(string stationFolder, string token, out string upstreamUrl)
    {
        upstreamUrl = string.Empty;
        if (token.Length != 32 || token.Any(c => !Uri.IsHexDigit(c))) return false;

        if (_tokenConfigCache.TryGetValue(token, out var cached))
        {
            try
            {
                if (File.Exists(cached.SidecarPath) &&
                    File.GetLastWriteTimeUtc(cached.SidecarPath) == cached.LastWriteUtc &&
                    new FileInfo(cached.SidecarPath).Length == cached.Length)
                {
                    upstreamUrl = cached.UpstreamUrl;
                    return true;
                }
            }
            catch { }
            _tokenConfigCache.TryRemove(token, out _);
        }

        try
        {
            foreach (var sidecar in Directory.EnumerateFiles(stationFolder, "*.radiometa-proxy.json", SearchOption.TopDirectoryOnly))
            {
                if (!TryReadProxyConfig(sidecar, out var config)) continue;
                if (!string.Equals(config.Token, token, StringComparison.OrdinalIgnoreCase)) continue;
                if (config.UpstreamUrl.Length > 8192 || !Uri.TryCreate(config.UpstreamUrl, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
                upstreamUrl = uri.ToString();
                try
                {
                    if (_tokenConfigCache.Count > 512) _tokenConfigCache.Clear();
                    _tokenConfigCache[token] = new CachedTokenConfig(sidecar, File.GetLastWriteTimeUtc(sidecar), new FileInfo(sidecar).Length, upstreamUrl);
                }
                catch { }
                return true;
            }
        }
        catch
        {
            // Fail closed. Do not include sidecar paths, upstream URLs, or tokens in logs.
        }
        return false;
    }

    private static bool TryReadProxyConfig(string sidecarPath, out ProxyStationConfig config)
    {
        config = null!;
        try
        {
            if (!RadioStationFileStore.TryReadCappedFile(sidecarPath, 16 * 1024, out var sidecarBytes)) return false;
            var parsed = JsonSerializer.Deserialize<ProxyStationConfig>(Encoding.UTF8.GetString(sidecarBytes));
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Token) || parsed.Token.Length != 32 ||
                parsed.Token.Any(c => !Uri.IsHexDigit(c)) || string.IsNullOrWhiteSpace(parsed.UpstreamUrl) || parsed.UpstreamUrl.Length > 8192 ||
                !Uri.TryCreate(parsed.UpstreamUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return false;
            config = parsed;
            return true;
        }
        catch { return false; }
    }

    private sealed record CachedTokenConfig(string SidecarPath, DateTime LastWriteUtc, long Length, string UpstreamUrl);

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { if (!_shutdown.IsCancellationRequested) _logger.Warn("Radio Meta proxy accept failed ({0}).", ex.GetType().Name); continue; }
            if (!_clientSlots.Wait(0))
            {
                _logger.Warn("Radio Meta proxy rejected a connection because the client limit was reached.");
                client.Dispose();
                continue;
            }

            var taskId = Interlocked.Increment(ref _nextClientTaskId);
            var task = Task.Run(() => HandleAcceptedClientAsync(client, _shutdown.Token));
            _clientTasks[taskId] = task;
            _ = task.ContinueWith(
                _ => _clientTasks.TryRemove(taskId, out var ignored),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            // Close the tiny completion-before-registration race: the continuation may
            // otherwise run before this dictionary entry is inserted.
            if (task.IsCompleted) _clientTasks.TryRemove(taskId, out _);
        }
    }

    private async Task HandleAcceptedClientAsync(TcpClient client, CancellationToken shutdownToken)
    {
        try { await HandleClientAsync(client, shutdownToken).ConfigureAwait(false); }
        finally { _clientSlots.Release(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken shutdownToken)
    {
        Interlocked.Increment(ref _totalClientConnections);
        using (client)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
        {
            var stream = client.GetStream();
            string requestLine;
            try
            {
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                headerTimeout.CancelAfter(HeaderReadTimeout);
                using var header = new MemoryStream();
                var one = new byte[1];
                var headerComplete = false;
                while (header.Length < MaxHeaderBytes)
                {
                    var count = await stream.ReadAsync(one.AsMemory(0, 1), headerTimeout.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    header.WriteByte(one[0]);
                    var b = header.GetBuffer(); var len = (int)header.Length;
                    if (len >= 4 && b[len - 4] == 13 && b[len - 3] == 10 && b[len - 2] == 13 && b[len - 1] == 10)
                    { headerComplete = true; break; }
                }
                if (!headerComplete)
                {
                    try { await WriteResponseAsync(stream, "431 Request Header Fields Too Large", "text/plain", "Request headers were too large.\n", linked.Token).ConfigureAwait(false); } catch { }
                    return;
                }
                requestLine = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            }
            catch { return; }

            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD"))
            {
                await WriteResponseAsync(stream, "405 Method Not Allowed", "text/plain", "Only GET and HEAD are supported.\n", linked.Token).ConfigureAwait(false); return;
            }
            var path = parts[1].Split('?')[0];
            var prefix = "/proxy/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length != prefix.Length + 32)
            {
                await WriteResponseAsync(stream, "404 Not Found", "text/plain", "Not found.\n", linked.Token).ConfigureAwait(false); return;
            }
            var token = path.Substring(prefix.Length);
            if (!TryGetConfigForToken(_stationFolder, token, out var upstream))
            {
                await WriteResponseAsync(stream, "404 Not Found", "text/plain", "Unknown proxy station.\n", linked.Token).ConfigureAwait(false); return;
            }
            if (parts[0] == "HEAD")
            {
                await WriteResponseAsync(stream, "200 OK", "audio/mpeg", string.Empty, linked.Token).ConfigureAwait(false); return;
            }
            if (string.IsNullOrWhiteSpace(_ffmpegPath) || (!File.Exists(_ffmpegPath) && Path.IsPathRooted(_ffmpegPath)))
            {
                await WriteResponseAsync(stream, "503 Service Unavailable", "text/plain", "FFmpeg executable unavailable. Check Radio Meta proxy diagnostics.\n", linked.Token).ConfigureAwait(false); return;
            }

            if (!_ffmpegSlots.Wait(0))
            {
                await WriteResponseAsync(stream, "503 Service Unavailable", "text/plain", "The Radio Meta proxy is busy. Try again shortly.\n", linked.Token).ConfigureAwait(false);
                return;
            }

            Process? process = null;
            var responseStarted = false;
            var ffmpegSlotHeld = true;
            var ffmpegStartAttempted = false;
            var ffmpegCountedActive = false;
            var stderrLineCount = 0;
            Task? stderrTask = null;
            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };
                var args = process.StartInfo.ArgumentList;
                args.Add("-hide_banner"); args.Add("-loglevel"); args.Add("warning"); args.Add("-nostdin");
                args.Add("-reconnect"); args.Add("1"); args.Add("-reconnect_streamed"); args.Add("1"); args.Add("-reconnect_delay_max"); args.Add("5");
                args.Add("-i"); args.Add(upstream); args.Add("-vn"); args.Add("-c:a"); args.Add("copy"); args.Add("-f"); args.Add("mp3"); args.Add("pipe:1");
                ffmpegStartAttempted = true;
                Interlocked.Increment(ref _ffmpegStartAttempts);
                if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
                Interlocked.Increment(ref _ffmpegStartedProcesses);
                Interlocked.Increment(ref _activeFfmpegProcesses);
                ffmpegCountedActive = true;
                stderrTask = Task.Run(async () =>
                {
                    // Drain stderr to avoid pipe backpressure, but never write raw lines to logs:
                    // FFmpeg diagnostics can contain credential-bearing upstream URLs.
                    try { while (await process.StandardError.ReadLineAsync(linked.Token).ConfigureAwait(false) is not null) Interlocked.Increment(ref stderrLineCount); } catch { }
                });
                var buffer = new byte[32768];
                var output = process.StandardOutput.BaseStream;
                int read;
                using (var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
                {
                    startupTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    try { read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), startupTimeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                    { throw new TimeoutException("FFmpeg did not produce audio before the startup deadline."); }
                }
                if (read == 0) throw new InvalidOperationException("FFmpeg exited before producing audio.");

                // Do not commit the HTTP 200 response until FFmpeg has actually produced bytes.
                // A stalled/invalid upstream can therefore fail clearly instead of leaving a silent client hanging.
                var headerBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nCache-Control: no-store\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n\r\n");
                await stream.WriteAsync(headerBytes, linked.Token).ConfigureAwait(false);
                responseStarted = true;
                await WriteChunkAsync(stream, buffer, read, linked.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _successfulAudioResponses);

                while (!linked.IsCancellationRequested)
                {
                    read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    // Chunked transfer permits indefinite live audio without a known Content-Length.
                    await WriteChunkAsync(stream, buffer, read, linked.Token).ConfigureAwait(false);
                }
                if (process.HasExited && process.ExitCode != 0)
                    _logger.Warn("Radio Meta proxy FFmpeg exited with code {0}; {1} diagnostic line(s) were suppressed to avoid logging upstream URLs or tokens.", process.ExitCode, Volatile.Read(ref stderrLineCount));
                try { if (stderrTask is not null) await stderrTask.ConfigureAwait(false); } catch { }
                if (!linked.IsCancellationRequested) await stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { _logger.Debug("Radio Meta proxy client disconnected; stopping its FFmpeg process."); }
            catch (IOException) { _logger.Debug("Radio Meta proxy client connection ended; stopping its FFmpeg process."); }
            catch (Exception ex)
            {
                if (ffmpegStartAttempted && !responseStarted && !linked.IsCancellationRequested)
                    Interlocked.Increment(ref _ffmpegStartupFailures);
                _logger.Warn("Radio Meta proxy request failed ({0}).", ex.GetType().Name);
                if (!responseStarted)
                {
                    try { await WriteResponseAsync(stream, "502 Bad Gateway", "text/plain", "FFmpeg could not start or the upstream stream could not be opened. Check the Emby server log.\n", CancellationToken.None).ConfigureAwait(false); } catch { }
                }
            }
            finally
            {
                try { if (process != null && !process.HasExited) process.Kill(true); } catch { }
                try { if (stderrTask is not null) await stderrTask.ConfigureAwait(false); } catch { }
                try { process?.Dispose(); } catch { }
                if (ffmpegCountedActive) Interlocked.Decrement(ref _activeFfmpegProcesses);
                if (ffmpegSlotHeld) _ffmpegSlots.Release();
            }
        }
    }

    private static async Task WriteChunkAsync(NetworkStream stream, byte[] buffer, int length, CancellationToken token)
    {
        var sizeLine = Encoding.ASCII.GetBytes(length.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
        await stream.WriteAsync(sizeLine, token).ConfigureAwait(false);
        await stream.WriteAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 13, 10 }, token).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, string body, CancellationToken token)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + contentType + "\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        try { _listener.Stop(); } catch { }
        try { _acceptTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { Task.WhenAll(_clientTasks.Values.ToArray()).Wait(TimeSpan.FromSeconds(5)); } catch { }
        if (_clientTasks.IsEmpty)
        {
            _shutdown.Dispose();
            _clientSlots.Dispose();
            _ffmpegSlots.Dispose();
        }
        else
        {
            _logger.Warn("Radio Meta proxy shutdown is still waiting for {0} client task(s); cancellation was requested.", _clientTasks.Count);
        }
        _logger.Info("Radio Meta proxy listener stopped.");
    }

    private sealed class ProxyStationConfig
    {
        public string Token { get; set; } = string.Empty;
        public string UpstreamUrl { get; set; } = string.Empty;
    }
}
