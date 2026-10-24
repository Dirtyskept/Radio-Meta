# Logging and privacy

The plugin writes diagnostic messages through Emby's normal server logger. This
source package deliberately excludes real server logs and generated build files.

## Privacy changes

The plugin's own log messages are designed not to include:
- station folder or plugin configuration absolute paths;
- FFprobe executable paths;
- Emby session IDs or access tokens;
- raw stream URLs or query strings;
- full track names / artist / station history records.

The built-in diagnostics also keeps a bounded, in-memory trace of the latest metadata
reader decisions (up to 80 events in the server; the dashboard displays the latest 40).
It records source/status, detected title text, whether a candidate was parsed, filtered,
accepted or rejected, HTTP status, ICY metadata interval information, and safe FFprobe
error summaries. Duplicate events are suppressed briefly to avoid log flooding. The
trace resets when Emby restarts and is not written to disk.

The direct ICY reader now examines up to 40 metadata intervals per attempt and returns
as soon as it finds a usable title. The existing five-second HTTP/ICY timeout remains
unchanged, so a slow stream may time out before all 40 intervals are examined. The
configured interval limit and reader events are surfaced in diagnostics.

The plugin still logs high-level status through Emby's normal server logger: whether the
station folder and FFprobe are available, whether HTTP/ICY metadata was usable, whether
FFprobe fallback was used, whether a title was retained, and aggregate station/artwork counts.

Emby's surrounding logs may still contain information written by Emby or other
plugins. Before sharing logs publicly, search and redact `api_key`, `AccessToken`,
`Authorization`, private hostnames/domains, local usernames, absolute paths, IP
addresses, device identifiers, session IDs, and station URLs containing credentials.

Do not put actual server logs or `RadioMetaHistory.json` into a public source ZIP.
This clean source package contains no sample or real server logs. Keep diagnostic examples separate from production logs.
