using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Services;

namespace RadioMeta;

/// <summary>
/// GET /RadioMeta/CurrentTracks: every active session's current track.
/// Admin only; used by Radio Meta Diagnostics.
/// </summary>
[Authenticated(Roles = "Admin")]
public sealed class RadioCurrentTracksApiService : IService
{
    public object Get(GetRadioCurrentTracks request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null) return RadioCurrentTracksMapping.NotInitialized();

        return new RadioCurrentTracksResponse
        {
            Items = plugin.CurrentTrackService.GetCurrentTracks()
                .Select(RadioCurrentTracksMapping.ToDto)
                .ToList()
        };
    }
}

/// <summary>
/// GET /RadioMeta/MyCurrentTracks: the current tracks of the caller's own sessions.
/// Any signed-in user; used by the embedded player as a fallback when Emby's
/// session data lacks the track. Emby identifies the caller from the request's
/// access token, so one user can never read another user's track. Admins receive
/// every session, which they can already see through CurrentTracks.
/// </summary>
[Authenticated]
public sealed class RadioMyCurrentTracksApiService : IService, IRequiresRequest
{
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ISessionManager _sessionManager;

    public RadioMyCurrentTracksApiService(IAuthorizationContext authorizationContext, ISessionManager sessionManager)
    {
        _authorizationContext = authorizationContext;
        _sessionManager = sessionManager;
    }

    /// <summary>Set by Emby for each request (IRequiresRequest).</summary>
    public IRequest Request { get; set; } = null!;

    public object Get(GetRadioMyCurrentTracks request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null) return RadioCurrentTracksMapping.NotInitialized();

        AuthorizationInfo? caller;
        try { caller = _authorizationContext.GetAuthorizationInfo(Request); }
        catch { caller = null; }

        // No identifiable caller: return nothing rather than guess.
        if (caller?.User is null) return new RadioCurrentTracksResponse();

        IEnumerable<RadioCurrentTrack> tracks = plugin.CurrentTrackService.GetCurrentTracks();
        if (caller.User.Policy?.IsAdministrator != true)
        {
            var callerUserId = caller.UserId;
            var ownSessionIds = _sessionManager.Sessions
                .Where(session => session is not null && session.UserInternalId == callerUserId &&
                                  !string.IsNullOrEmpty(session.Id))
                .Select(session => session.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            tracks = tracks.Where(track => ownSessionIds.Contains(track.SessionId));
        }

        return new RadioCurrentTracksResponse
        {
            Items = tracks.Select(RadioCurrentTracksMapping.ToDto).ToList()
        };
    }
}

internal static class RadioCurrentTracksMapping
{
    internal static RadioCurrentTracksResponse NotInitialized() => new()
    {
        Error = "The Radio Meta plugin is not initialized."
    };

    internal static RadioCurrentTrackDto ToDto(RadioCurrentTrack track) => new()
    {
        SessionId = track.SessionId,
        StationName = track.StationName,
        Artist = track.Artist,
        SongTitle = track.SongTitle,
        RawSongTitle = track.RawSongTitle,
        Source = track.Source,
        UpdatedAt = track.UpdatedAt
    };
}
