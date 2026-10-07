# Jellyfin TorrCatalog

Jellyfin 12.1 server plugin that shows TMDB catalogs (Trending, New Releases, Popular, TV, …) as virtual
movie and TV libraries and plays them from torrents (Prowlarr + TorrServer). Items don't exist in the
Jellyfin library DB: a global MVC filter answers the Jellyfin API calls for their IDs, so every client
(Web, Android, Android TV, Samsung Tizen) shows them natively, including "Latest in <section>" rows on Home.

Status: Phases 0-7 of the plan (no Home Screen Sections: the native "Latest" rows cover it).

## How it works

- **IDs**: `VirtualId` packs a magic prefix + kind + TMDB id (+ season, episode, release pin) into the
  Guid, so an ID decodes straight back. No mapping table, IDs survive restarts.
- **Sections**: each enabled section becomes a `CollectionFolder` (`movies`, or `tvshows` when its TMDB path
  has a `tv` segment) appended to `/UserViews`; `/Items?parentId=` and `/Items/Latest` page through TMDB.
- **TV**: series → seasons → episodes (`/Shows/{id}/Seasons|Episodes|NextUp`, `parentId` browsing);
  unaired episodes show as missing.
- **Items**: `/Items/{id}`, images (302 to image.tmdb.org; the TMDB image path is the image tag),
  `/Items?ids=`, and empties for intros/trailers/similar/theme media/segments/filters.
- **Search**: TMDB movie and TV matches are appended to Jellyfin's own `/Items?searchTerm=` results.
- **Releases**: Prowlarr is searched by ids plus localized and original titles (movies: + year; TV: per
  season, so packs are found). Results must match the item (indexer id or the title as whole words), are
  merged by infohash, parsed (resolution, source incl. CAM/TS/TC rips, codec, HDR, audio, language incl.
  RuTracker `D, P, A` codes, seasons/episode ranges) and scored with an explanation per point.
  Clients never see magnet/download URLs.
- **Versions** (release picker): details pages list "Auto" plus the top releases as native Jellyfin
  versions (`MediaSources`; low-quality CAM/TS ones marked ⚠). A pick on an episode is reused for the rest
  of that season.
- **Playback**: `PlaybackInfo` resolves the item: release → TorrServer → wait for metadata → video file
  (largest for movies; the right SxxEyy/numbered file in packs) → ffprobe (codecs, audio tracks,
  preferred-language default). Auto checks the client's `DeviceProfile` and skips releases it can't direct
  play; falls back to the next release (max 3) and, with nothing better, to a live CAM/TS with a warning.
  Media sources are `Protocol=File`, so clients request `/Videos/{id}/stream`, which redirects (302) to
  TorrServer (`Protocol=Http` breaks jellyfin-android's ExoPlayer).
- **Fallback library (transcoding)**: when a client can't direct play the chosen release (typically a
  browser and HEVC/AC3), the item is written as `.strm` into auto-created "TorrCatalog Movies/Shows"
  libraries and the `PlaybackInfo` call is handed to Jellyfin, which transcodes. The `.strm` points at
  `/Plugins/TorrCatalog/Stream/{id}?key=…` (anonymous for Jellyfin's ffmpeg, guarded by a generated key).
- **Watch progress**: per user in `watch-state.json` (plugin data folder): progress bars, resume,
  "Continue watching", played marks, next up. Jellyfin's resume thresholds (5% / 90%).
- **Status toasts**: while preparing playback, the client gets DisplayMessage toasts (searching, starting,
  peers, MB buffered and speed, warnings, failure reasons). Needs the client's websocket session.
- **Cache**: in-memory (30 min sections and release searches, 24 h details, 6 h started streams);
  concurrent requests share one fetch.

### Limitations

- Virtual items play directly; transcoding only through the fallback library (slower start: Jellyfin
  re-probes `.strm` items on every play).
- Subtitles aren't exposed for virtual playback yet.
- TorrServer must be reachable by clients (they're redirected to it) and without HTTP auth for now.
- Favorites on virtual items aren't stored.
- Status toasts don't reach clients without a live websocket (seen with Jellyfin for Tizen).

Unhandled API routes for a virtual ID are logged as `TorrCatalog: unhandled virtual route …`.

## Install

Requires Jellyfin 12.1.x, plus running Prowlarr and TorrServer (MatriX, reachable by clients, no HTTP auth).

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/Shareed2k/jellyfin-plugin-torrcatalog/gh-pages/manifest.json`.
2. Catalog → TorrCatalog → Install, then restart Jellyfin.
3. Plugin settings: TMDB key, Prowlarr URL + key, TorrServer URL; for browser transcoding also the
   fallback library folder (e.g. `/config/torrcatalog-library`), the Jellyfin LAN URL and "Fast ffprobe".
   Use the Test buttons.

Releases: push a tag `vX.Y.Z`; the Build workflow runs the tests, attaches `torrcatalog_X.Y.Z.0.zip` to a
GitHub release and adds the version to `manifest.json` on the `gh-pages` branch.

## Development

No local .NET SDK needed; everything builds in Docker. The dev compose targets a remote docker host
(no bind mounts, the plugin is baked into the image and copied into `/config/plugins` on start):

```bash
DOCKER_CONTEXT=remote-builder docker compose -f docker/docker-compose.dev.yml up -d --build
```

- Jellyfin: http://192.168.88.95:8096 (finish the setup wizard on first start)
- TorrServer: http://192.168.88.95:8090 (`ghcr.io/yourok/torrserver:MatriX.145.2`, no auth in dev)
- `media`: nginx serving a generated 5-minute h264/aac test mp4 on :8091, used as the stand-in stream
  when no TorrServer URL is configured (`TORRCATALOG_DEV_STREAM_URL`, override with `DEV_STREAM_URL=...`).
- Legal test torrent for TorrServer checks: Sintel (CC-BY), infohash `08ada5a7a6183aae1e09d831df6748d566095a10`.
- Tests run during the image build (`dotnet test --solution Jellyfin.Plugin.TorrCatalog.slnx`);
  a failing test fails the build.

Configure in Dashboard → Plugins → TorrCatalog: TMDB API key (v3 key or v4 read access token),
language, region, sections (hidden until a key is set), Prowlarr URL + API key, TorrServer URL,
release preferences. Each service has a Test button; "Search releases" shows the ranked list for a
TMDB id with the scoring breakdown.

## Manual check

1. Set the TMDB key, press **Test TMDB**, save.
2. Home shows the section libraries and "Latest in Trending / New Releases / Popular" rows.
3. Open a section, scroll; open a movie: poster, backdrop, title, year, overview, rating.
4. Search finds TMDB movies; Play starts the test stream; seeking works.
5. Restart the container: config and item links still work.

## Client compatibility

| Client | Catalog | Playback (dev stream) | Playback (TorrServer) |
| --- | --- | --- | --- |
| Jellyfin Web 12.1 (Chrome, macOS) | ✅ | ✅ | ✅ |
| Jellyfin for Android 2.7.3 | ✅ | ✅ | ✅ |
| Samsung Tizen | pending | pending | pending |

Verified 2026-10-06.
