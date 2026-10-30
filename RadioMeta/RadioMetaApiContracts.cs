using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace RadioMeta;

public sealed class RadioPlayHistoryResponse
{
    public string Error { get; set; } = string.Empty;
    public int Limit { get; set; }
    public List<RadioPlayHistoryEntryDto> Items { get; set; } = new();
}

public sealed class RadioPlayHistoryEntryDto
{
    public Guid Id { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string OriginalSongTitle { get; set; } = string.Empty;
    public DateTimeOffset PlayedAt { get; set; }
}

[Route("/RadioMeta/History", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioPlayHistory : IReturn<RadioPlayHistoryResponse>
{
}

public sealed class RadioPlayHistoryMutationResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int RemovedCount { get; set; }
}

[Route("/RadioMeta/History/Delete", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class DeleteRadioPlayHistoryEntry : IReturn<RadioPlayHistoryMutationResponse>
{
    public Guid Id { get; set; }
}

[Route("/RadioMeta/History/Clear", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class ClearRadioPlayHistory : IReturn<RadioPlayHistoryMutationResponse>
{
}

public sealed class RadioHistoryTitleEditResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RuleDescription { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
}

[Route("/RadioMeta/History/Edit", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class EditRadioPlayHistoryTitle : IReturn<RadioHistoryTitleEditResponse>
{
    public Guid Id { get; set; }
    public string CorrectedTitle { get; set; } = string.Empty;
    public string CorrectedArtist { get; set; } = string.Empty;
    // SongOnly stores an exact station/title correction. Station infers a reusable suffix rule.
    // Exact artist corrections are supported only for SongOnly; station-wide suffix rules stay title-only.
    public string Scope { get; set; } = "SongOnly";
    // Additional comma/semicolon-separated labels to preserve for a reusable station rule.
    public string PreserveLabels { get; set; } = string.Empty;
}

public sealed class RadioMetadataIgnorePhrasesResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioMetadataIgnorePhraseRule> Rules { get; set; } = new();
}

[Route("/RadioMeta/IgnorePhrases", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioMetadataIgnorePhrases : IReturn<RadioMetadataIgnorePhrasesResponse>
{
}

[Route("/RadioMeta/IgnorePhrases", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class SaveRadioMetadataIgnorePhrases : IReturn<RadioMetadataIgnorePhrasesResponse>
{
    public List<RadioMetadataIgnorePhraseRule> Rules { get; set; } = new();
}

public sealed class RadioStationMetadataModesResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioStationMetadataModeRule> Rules { get; set; } = new();
}

[Route("/RadioMeta/StationModes", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioStationMetadataModes : IReturn<RadioStationMetadataModesResponse>
{
}

[Route("/RadioMeta/StationModes", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class SaveRadioStationMetadataModes : IReturn<RadioStationMetadataModesResponse>
{
    public List<RadioStationMetadataModeRule> Rules { get; set; } = new();
}

public sealed class RadioTitleCleanupRulesResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioTitleCleanupRule> Rules { get; set; } = new();
}

[Route("/RadioMeta/TitleCleanup", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioTitleCleanupRules : IReturn<RadioTitleCleanupRulesResponse>
{
}

[Route("/RadioMeta/TitleCleanup", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class SaveRadioTitleCleanupRules : IReturn<RadioTitleCleanupRulesResponse>
{
    public List<RadioTitleCleanupRule> Rules { get; set; } = new();
}

public sealed class RadioTitleCorrectionsResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<RadioTitleCorrectionRule> Corrections { get; set; } = new();
}

[Route("/RadioMeta/TitleCorrections", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioTitleCorrections : IReturn<RadioTitleCorrectionsResponse>
{
}

[Route("/RadioMeta/TitleCorrections", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class SaveRadioTitleCorrections : IReturn<RadioTitleCorrectionsResponse>
{
    public List<RadioTitleCorrectionRule> Corrections { get; set; } = new();
}


public sealed class RadioPathSettingsResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string StationFolder { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
    public string ExpandedStationFolder { get; set; } = string.Empty;
    public bool AutomaticStationDetection { get; set; }
    public bool StationFolderAvailable { get; set; }
    public string ResolvedFfprobePath { get; set; } = string.Empty;
    public bool FfprobeAvailable { get; set; }
    public bool EnableAdaptiveMetadataRouting { get; set; } = true;
}

[Route("/RadioMeta/PathSettings", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetRadioPathSettings : IReturn<RadioPathSettingsResponse>
{
}

[Route("/RadioMeta/PathSettings", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class SaveRadioPathSettings : IReturn<RadioPathSettingsResponse>
{
    public string StationFolder { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
    // Nullable so older dashboard clients which do not send this adaptive-routing field
    // preserve the currently saved value instead of silently turning routing back on.
    public bool? EnableAdaptiveMetadataRouting { get; set; }
}

public sealed class RadioCurrentTrackDto
{
    public string SessionId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string SongTitle { get; set; } = string.Empty;
    public string RawSongTitle { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RadioCurrentTracksResponse
{
    public string Error { get; set; } = string.Empty;
    public List<RadioCurrentTrackDto> Items { get; set; } = new();
}

[Route("/RadioMeta/CurrentTracks", "GET")]
public sealed class GetRadioCurrentTracks : IReturn<RadioCurrentTracksResponse>
{
}
