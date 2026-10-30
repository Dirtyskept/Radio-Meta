using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// One radio track observed during an actual Emby playback session.
/// </summary>
public sealed class RadioPlayedItem
{
    public Guid Id { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    // Original incoming title before cleanup/manual corrections. Older history JSON
    // has no value; readers fall back to SongTitle for those entries.
    public string OriginalSongTitle { get; set; } = string.Empty;
    public DateTimeOffset PlayedAt { get; set; }
}

/// <summary>
/// Persistent, newest-first history of radio tracks observed during playback.
/// History is stored alongside the plugin configuration and survives restarts.
/// </summary>
public sealed class RadioPlayHistoryStore
{
    public const int MaximumItems = 200;
    private const long MaximumHistoryFileBytes = 40L * 1024 * 1024;

    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly Func<int> _limitProvider;
    private readonly object _gate = new();
    private readonly List<RadioPlayedItem> _items = new();

    public RadioPlayHistoryStore(
        string filePath,
        ILogger logger,
        Func<int>? limitProvider = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limitProvider = limitProvider ?? (() => MaximumItems);
        Load();
    }

    private int CurrentLimit
    {
        get
        {
            try
            {
                return Math.Clamp(_limitProvider(), 1, 5000);
            }
            catch
            {
                return MaximumItems;
            }
        }
    }

    public IReadOnlyList<RadioPlayedItem> GetRecent(int? count = null)
    {
        var configuredLimit = CurrentLimit;
        var safeCount = Math.Clamp(count ?? configuredLimit, 1, configuredLimit);

        lock (_gate)
        {
            return _items
                .Take(safeCount)
                .Select(Clone)
                .ToArray();
        }
    }

    /// <summary>
    /// Adds a song only when artist or title metadata was actually observed
    /// during a playback event. Repeated progress events are deduplicated.
    /// </summary>
    public bool Record(
        string stationName,
        string artist,
        string songTitle,
        string? originalSongTitle = null)
    {
        stationName = Limit((stationName ?? string.Empty).Trim(), 160);
        artist = Limit((artist ?? string.Empty).Trim(), 300);
        songTitle = Limit((songTitle ?? string.Empty).Trim(), 300);
        originalSongTitle = Limit((string.IsNullOrWhiteSpace(originalSongTitle) ? songTitle : originalSongTitle).Trim(), 300);

        if (stationName.Length == 0 ||
            (artist.Length == 0 && songTitle.Length == 0))
        {
            return false;
        }

        var now = DateTimeOffset.Now;

        lock (_gate)
        {
            // Emby can report the same playback from more than one event or
            // client. Avoid adding a duplicate if this exact track was just
            // observed in the last couple of minutes.
            var isRecentDuplicate = _items.Any(item =>
                item.PlayedAt >= now - DuplicateWindow &&
                Same(item.StationName, stationName) &&
                Same(item.Artist, artist) &&
                Same(item.SongTitle, songTitle));

            if (isRecentDuplicate)
            {
                return false;
            }

            _items.Insert(0, new RadioPlayedItem
            {
                Id = Guid.NewGuid(),
                StationName = stationName,
                Artist = artist,
                SongTitle = songTitle,
                OriginalSongTitle = originalSongTitle,
                PlayedAt = now
            });

            TrimToLimit();

            Save();
            return true;
        }
    }

