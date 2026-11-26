using System.Text.Json;
using System.Globalization;
using System.Xml.Linq;
using RadioMeta;

var tests = new List<(string Name, Action Test)>
{
    ("ICY single-quoted title containing an apostrophe", () => Equal(
        "Randy Crawford - One Day I'll Fly Away",
        RadioIcyStreamTitleParser.ExtractStreamTitleValue("'Randy Crawford - One Day I'll Fly Away';StreamUrl='https://example.invalid/stream';"))),
    ("ICY quoted field ends at quote-semicolon, not an interior apostrophe", () => Equal(
        "Let's Stay Together - Al Green",
        RadioIcyStreamTitleParser.ExtractStreamTitleValue("'Let's Stay Together - Al Green';StreamUrl='https://example.invalid/stream';"))),
    ("ICY double-quoted title containing an apostrophe", () => Equal(
        "Artist's Name - Song's Name",
        RadioIcyStreamTitleParser.ExtractStreamTitleValue("\"Artist's Name - Song's Name\";StreamUrl='https://example.invalid/stream';"))),
    ("ICY unquoted title ends at semicolon", () => Equal(
        "Artist - One Day I'll Fly Away",
        RadioIcyStreamTitleParser.ExtractStreamTitleValue("Artist - One Day I'll Fly Away;StreamUrl='https://example.invalid/stream';"))),
    ("ICY malformed quote preserves interior apostrophes", () => Equal(
        "Let's Stay Together",
        RadioIcyStreamTitleParser.ExtractStreamTitleValue("'Let's Stay Together"))),
    ("HTML-encoded ampersand is decoded", () => Equal(
        "Gesaffelstein & Pharrell Williams - Happy",
        RadioStreamTitleParser.NormalizeStreamTitle("Gesaffelstein &amp; Pharrell Williams - Happy"))),
    ("Spacing acute accent normalizes to apostrophe", () => Equal(
        "liquidfive x GREATOREX - Gravity's Losing Me (Original Mix)",
        RadioStreamTitleParser.NormalizeStreamTitle("liquidfive x GREATOREX - Gravity´s Losing Me (Original Mix)"))),
    ("Artist/title split preserves apostrophe in song title", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle("Randy Crawford - One Day I'll Fly Away");
        Equal("Randy Crawford", parsed.Artist);
        Equal("One Day I'll Fly Away", parsed.SongTitle);
    }),
    ("Artist/title split preserves later hyphen in song title", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle("Artist - Song - Live Version");
        Equal("Artist", parsed.Artist);
        Equal("Song - Live Version", parsed.SongTitle);
    }),
    ("Artist repeated at the start of the title is removed (1.3.157)", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle("Ella Langley - Ella Langley - Choosin' Texas");
        Equal("Ella Langley", parsed.Artist);
        Equal("Choosin' Texas", parsed.SongTitle);
    }),
    ("Repeated artist is removed regardless of case and dash type", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle("Ella Langley – ELLA LANGLEY — Choosin' Texas");
        Equal("Ella Langley", parsed.Artist);
        Equal("Choosin' Texas", parsed.SongTitle);
    }),
    ("Repeated artist is removed in other formats too", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle("Billie Eilish ~ Billie Eilish - Bad Guy ~ 246 ~ 0 ^");
        Equal("Billie Eilish", parsed.Artist);
        Equal("Bad Guy", parsed.SongTitle);
    }),
    ("Title that only contains the artist's name is kept", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle("Queen - Killer Queen");
        Equal("Queen", parsed.Artist);
        Equal("Killer Queen", parsed.SongTitle);
    }),
    ("Title that starts with the artist's name without a dash is kept", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle("Bad Company - Bad Company");
        Equal("Bad Company", parsed.SongTitle);
        parsed = RadioStreamTitleParser.SplitStreamTitle("Queen - Queens Of The Night");
        Equal("Queens Of The Night", parsed.SongTitle);
    }),
    ("Song named after its artist with a version label is kept", () =>
    {
        Equal("Black Sabbath - 2009 Remaster", RadioStreamTitleParser.SplitStreamTitle("Black Sabbath - Black Sabbath - 2009 Remaster").SongTitle);
        Equal("Bad Company - Live", RadioStreamTitleParser.SplitStreamTitle("Bad Company - Bad Company - Live").SongTitle);
        Equal("Iron Maiden - Remastered 2015", RadioStreamTitleParser.SplitStreamTitle("Iron Maiden - Iron Maiden - Remastered 2015").SongTitle);
        Equal("Ladytron - Radio Edit", RadioStreamTitleParser.SplitStreamTitle("Ladytron - Ladytron - Radio Edit").SongTitle);
        Equal("Queen - Live at Wembley", RadioStreamTitleParser.SplitStreamTitle("Queen - Queen - Live at Wembley").SongTitle);
    }),
    ("Repeated artist before a song whose name starts like a label is still removed", () => Equal(
        "Live Forever",
        RadioStreamTitleParser.SplitStreamTitle("Oasis - Oasis - Live Forever").SongTitle)),
    ("Title that is the artist plus a dash and nothing else is kept", () => Equal(
        "Prince -",
        RadioStreamTitleParser.RemoveRepeatedArtist("Prince", "Prince -"))),
    ("iHeartRadio cart record: artist before ' - text=', title in text", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"Ed Sheeran - text=""Bad Habits"" song_spot=""M"" MediaBaseId=""2720462"" itunesTrackId=""0"" amgTrackId=""-1"" length=""00:03:47"" unsID=""-1"" spotInstanceId=""-1""");
        Equal(@"Ed Sheeran", parsed.Artist);
        Equal(@"Bad Habits", parsed.SongTitle);
    }),
    ("iHeartRadio cart record keeps a hyphen inside the artist", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"Jay-Z - text=""Empire State of Mind"" song_spot=""M""");
        Equal(@"Jay-Z", parsed.Artist);
        Equal(@"Empire State of Mind", parsed.SongTitle);
    }),
    ("iHeartRadio cart record with ' | text='", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"Sam Smith | text=""Stay With Me"" song_spot=""M"" TAID=""0"" TPID=""0"" cartcutId=""0""");
        Equal(@"Sam Smith", parsed.Artist);
        Equal(@"Stay With Me", parsed.SongTitle);
    }),
    ("iHeartRadio title/artist key-value record", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"title=""SOMETHING JUST LIKE THIS"",artist=""Chainsmokers / Coldplay"",url=""song_spot=""F"" MediaBaseId=""0"" length=""00:04:00""");
        Equal(@"Chainsmokers / Coldplay", parsed.Artist);
        Equal(@"SOMETHING JUST LIKE THIS", parsed.SongTitle);
    }),
    ("iHeartRadio traffic spot (song_spot T) is not a song", () => True(!RadioStreamTitleParser.IsUsableStreamTitle(@"Pfizer - text=""Pfizer"" song_spot=""T"""))),
    ("iHeartRadio record without an artist (Spot Block End) is not a song", () => True(!RadioStreamTitleParser.IsUsableStreamTitle(@" - text=""Spot Block End"" amgTrackId=""9876543"" length=""00:00:00"""))),
    ("iHeartRadio key-value record without an artist is not a song", () => True(!RadioStreamTitleParser.IsUsableStreamTitle(@"title=""EntertainmentReport121021b"",artist="""",url=""song_spot=""F"""))),
    ("Ad break start marker '*' is not a song", () => True(!RadioStreamTitleParser.IsUsableStreamTitle(@"*"))),
    ("Ad break end marker '#' is not a song", () => True(!RadioStreamTitleParser.IsUsableStreamTitle(@"#"))),
    ("Triton TildeATS event '##...' is not a song", () => True(!RadioStreamTitleParser.IsUsableStreamTitle(@"##Ignored ~ Event ~ 300 ~ 0 ^"))),
    ("Triton TildeATS song: artist ~ title ~ seconds ~ 0 ^", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"Billie Eilish ~ Bad Guy ~ 246 ~ 0 ^");
        Equal(@"Billie Eilish", parsed.Artist);
        Equal(@"Bad Guy", parsed.SongTitle);
    }),
    ("Broadcast-automation '~' record (R101 format)", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"DAFT PUNK~ONE MORE TIME~DISCOVERY~2000~~231~2026-11-08T06:47:33~2026-11-08T06:47:33~R101");
        Equal(@"DAFT PUNK", parsed.Artist);
        Equal(@"ONE MORE TIME", parsed.SongTitle);
    }),
    ("Double-pipe record: artist || title || type", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"MADONNA || Beautiful Stranger || S");
        Equal(@"MADONNA", parsed.Artist);
        Equal(@"Beautiful Stranger", parsed.SongTitle);
    }),
    ("Extra text after ' | ' is dropped from the title", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"Artist - Title | Visit our website");
        Equal(@"Artist", parsed.Artist);
        Equal(@"Title", parsed.SongTitle);
    }),
    ("Title with a single '~' is split at the dash as before", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"Artist - Song ~ Remix");
        Equal(@"Artist", parsed.Artist);
        Equal(@"Song ~ Remix", parsed.SongTitle);
    }),
    ("'~' record with an empty artist field falls back to a plain title", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"~ONLY TITLE~ALBUM~2000~~200");
        Equal(@"", parsed.Artist);
        Equal(@"~ONLY TITLE~ALBUM~2000~~200", parsed.SongTitle);
    }),
    ("Hyphen inside an artist name stays in the artist", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"A-Ha - Take On Me");
        Equal(@"A-Ha", parsed.Artist);
        Equal(@"Take On Me", parsed.SongTitle);
    }),
    ("Title without a separator stays a plain title", () =>
    {
        var parsed = RadioStreamTitleParser.SplitStreamTitle(@"Just A Title");
        Equal(@"", parsed.Artist);
        Equal(@"Just A Title", parsed.SongTitle);
    }),
    ("JSON FFprobe StreamTitle is found case-insensitively", () =>
    {
        using var doc = JsonDocument.Parse("{\"format\":{\"tags\":{\"streamtitle\":\"Artist - Song\"}}}");
        Equal("Artist - Song", RadioStreamTitleParser.FindStreamTitle(doc.RootElement));
    }),
    ("Adaptive routing starts in parallel for an unlearned station", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        True(RadioMetaMetadataRoutingMode.ParallelLearning == router.BeginLookup("station-a").Mode);
    }),
    ("Adaptive routing prefers a source after four consecutive parallel losses", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        for (var i = 0; i < 3; i++) router.RecordParallelOutcome("station-b", "ICY", "Shoutcast");
        True(RadioMetaMetadataRoutingMode.ParallelLearning == router.BeginLookup("station-b").Mode);
        router.RecordParallelOutcome("station-b", "ICY", "Shoutcast");
        var plan = router.BeginLookup("station-b");
        True(RadioMetaMetadataRoutingMode.PreferredFirst == plan.Mode);
        Equal("ICY", plan.PreferredSource);
    }),
    ("Adaptive routing does not learn from alternating source winners", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        router.RecordParallelOutcome("station-alternating", "ICY", "Shoutcast");
        router.RecordParallelOutcome("station-alternating", "Shoutcast", "ICY");
        router.RecordParallelOutcome("station-alternating", "ICY", "Shoutcast");
        router.RecordParallelOutcome("station-alternating", "Shoutcast", "ICY");
        True(RadioMetaMetadataRoutingMode.ParallelLearning == router.BeginLookup("station-alternating").Mode);
    }),
    ("Adaptive routing preferences are isolated per station", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        for (var i = 0; i < 4; i++) router.RecordParallelOutcome("station-learned", "ICY", "Shoutcast");
        True(RadioMetaMetadataRoutingMode.PreferredFirst == router.BeginLookup("station-learned").Mode);
        True(RadioMetaMetadataRoutingMode.ParallelLearning == router.BeginLookup("station-unseen").Mode);
    }),
    ("Adaptive routing can be reset to parallel learning", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        for (var i = 0; i < 4; i++) router.RecordParallelOutcome("station-reset", "Shoutcast", "ICY");
        True(RadioMetaMetadataRoutingMode.PreferredFirst == router.BeginLookup("station-reset").Mode);
        router.Reset();
        True(RadioMetaMetadataRoutingMode.ParallelLearning == router.BeginLookup("station-reset").Mode);
    }),
    ("Adaptive routing periodically rechecks the preferred source in parallel", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        for (var i = 0; i < 4; i++) router.RecordParallelOutcome("station-c", "Shoutcast", "ICY");
        RadioMetaMetadataRoutePlan? plan = null;
        for (var i = 0; i < RadioMetaAdaptiveMetadataRouting.SequentialLookupsBetweenParallelRechecks; i++)
            plan = router.BeginLookup("station-c");
        True(RadioMetaMetadataRoutingMode.ParallelRecheck == plan!.Mode);
    }),
    ("Adaptive routing resets to learning after two preferred-source misses", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        for (var i = 0; i < 4; i++) router.RecordParallelOutcome("station-d", "ICY", "Shoutcast");
        router.RecordSequentialOutcome("station-d", "ICY", "Shoutcast");
        router.RecordSequentialOutcome("station-d", "ICY", null);
        True(RadioMetaMetadataRoutingMode.ParallelLearning == router.BeginLookup("station-d").Mode);
    }),
    ("Adaptive parallel recheck interval grows after two confirming rechecks", () =>
    {
        var router = new RadioMetaAdaptiveMetadataRouting();
        for (var i = 0; i < 4; i++) router.RecordParallelOutcome("station-widen", "ICY", "Shoutcast");
        RadioMetaMetadataRoutePlan? plan = null;
        for (var i = 0; i < 10; i++) plan = router.BeginLookup("station-widen");
        True(plan!.Mode == RadioMetaMetadataRoutingMode.ParallelRecheck);
        router.RecordParallelOutcome("station-widen", "ICY", "Shoutcast", wasPeriodicRecheck: true);
        for (var i = 0; i < 10; i++) plan = router.BeginLookup("station-widen");
        True(plan!.Mode == RadioMetaMetadataRoutingMode.ParallelRecheck);
        router.RecordParallelOutcome("station-widen", "ICY", "Shoutcast", wasPeriodicRecheck: true);
        for (var i = 0; i < 19; i++) plan = router.BeginLookup("station-widen");
        True(plan!.Mode == RadioMetaMetadataRoutingMode.PreferredFirst);
        plan = router.BeginLookup("station-widen");
        True(plan.Mode == RadioMetaMetadataRoutingMode.ParallelRecheck);
    }),
    ("Adaptive source preference survives state-store restart", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadioMeta-Routing-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new RadioMetaDiagnosticEventStore();
            var stateStore = new RadioMetaAdaptiveRoutingStateStore(directory, diagnostics);
            var router = new RadioMetaAdaptiveMetadataRouting(stateStore);
            for (var i = 0; i < 4; i++) router.RecordParallelOutcome("station-persisted", "ICY", "Shoutcast");
            var restartedStore = new RadioMetaAdaptiveRoutingStateStore(directory, diagnostics);
            var restartedRouter = new RadioMetaAdaptiveMetadataRouting(restartedStore);
            var plan = restartedRouter.BeginLookup("station-persisted");
            True(plan.Mode == RadioMetaMetadataRoutingMode.PreferredFirst);
            Equal("ICY", plan.PreferredSource);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }),
    ("Web stream: an http address becomes a virtual station with that address", () =>
    {
        RadioWebStreamStations.Clear();
        var root = Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test");
        var path = RadioWebStreamStations.Register(root, "__80 HITS", "https://stream.example.invalid/80s", "6452563");
        True(path.Length > 0);
        True(RadioWebStreamStations.IsVirtualStation(path));
        True(RadioWebStreamStations.TryGetStreamUrl(path, out var url));
        Equal("https://stream.example.invalid/80s", url);
        Equal("__80 HITS", Path.GetFileNameWithoutExtension(path));
        True(!File.Exists(path)); // never written to disk
    }),
    ("Web stream: non-http addresses and missing item numbers are refused", () =>
    {
        RadioWebStreamStations.Clear();
        var root = Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test");
        Equal(string.Empty, RadioWebStreamStations.Register(root, "Local", @"C:\Music\song.mp3", "1"));
        Equal(string.Empty, RadioWebStreamStations.Register(root, "Icy", "icy://stream.example.invalid/x", "2"));
        Equal(string.Empty, RadioWebStreamStations.Register(root, "No id", "https://stream.example.invalid/x", ""));
        Equal(string.Empty, RadioWebStreamStations.Register("", "No root", "https://stream.example.invalid/x", "3"));
    }),
    ("Web stream: a curated .strm path is never treated as virtual", () =>
    {
        RadioWebStreamStations.Clear();
        var root = Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test");
        RadioWebStreamStations.Register(root, "Station", "https://stream.example.invalid/s", "10");
        var curated = Path.Combine(Path.GetTempPath(), "Curated", "Station.strm");
        True(!RadioWebStreamStations.IsVirtualStation(curated));
        True(!RadioWebStreamStations.TryGetStreamUrl(curated, out _));
    }),
    ("Web stream: names with characters not allowed in file names stay inside the folder", () =>
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test"));
        var path = RadioWebStreamStations.BuildStationPath(root, "../..\\Rock: Hits / 24*7?");
        True(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Equal(root, Path.GetDirectoryName(path));
        Equal("Web stream", Path.GetFileNameWithoutExtension(RadioWebStreamStations.BuildStationPath(root, "  ")));
    }),
    ("Web stream: the session check matches the Emby item number only", () =>
    {
        RadioWebStreamStations.Clear();
        var root = Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test");
        var path = RadioWebStreamStations.Register(root, "Station", "https://stream.example.invalid/s", "6452564");
        True(RadioWebStreamStations.ItemKeyMatches(path, "6452564"));
        True(!RadioWebStreamStations.ItemKeyMatches(path, "6452565"));
        True(!RadioWebStreamStations.ItemKeyMatches(path, null));
    }),
    ("Web stream: only a stream with no ICY header and no song data is rested", () =>
    {
        RadioWebStreamStations.Clear();
        var root = Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test");
        var url = "https://stream.example.invalid/podcast";
        var path = RadioWebStreamStations.Register(root, "Podcast", url, "20");
        // A live station between songs (it has ICY) keeps being asked.
        RadioWebStreamStations.MarkLookupFoundNothing(path);
        True(!RadioWebStreamStations.IsResting(path));
        // No ICY header and nothing found: rested.
        RadioWebStreamStations.MarkNoIcy(url);
        RadioWebStreamStations.MarkLookupFoundNothing(path);
        True(RadioWebStreamStations.IsResting(path));
        // Song data found later: asked normally again.
        RadioWebStreamStations.MarkLookupFoundMetadata(path);
        True(!RadioWebStreamStations.IsResting(path));
    }),
    ("Web stream: an update without the address keeps the item's station", () =>
    {
        RadioWebStreamStations.Clear();
        var root = Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test");
        var path = RadioWebStreamStations.Register(root, "__80 HITS", "https://stream.example.invalid/80s", "6452563");
        // A progress update with no media source: only the item number is known.
        True(RadioWebStreamStations.TryFindByItemKey("6452563", out var kept));
        Equal(path, kept);
        True(RadioWebStreamStations.TryGetStreamUrl(kept, out var url));
        Equal("https://stream.example.invalid/80s", url);
        // A different item is not given someone else's station.
        True(!RadioWebStreamStations.TryFindByItemKey("6452564", out _));
        True(!RadioWebStreamStations.TryFindByItemKey(null, out _));
    }),
    ("Web stream: an item's media sources are asked once, then rested if no address", () =>
    {
        RadioWebStreamStations.Clear();
        True(RadioWebStreamStations.TryBeginAddressLookup("6452564"));
        True(!RadioWebStreamStations.TryBeginAddressLookup("6452564")); // already running
        True(RadioWebStreamStations.TryBeginAddressLookup("6452565"));  // another item is independent
        RadioWebStreamStations.FinishAddressLookup("6452564", foundAddress: false);
        True(!RadioWebStreamStations.TryBeginAddressLookup("6452564")); // rested for 10 minutes
        RadioWebStreamStations.FinishAddressLookup("6452565", foundAddress: true);
        True(RadioWebStreamStations.TryBeginAddressLookup("6452565"));  // found: may be asked again later
        True(!RadioWebStreamStations.TryBeginAddressLookup(null));
        RadioWebStreamStations.Clear();
    }),
    ("Web stream: a changed address starts afresh", () =>
    {
        RadioWebStreamStations.Clear();
        var root = Path.Combine(Path.GetTempPath(), "RadioMetaWebStreams-Test");
        var path = RadioWebStreamStations.Register(root, "Station", "https://stream.example.invalid/old", "30");
        RadioWebStreamStations.MarkNoIcy("https://stream.example.invalid/old");
        RadioWebStreamStations.MarkLookupFoundNothing(path);
        True(RadioWebStreamStations.IsResting(path));
        RadioWebStreamStations.Register(root, "Station", "https://stream.example.invalid/new", "30");
        True(!RadioWebStreamStations.IsResting(path));
        True(RadioWebStreamStations.TryGetStreamUrl(path, out var url));
        Equal("https://stream.example.invalid/new", url);
        RadioWebStreamStations.Clear();
    }),
    ("Transient endpoint failures use persisted ten-lookup backoff", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadioMeta-Backoff-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RadioMetaAdaptiveRoutingStateStore(directory, new RadioMetaDiagnosticEventStore());
            store.RecordEndpointFailure("station-backoff", "stats", 500, "HTTP 500");
            for (var i = 0; i < 9; i++) store.AdvanceEligibleLookup("station-backoff");
            True(!store.ShouldAttemptEndpoint("station-backoff", "stats", out _));
            store.AdvanceEligibleLookup("station-backoff");
            True(store.ShouldAttemptEndpoint("station-backoff", "stats", out _));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }),
    ("Permanent endpoint recovery requires both time and lookup thresholds", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadioMeta-Recovery-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new RadioMetaDiagnosticEventStore();
            var store = new RadioMetaAdaptiveRoutingStateStore(directory, diagnostics);
            store.RecordEndpointFailure("station-404", "stats", 404, "HTTP 404");
            var statePath = Path.Combine(directory, "RadioMetaAdaptiveRoutingState.xml");
            var xml = XDocument.Load(statePath);
            var endpoint = xml.Root!.Element("Endpoints")!.Elements("Endpoint").Single();
            endpoint.SetAttributeValue("nextRecoveryUtc", DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture));
            endpoint.SetAttributeValue("lookupsSinceBlock", "319");
            xml.Save(statePath);
            var reopenedStore = new RadioMetaAdaptiveRoutingStateStore(directory, diagnostics);
            True(!reopenedStore.ShouldAttemptEndpoint("station-404", "stats", out _));
            reopenedStore.AdvanceEligibleLookup("station-404");
            True(reopenedStore.ShouldAttemptEndpoint("station-404", "stats", out _));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }),
    ("Manual endpoint block survives XML reload and overrides recovery", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadioMeta-Manual-Block-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new RadioMetaDiagnosticEventStore();
            _ = new RadioMetaAdaptiveRoutingStateStore(directory, diagnostics);
            var statePath = Path.Combine(directory, "RadioMetaAdaptiveRoutingState.xml");
            var xml = XDocument.Load(statePath);
            xml.Root!.Element("Endpoints")!.Add(new XElement("Endpoint",
                new XAttribute("stationPath", "station-manually-blocked"),
                new XAttribute("endpoint", "7.html"),
                new XAttribute("manualBlock", "true")));
            xml.Save(statePath);
            var reopenedStore = new RadioMetaAdaptiveRoutingStateStore(directory, diagnostics);
            True(!reopenedStore.ShouldAttemptEndpoint("station-manually-blocked", "7.html", out var reason));
            True(reason.Contains("manual XML block", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }),
    ("Blank title is rejected", () => True(!RadioStreamTitleParser.IsUsableStreamTitle("  "))),
    ("Bare stream URL is rejected as a song title", () => True(!RadioStreamTitleParser.IsUsableStreamTitle("https://example.invalid/stream"))),
    ("Plain station URL carries no credentials", () =>
        True(!RadioMetaUriSchemeHelpers.HasEmbeddedCredentials(new Uri("https://example.invalid/live.mp3?sid=1&type=mp3")))),
    ("Every known token query name is detected, case-insensitively", () =>
    {
        foreach (var name in new[] { "api_key", "apikey", "access_token", "emby_token", "x-emby-token", "authorization", "API_KEY", "X-Emby-Token" })
            True(RadioMetaUriSchemeHelpers.HasEmbeddedCredentials(new Uri("https://example.invalid/s?" + name + "=abc")));
    }),
    ("Percent-encoded token name is detected", () =>
        True(RadioMetaUriSchemeHelpers.HasEmbeddedCredentials(new Uri("https://example.invalid/s?api%5Fkey=abc")))),
    ("User-info in a URL counts as credentials", () =>
        True(RadioMetaUriSchemeHelpers.HasEmbeddedCredentials(new Uri("https://user:pass@example.invalid/s")))),
    ("Emby server path prefixes are matched case-insensitively", () =>
    {
        True(RadioMetaUriSchemeHelpers.PathStartsWithAny(new Uri("http://example.invalid/EMBY/Audio/1/stream"), "/emby/", "/Items/"));
        True(!RadioMetaUriSchemeHelpers.PathStartsWithAny(new Uri("http://example.invalid/stream/emby/"), "/emby/", "/Items/"));
    }),
};

