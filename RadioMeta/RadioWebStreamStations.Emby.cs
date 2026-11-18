using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace RadioMeta;

/// <summary>
/// The Emby-facing part of <see cref="RadioWebStreamStations"/>: reading the
/// playback event and Emby's item numbers. The core is plain C# so it can be
/// tested without Emby.
/// </summary>
internal static partial class RadioWebStreamStations
{
    /// <summary>
    /// On when "Stations outside the station folder" is ticked, and in automatic
    /// mode (blank station folder), which already accepts any audio station.
    /// </summary>
    internal static bool IsEnabled(RadioMetaPlugin plugin) =>
        plugin.GetConfiguredStationFolder().Length == 0 ||
        (plugin.Configuration?.EnableStationsOutsideFolder ?? true);

    /// <summary>
    /// Returns a virtual station path when the playing item is audio with an
    /// http/https stream address and no .strm file. Callers only use this after
    /// the normal .strm check has found no station.
    /// </summary>
    internal static bool TryResolve(
        RadioMetaPlugin plugin,
        string sessionKey,
        PlaybackProgressEventArgs args,
        out string stationPath,
        out object? item)
    {
        stationPath = string.Empty;
        item = null;
        try
        {
            if (!IsEnabled(plugin)) return false;

            // Videos and local audio files are ordinary library playback.
            if (RadioCurrentlyPlayingStationCandidateBuilder.IsClearlyNonRadioPlayback(args)) return false;

            var playSession = args.PlaySession;
            var itemKey = GetItemKey(playSession?.FullNowPlayingItem) ??
                GetItemKey(args.Item) ??
                GetItemKey(playSession?.NowPlayingItem);
            if (itemKey is null) return false;

            // The same logic "Add to Radio Meta" uses to find the stream address.
            // Emby includes the media source when playback starts but leaves it out
            // of most progress updates, so an update without an address keeps the
            // virtual station this Emby item already has (as the capture code does).
            var snapshot = RadioCurrentlyPlayingStationCandidateBuilder.BuildCandidate(sessionKey, args);
            var path = snapshot is not null && !string.IsNullOrWhiteSpace(snapshot.Name)
                ? Register(GetRoot(plugin), snapshot.Name, snapshot.StreamUrl, itemKey)
                : string.Empty;
            if (path.Length == 0 && !TryFindByItemKey(itemKey, out path)) return false;

            stationPath = path;
            item = (object?)playSession?.NowPlayingItem ?? (object?)args.Item ?? playSession?.FullNowPlayingItem;
            return true;
        }
        catch
        {
            stationPath = string.Empty;
            item = null;
            return false;
        }
    }

    private static readonly TimeSpan AddressLookupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Emby doesn't put a web stream's address on the playback event (WebStreams
    /// fills it in only when a player asks for the item's media sources). For an
    /// audio item with no .strm and no virtual station yet, ask Emby's media-source
    /// manager for its playable sources once, in the background: the same request
    /// the PlaybackInfo API makes, which "Add to Radio Meta" already uses. No media
    /// probing. When a web address is found, the item gets its virtual station and
    /// the next progress event uses it. Never waits; never asks twice at once; an
    /// item with no web address isn't asked again for 10 minutes.
    /// </summary>
    internal static void BeginAddressLookup(
        RadioMetaPlugin plugin,
        IMediaSourceManager? mediaSourceManager,
        IUserManager? userManager,
        SessionInfo? session,
        PlaybackProgressEventArgs args,
        ILogger? logger)
    {
        try
        {
            if (mediaSourceManager is null || !IsEnabled(plugin)) return;
            if (RadioCurrentlyPlayingStationCandidateBuilder.IsClearlyNonRadioPlayback(args)) return;

            var item = args.PlaySession?.FullNowPlayingItem ?? args.Item;
            if (item is null) return;
            if (!string.Equals(item.MediaType, "Audio", StringComparison.OrdinalIgnoreCase)) return;
            // A .strm item is always handled by the normal station check.
            if (!string.IsNullOrWhiteSpace(item.Path) &&
                string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase)) return;

            var itemKey = GetItemKey(item);
            if (itemKey is null || TryFindByItemKey(itemKey, out _)) return;
            if (!TryBeginAddressLookup(itemKey)) return;

            var userInternalId = session?.UserInternalId ?? 0;
            var root = GetRoot(plugin);
            var name = item.Name ?? string.Empty;

            _ = Task.Run(async () =>
            {
                var found = false;
                try
                {
                    User? user = null;
                    if (userManager is not null && userInternalId > 0)
                    {
                        try { user = userManager.GetUserById(userInternalId); }
                        catch { user = null; }
                    }

                    using var timeout = new CancellationTokenSource(AddressLookupTimeout);
                    var sources = await mediaSourceManager
                        .GetPlayackMediaSources(item, user, false, false, timeout.Token)
                        .ConfigureAwait(false);

                    var streamUrl = string.Empty;
                    if (sources is not null)
                    {
                        foreach (var source in sources)
                        {
                            if (source is null) continue;
                            streamUrl = AcceptAddress(source.Path);
                            if (streamUrl.Length == 0) streamUrl = AcceptAddress(source.DirectStreamUrl);
                            if (streamUrl.Length > 0) break;
                        }
                    }

                    if (streamUrl.Length > 0 && name.Length > 0)
                    {
                        found = Register(root, name, streamUrl, itemKey).Length > 0;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Debug("Radio Meta could not read the media sources of a web-address station ({0}).", ex.GetType().Name);
                }
                finally
                {
                    FinishAddressLookup(itemKey, found);
                }
            });
        }
        catch
        {
            // Never let this affect playback handling.
        }
    }

    // Same filter as "Add to Radio Meta": http/https only, no credentials, never
    // Emby's own playback addresses.
    private static string AcceptAddress(string? value)
    {
        var accepted = RadioCurrentlyPlayingStationCandidateBuilder.ValidateExternalStreamUrl(value ?? string.Empty);
        return accepted.Length > 0 &&
               Uri.TryCreate(accepted, UriKind.Absolute, out var uri) &&
               RadioMetaUriSchemeHelpers.IsHttpOrHttps(uri)
            ? accepted
            : string.Empty;
    }

    /// <summary>True when the item is the Emby item a virtual station was created for.</summary>
    internal static bool ItemMatches(string? stationPath, object? item) =>
        ItemKeyMatches(stationPath, GetItemKey(item));

    /// <summary>True when the session is still playing the item of this virtual station.</summary>
    internal static bool SessionUsesStation(PlaySessionInfo? playSession, string stationPath)
    {
        if (playSession is null || !IsVirtualStation(stationPath)) return false;
        return ItemMatches(stationPath, playSession.NowPlayingItem) ||
               ItemMatches(stationPath, playSession.FullNowPlayingItem);
    }

    internal static string GetRoot(RadioMetaPlugin plugin)
    {
        try
        {
            var configurationDirectory = Path.GetDirectoryName(plugin.ConfigurationFilePath);
            return string.IsNullOrWhiteSpace(configurationDirectory)
                ? string.Empty
                : Path.GetFullPath(Path.Combine(configurationDirectory, FolderName));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Emby's internal item number. BaseItem has it as InternalId; the session
    /// DTO's Id is that same number as text.
    /// </summary>
    internal static string? GetItemKey(object? item)
    {
        try
        {
            switch (item)
            {
                case BaseItem baseItem when baseItem.InternalId > 0:
                    return baseItem.InternalId.ToString(CultureInfo.InvariantCulture);
                case BaseItemDto dto when !string.IsNullOrWhiteSpace(dto.Id):
                    return dto.Id.Trim();
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }
}