    /// <summary>Gets a copy of a history entry by its stable identifier.</summary>
    public RadioPlayedItem? GetById(Guid id)
    {
        if (id == Guid.Empty)
        {
            return null;
        }

        lock (_gate)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == id);
            return item is null ? null : Clone(item);
        }
    }

    /// <summary>Updates only the displayed song title for one history entry.
    /// The previous value is returned so the caller can roll back related config changes if needed.</summary>
    public bool UpdateSongTitle(Guid id, string correctedTitle, out string previousTitle, out string error)
    {
        previousTitle = string.Empty;
        error = string.Empty;
        correctedTitle = (correctedTitle ?? string.Empty).Trim();
        if (id == Guid.Empty || correctedTitle.Length == 0 || correctedTitle.Length > 300)
        {
            error = "A valid history entry and corrected title are required (1–300 characters).";
            return false;
        }

        lock (_gate)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
            {
                error = "That history entry no longer exists. Refresh the page and try again.";
                return false;
            }

            previousTitle = item.SongTitle;
            var previousOriginalTitle = item.OriginalSongTitle;
            if (string.IsNullOrWhiteSpace(item.OriginalSongTitle))
            {
                // For legacy JSON rows, preserve the first known title before editing it.
                item.OriginalSongTitle = item.SongTitle;
            }
            item.SongTitle = correctedTitle;
            if (Save())
            {
                return true;
            }

            item.SongTitle = previousTitle;
            item.OriginalSongTitle = previousOriginalTitle;
            error = "The corrected title could not be saved to history. Check plugin configuration-folder permissions.";
            return false;
        }
    }

    /// <summary>Updates the displayed artist and title of one history entry atomically.</summary>
    public bool UpdateSongMetadata(Guid id, string correctedArtist, string correctedTitle, out string previousArtist, out string previousTitle, out string error)
    {
        previousArtist = string.Empty;
        previousTitle = string.Empty;
        error = string.Empty;
        correctedArtist = Limit((correctedArtist ?? string.Empty).Trim(), 300);
        correctedTitle = (correctedTitle ?? string.Empty).Trim();
        if (id == Guid.Empty || correctedTitle.Length == 0 || correctedTitle.Length > 300)
        {
            error = "A valid history entry and corrected title are required (1–300 characters).";
            return false;
        }

        lock (_gate)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
            {
                error = "That history entry no longer exists. Refresh the page and try again.";
                return false;
            }

            previousArtist = item.Artist;
            previousTitle = item.SongTitle;
            var previousOriginalTitle = item.OriginalSongTitle;
            if (string.IsNullOrWhiteSpace(item.OriginalSongTitle))
            {
                item.OriginalSongTitle = item.SongTitle;
            }
            item.Artist = correctedArtist;
            item.SongTitle = correctedTitle;
            if (Save())
            {
                return true;
            }

            item.Artist = previousArtist;
            item.SongTitle = previousTitle;
            item.OriginalSongTitle = previousOriginalTitle;
            error = "The corrected artist/title could not be saved to history. Check plugin configuration-folder permissions.";
            return false;
        }
    }

    /// <summary>Removes one history entry by its stable identifier.</summary>
    public bool Remove(Guid id, out string error)
    {
        error = string.Empty;
        if (id == Guid.Empty)
        {
            error = "The history entry identifier is invalid.";
            return false;
        }

        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                error = "That history entry no longer exists. Refresh the page and try again.";
                return false;
            }

            var removed = _items[index];
            _items.RemoveAt(index);
            if (Save())
            {
                return true;
            }

            _items.Insert(index, removed);
            error = "The history entry could not be saved as deleted. Check the plugin configuration folder permissions.";
            return false;
        }
    }

    /// <summary>Clears all recorded history; restores the in-memory list if saving fails.</summary>
    public bool Clear(out int removedCount, out string error)
    {
        error = string.Empty;
        lock (_gate)
        {
            removedCount = _items.Count;
            if (removedCount == 0)
            {
                return true;
            }

            var previous = _items.ToList();
            _items.Clear();
            if (Save())
            {
                return true;
            }

            _items.AddRange(previous);
            removedCount = 0;
            error = "History could not be cleared on disk. Check the plugin configuration folder permissions.";
            return false;
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    if (!RadioStationFileStore.TryReadCappedFile(_filePath, MaximumHistoryFileBytes, out var bytes))
                        throw new InvalidDataException("History file is empty or exceeds the 40 MB safety limit.");
                    var json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                    var saved = JsonSerializer.Deserialize<List<RadioPlayedItem>>(
                        json, JsonOptions);

                    if (saved is not null)
                    {
                        var validItems = saved
                            .Where(item => item is not null &&
                                !string.IsNullOrWhiteSpace(item.StationName) &&
                                (!string.IsNullOrWhiteSpace(item.Artist) ||
                                 !string.IsNullOrWhiteSpace(item.SongTitle)))
                            .OrderByDescending(item => item.PlayedAt)
                            .Take(CurrentLimit)
                            .ToList();

                        var assignedLegacyIds = false;
                        foreach (var item in validItems)
                        {
                            if (item.Id == Guid.Empty)
                            {
                                item.Id = Guid.NewGuid();
                                assignedLegacyIds = true;
                            }

                            _items.Add(item);
                        }

                        // Existing history files predate per-entry IDs. Persist the
                        // generated IDs so individual deletion remains stable after restart.
                        if (assignedLegacyIds)
                        {
                            Save();
                        }
                    }
                }

                TrimToLimit();
                _logger.Info(
                    "Radio play history loaded {0} entries (configured limit {1}).",
                    _items.Count,
                    CurrentLimit);
            }
            catch (Exception ex)
            {
                _items.Clear();
                _logger.Error(
                    "Radio play history could not be loaded ({0}).",
                    ex.GetType().Name);
            }
        }
    }


    private void TrimToLimit()
    {
        var limit = CurrentLimit;
        if (_items.Count > limit)
        {
            _items.RemoveRange(limit, _items.Count - limit);
        }
    }

    private bool Save()
    {
        var temporaryPath = _filePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_items, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Radio play history could not be saved ({0}).",
                ex.GetType().Name);

            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }

            return false;
        }
    }

    private static string Limit(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static RadioPlayedItem Clone(RadioPlayedItem item) => new()
    {
        Id = item.Id,
        StationName = item.StationName,
        Artist = item.Artist,
        SongTitle = item.SongTitle,
        OriginalSongTitle = item.OriginalSongTitle,
        PlayedAt = item.PlayedAt
    };

    private static bool Same(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