var failed = 0;
Console.WriteLine("Radio Meta v1.3.157 - Offline Metadata Regression Harness");
Console.WriteLine("Runs the same parsing source used by the plugin. No network, Emby server, or audio is used.");
Console.WriteLine(new string('-', 72));
foreach (var item in tests)
{
    try
    {
        item.Test();
        Console.WriteLine($"PASS  {item.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {item.Name}");
        Console.WriteLine($"      {ex.Message}");
    }
}

// Informational only: a prefix may be a playlist/program label, so a parser alone
// cannot reliably infer whether the first segment is the artist.
const string ambiguous = "Future House Music - Calvin Harris & Sam Smith - Desire";
var ambiguousSplit = RadioStreamTitleParser.SplitStreamTitle(ambiguous);
Console.WriteLine();
Console.WriteLine("REVIEW  Ambiguous multi-part metadata (manual classification required)");
Console.WriteLine($"        Input:  {ambiguous}");
Console.WriteLine($"        Current split candidate: artist='{ambiguousSplit.Artist}', title='{ambiguousSplit.SongTitle}'");
Console.WriteLine("        A test cannot declare this split correct without station-specific evidence.");
Console.WriteLine();
Console.WriteLine($"Result: {tests.Count - failed} passed, {failed} failed, 1 review case.");
return failed == 0 ? 0 : 1;

static void Equal(string expected, string? actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>.");
}

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Expected condition to be true.");
}
