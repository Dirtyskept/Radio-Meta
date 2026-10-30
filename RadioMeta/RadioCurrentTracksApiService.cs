using System.Linq;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

[Authenticated]
public sealed class RadioCurrentTracksApiService : IService
{
    public object Get(GetRadioCurrentTracks request)
    {
        var plugin = RadioMetaPlugin.Instance;
        if (plugin is null)
        {
            return new RadioCurrentTracksResponse
            {
                Error = "The Radio Meta plugin is not initialized."
            };
        }

        return new RadioCurrentTracksResponse
        {
            Items = plugin.CurrentTrackService.GetCurrentTracks()
                .Select(track => new RadioCurrentTrackDto
                {
                    SessionId = track.SessionId,
                    StationName = track.StationName,
                    Artist = track.Artist,
                    SongTitle = track.SongTitle,
                    RawSongTitle = track.RawSongTitle,
                    Source = track.Source,
                    UpdatedAt = track.UpdatedAt
                })
                .ToList()
        };
    }
}
