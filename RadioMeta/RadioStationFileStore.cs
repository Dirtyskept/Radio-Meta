using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RadioMeta;

/// <summary>
/// Owns station-folder file operations shared by the station-management endpoints.
/// Request validation, API response construction, and Emby library-scan resolution remain in their existing services.
/// </summary>
internal static class RadioStationFileStore
{
    private const int MaxArtworkBytes = 5 * 1024 * 1024;

    internal static bool IsArtworkExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindMatchingArtwork(string stationFilePath)
    {
        var directory = Path.GetDirectoryName(stationFilePath)!;
        var baseName = Path.GetFileNameWithoutExtension(stationFilePath);
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(candidate => IsArtworkExtension(candidate) &&
                string.Equals(Path.GetFileNameWithoutExtension(candidate), baseName, StringComparison.OrdinalIgnoreCase));
    }

    internal static (string StreamUrl, string Status) ReadStationStreamFile(string fullPath)
    {
        try
        {
            if (new FileInfo(fullPath).Length > 64 * 1024) return (string.Empty, "Station file exceeds the 64 KB safety limit");
            using var reader = new StreamReader(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (!reader.EndOfStream)
            {
                var line = (reader.ReadLine() ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (line.Length > 8192) return (string.Empty, "Station URL exceeds the 8,192-character safety limit");

                if (RadioMetaProxyService.TryGetUpstreamUrl(fullPath, out var upstream)) line = upstream;
                var status = Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                    RadioMetaUriSchemeHelpers.IsHttpHttpsOrIcy(uri)
                    ? "Readable"
                    : "Read; URL format unverified";
                return (line, status);
            }

            return (string.Empty, "No stream URL found");
        }
        catch (Exception ex)
        {
            return (string.Empty, "Unreadable (" + ex.GetType().Name + ")");
        }
    }

    internal static string ReadArtworkDataUri(string artworkPath, long remainingBudgetBytes, out long bytesIncluded)
    {
        bytesIncluded = 0;
        // The scan remains successful if optional dashboard artwork cannot be read.
        // Check the file length before allocating and honor the total-response budget.
        try
        {
            var extension = Path.GetExtension(artworkPath);
            var mime = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            var length = new FileInfo(artworkPath).Length;
            if (length <= 0 || length > MaxArtworkBytes || length > remainingBudgetBytes)
                return string.Empty;
            if (!TryReadCappedFile(artworkPath, Math.Min(MaxArtworkBytes, remainingBudgetBytes), out var bytes))
                return string.Empty;
            bytesIncluded = bytes.LongLength;
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            // Keep scan successful if artwork cannot be read.
        }

        return string.Empty;
    }

    internal static bool TryReadCappedFile(string path, long maximumBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue) return false;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length <= 0 || input.Length > maximumBytes) return false;
            using var output = new MemoryStream((int)input.Length);
            var buffer = new byte[81920];
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (output.Length + read > maximumBytes) return false;
                output.Write(buffer, 0, read);
            }
            if (output.Length <= 0) return false;
            bytes = output.ToArray();
            return bytes.LongLength <= maximumBytes;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return false;
        }
    }

    internal static void WriteNewStationStreamFile(string path, string streamUrl)
    {
        // CreateNew prevents overwriting an existing station if another process races the preflight check.
        // Close the file before asking Emby to scan so the scanner sees a complete .strm.
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine(streamUrl);
            writer.Flush();
        }
    }

    internal static void WriteTemporaryStationStreamFile(string path, string streamUrl)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine(streamUrl);
        }
    }

    internal static void WriteArtworkFile(string path, byte[] bytes, FileShare fileShare)
    {
        var createdByThisCall = false;
        try
        {
            using var imageFile = new FileStream(path, FileMode.CreateNew, FileAccess.Write, fileShare);
            createdByThisCall = true;
            imageFile.Write(bytes, 0, bytes.Length);
            imageFile.Flush(flushToDisk: true);
        }
        catch
        {
            // Never remove a file that existed before this CreateNew attempt.
            if (createdByThisCall) { try { File.Delete(path); } catch { } }
            throw;
        }
    }

    internal static (bool Success, string Error, string ArtworkDeleted, string Warning) DeleteStationAndArtwork(string stationPath)
    {
        if (!File.Exists(stationPath))
            return (false, "The station file no longer exists. Scan the folder again.", string.Empty, string.Empty);

        var logo = FindMatchingArtwork(stationPath);
        try
        {
            File.Delete(stationPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return (false, "Could not delete the station file: " + ex.Message, string.Empty, string.Empty);
        }

        // The station file is gone. A logo-delete problem must not suppress the library scan,
        // because Emby still needs to reconcile the missing .strm.
        var warning = string.Empty;
        var artworkDeleted = string.Empty;
        if (logo != null && File.Exists(logo))
        {
            try
            {
                File.Delete(logo);
                artworkDeleted = logo;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warning = "The station file was deleted, but its matching logo could not be deleted: " + ex.Message;
            }
        }

        return (true, string.Empty, artworkDeleted, warning);
    }

    internal static bool TryCommitEditedStationFiles(
        string oldPath,
        string newPath,
        string streamUrl,
        string? artwork,
        string? targetArtwork,
        string downloadedArtworkPath,
        string downloadedArtworkExtension,
        out string error)
    {
        var newBase = Path.GetFileNameWithoutExtension(newPath);
        var directory = Path.GetDirectoryName(oldPath)!;
        var tempPath = oldPath + ".edit-tmp";
        try
        {
            WriteTemporaryStationStreamFile(tempPath, streamUrl);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                File.Move(oldPath, newPath);
            try { File.Replace(tempPath, newPath, null); }
            catch (PlatformNotSupportedException) { File.Delete(newPath); File.Move(tempPath, newPath); }
            catch (IOException) { File.Delete(newPath); File.Move(tempPath, newPath); }

            if (artwork is not null && targetArtwork is not null &&
                !string.Equals(artwork, targetArtwork, StringComparison.OrdinalIgnoreCase))
                File.Move(artwork, targetArtwork);

            if (downloadedArtworkPath.Length > 0)
            {
                var finalArtwork = Path.Combine(directory, newBase + downloadedArtworkExtension);
                if (File.Exists(finalArtwork)) File.Delete(finalArtwork);
                File.Move(downloadedArtworkPath, finalArtwork);
                if (artwork is not null && File.Exists(artwork) &&
                    !string.Equals(artwork, finalArtwork, StringComparison.OrdinalIgnoreCase))
                    File.Delete(artwork);
                if (targetArtwork is not null && File.Exists(targetArtwork) &&
                    !string.Equals(targetArtwork, finalArtwork, StringComparison.OrdinalIgnoreCase))
                    File.Delete(targetArtwork);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            error = ex.Message;
            return false;
        }
    }
}
