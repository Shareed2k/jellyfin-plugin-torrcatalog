# Jellyfin TorrCatalog

Jellyfin 12.1 server plugin that shows TMDB catalogs (Trending, New Releases, Popular, …) as virtual
movie libraries. Items don't exist in the Jellyfin library DB: a global MVC filter answers the Jellyfin
API calls for their IDs, so every client (Web, Android, Android TV, Samsung Tizen) shows them natively,
including "Latest in <section>" rows on Home.

Status: Phase 4 MVP. Catalog (TMDB), release search (Prowlarr) and torrent playback (TorrServer) work
for movies. Next: manual release picker, TV shows, persistence.

## How it works

- **IDs**: `VirtualId` packs a magic prefix + kind + TMDB id into the Guid, so an ID decodes straight back
  to its TMDB id. No mapping table, IDs survive restarts.
- **Sections**: each enabled section in the plugin config becomes a `CollectionFolder` (`movies`) appended
  to `/UserViews`; `/Items?parentId=` and `/Items/Latest?parentId=` page through the TMDB list.
- **Items**: `/Items/{id}`, images (302 to image.tmdb.org; the TMDB image path is the image tag),
  `/Items?ids=`, and empties for intros/trailers/similar/theme media/segments.
- **Search**: TMDB movie matches are appended to Jellyfin's own `/Items?searchTerm=` results.
- **Releases**: Prowlarr is searched by IMDb/TMDB id plus localized and original title + year, results
  are merged by infohash, parsed (resolution, source, codec, HDR, audio, language incl. RuTracker
  `D, P, A` codes) and scored with an explanation per point. Clients never see magnet/download URLs.
- **Playback**: `PlaybackInfo` resolves the movie: best positive-scored release → TorrServer → wait for
  metadata → largest non-junk video file → ffprobe (real codecs/audio tracks, preferred-language audio as
  default). A release that times out or has no video is dropped and the next one is tried (max 3).
  Media sources are declared `Protocol=File`, so every client requests `/Videos/{id}/stream`, which is
  redirected (302) to TorrServer's `/stream` (Range/seek supported). `Protocol=Http` breaks
  jellyfin-android (its ExoPlayer assumes HLS for Http sources). Opening a details page starts the
  release search in the background so Play doesn't wait for Prowlarr.
- **Cache**: in-memory (30 min sections and release searches, 24 h details, 15 min search, 6 h resolved
  streams); concurrent requests share one fetch.

### Limitations

- Direct play only: virtual items aren't in the Jellyfin DB, so the transcoder can't serve them. TVs and
  Android usually play HEVC/MKV natively; browsers may not.
- Subtitles aren't exposed yet (they'd need DB-backed Jellyfin endpoints).
- TorrServer must be reachable by clients (they're redirected to it) and without HTTP auth for now.
- No watch progress for virtual items yet.

Unhandled API routes for a virtual ID are logged as `TorrCatalog: unhandled virtual route …`.

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

## Phase 1 manual check

1. Set the TMDB key, press **Test TMDB**, save.
2. Home shows the section libraries and "Latest in Trending / New Releases / Popular" rows.
3. Open a section, scroll; open a movie: poster, backdrop, title, year, overview, rating.
4. Search finds TMDB movies; Play starts the test stream; seeking works.
5. Restart the container: config and item links still work.

## Client compatibility

| Client | Catalog | Playback (dev stream) | Playback (TorrServer) |
| --- | --- | --- | --- |
| Jellyfin Web 12.1 | pending | ✅ | pending |
| Jellyfin for Android 2.7.3 | pending | ✅ | pending |
| Samsung Tizen | pending | pending | pending |

Catalog and torrent playback were confirmed working on 2026-10-06; which clients were used isn't recorded yet.
