using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;

namespace RadioMeta;

public sealed partial class RadioMetadataReader
{
    private RadioStationMetadata ReadUncached(
        string stationFilePath,
        string ffprobePath,
        Func<string, string, bool>? ignoreMetadataPhrase)
    {
        var stationName =
            Path.GetFileNameWithoutExtension(stationFilePath);

        var folder = Path.GetDirectoryName(stationFilePath)
            ?? string.Empty;

        try
        {
            var stationInfo = new FileInfo(stationFilePath);
            if (!stationInfo.Exists || stationInfo.Length > 64 * 1024)
                return FailureForStation(stationName, string.Empty, string.Empty, "Station file is missing or exceeds the 64 KB safety limit.");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return FailureForStation(stationName, string.Empty, string.Empty, "Station file could not be inspected safely.");
        }

        var artwork = FindArtwork(folder, stationName);
        var streamUrl = ReadStreamUrl(stationFilePath);

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return new RadioStationMetadata
            {
                StationName = stationName,
                ArtworkPath = artwork,
                Error = "The station file contains no stream URL."
            };
        }

        var probeTimer = Stopwatch.StartNew();
        var processStarted = false;
        var probeOutcome = "exception";
        try
        {
            _diagnostics?.Record(stationName, "FFprobe", "info", string.Empty, string.Empty, "Starting FFprobe stream metadata probe.");
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-rw_timeout");
            startInfo.ArgumentList.Add("10000000");
            startInfo.ArgumentList.Add("-probesize");
            startInfo.ArgumentList.Add("1000000");
            startInfo.ArgumentList.Add("-analyzeduration");
            startInfo.ArgumentList.Add("3000000");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format=format_name:format_tags");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(streamUrl);

            using var process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
            {
                probeOutcome = "start-failed";
                _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe process could not be started.");
                return FailureForStation(
                    stationName, artwork, streamUrl,
                    "Could not start ffprobe.");
            }

            processStarted = true;
            // Stream child output into bounded buffers. Keep draining after the cap is reached
            // so FFprobe cannot block on a full pipe, but never allocate without a fixed limit.
            var outputTask = ReadTextBoundedAsync(process.StandardOutput, 1024 * 1024);
            var errorTask = ReadTextBoundedAsync(process.StandardError, 64 * 1024);

            if (!process.WaitForExit(
                (int)ProbeTimeout.TotalMilliseconds))
            {
                probeOutcome = "timeout";
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may already have exited.
                }

                try { Task.WhenAll(outputTask, errorTask).Wait(TimeSpan.FromSeconds(1)); } catch { }
                _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe timed out before returning metadata.");
                return FailureForStation(
                    stationName, artwork, streamUrl,
                    "ffprobe timed out.");
            }

            var outputResult = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult(); // bounded stderr drain; never expose URLs/paths from it
            if (outputResult.Truncated)
            {
                probeOutcome = "output-too-large";
                _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe output exceeded the 1 MB safety limit.");
                return FailureForStation(stationName, artwork, string.Empty, "ffprobe metadata output was unexpectedly large.");
            }
            var output = outputResult.Text;

            if (process.ExitCode != 0)
            {
                probeOutcome = "exit-code-" + process.ExitCode;
                _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe returned exit code " + process.ExitCode + ".");
                return FailureForStation(
                    stationName, artwork, streamUrl,
                    "ffprobe could not read stream metadata (exit code " + process.ExitCode + ").");
            }

            using var document = JsonDocument.Parse(output);

            var streamTitle = RadioStreamTitleParser.FindStreamTitle(document.RootElement);
            if (!string.IsNullOrWhiteSpace(streamTitle))
            {
                _diagnostics?.Record(stationName, "FFprobe", "detected", streamTitle, string.Empty, "A title-like metadata value was found in FFprobe output.", streamTitle);
            }
            if (ignoreMetadataPhrase?.Invoke(stationName, streamTitle) == true)
            {
                probeOutcome = "filtered";
                _diagnostics?.Record(stationName, "Ignore Metadata Phrases", "filtered", streamTitle, string.Empty, "FFprobe title matched a configured ignore phrase.", streamTitle);
                return FailureForStation(stationName, artwork, string.Empty, "ffprobe metadata was blocked by an ignore phrase.");
            }
            if (!RadioStreamTitleParser.IsUsableNormalizedStreamTitle(streamTitle))
            {
                probeOutcome = "no-usable-title";
                _diagnostics?.Record(stationName, "FFprobe", "rejected", streamTitle, string.Empty, "FFprobe returned no usable song title.", streamTitle);
                return FailureForStation(
                    stationName, artwork, string.Empty,
                    "ffprobe returned no usable song metadata.");
            }

            var (artist, songTitle) = RadioStreamTitleParser.SplitNormalizedStreamTitle(streamTitle);
            if (string.IsNullOrWhiteSpace(songTitle))
            {
                probeOutcome = "split-rejected";
                _diagnostics?.Record(stationName, "FFprobe", "rejected", streamTitle, artist, "The stream title could not be split into a usable song title.", streamTitle);
                return FailureForStation(
                    stationName, artwork, string.Empty,
                    "ffprobe returned no usable song title.");
            }

            probeOutcome = "success";
            return new RadioStationMetadata
            {
                StationName = stationName,
                ArtworkPath = artwork,
                StreamUrl = streamUrl,
                StreamTitle = streamTitle,
                Artist = artist,
                SongTitle = songTitle
            };
        }
        catch (JsonException)
        {
            probeOutcome = "invalid-json";
            _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "FFprobe output was not valid JSON.");
            return FailureForStation(
                stationName, artwork, streamUrl,
                "ffprobe returned invalid JSON.");
        }
        catch (Exception ex)
        {
            probeOutcome = "exception-" + ex.GetType().Name;
            _diagnostics?.Record(stationName, "FFprobe", "error", string.Empty, string.Empty, "Unable to read stream metadata (" + ex.GetType().Name + ").");
            return FailureForStation(
                stationName, artwork, string.Empty,
                "Unable to read stream metadata (" + ex.GetType().Name + ").");
        }
        finally
        {
            probeTimer.Stop();
            _performance?.RecordFfprobeAttempt(stationName, probeTimer.Elapsed.TotalMilliseconds, processStarted, probeOutcome);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadTextBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        return (builder.ToString(), truncated);
    }

    private static string ReadStreamUrl(string stationFilePath)
    {
        if (RadioMetaProxyService.TryGetUpstreamUrl(stationFilePath, out var proxyUpstream)) return proxyUpstream;
        try
        {
            foreach (var line in File.ReadLines(stationFilePath))
            {
                var value = line.Trim();

                if (value.Length == 0 || value.StartsWith("#"))
                {
                    continue;
                }
                if (value.Length > 8192) return string.Empty;

                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri))
                {
                    return uri.ToString();
                }

                return string.Empty;
            }
        }
        catch
        {
            // The caller will receive an empty-URL fallback.
        }

        return string.Empty;
    }

    private static string FindArtwork(string folder, string stationName)
    {
        string[] extensions = { ".jpg", ".jpeg", ".png", ".webp" };

        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(folder, stationName + extension);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns false for empty metadata, bare URLs, and station status values
    /// such as "Tracklist: https://example.invalid". This prevents status text
    /// from blocking the FFprobe fallback or being written to play history.
    /// </summary>
    public static bool IsUsableStreamTitle(string? streamTitle)
        => RadioStreamTitleParser.IsUsableStreamTitle(streamTitle);


    private static RadioStationMetadata Failure(string message)
    {
        return new RadioStationMetadata { Error = message };
    }

    private static RadioStationMetadata FailureForStation(
        string stationName,
        string artwork,
        string streamUrl,
        string message)
    {
        return new RadioStationMetadata
        {
            StationName = stationName,
            ArtworkPath = artwork,
            StreamUrl = streamUrl,
            Error = message
        };
    }
}
