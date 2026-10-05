# Jellyfin TorrCatalog

Jellyfin 12.1 server plugin that shows TMDB catalogs (Trending, New Releases, Popular, …) as virtual
movie libraries. Items don't exist in the Jellyfin library DB: a global MVC filter answers the Jellyfin
API calls for their IDs, so every client (Web, Android, Android TV, Samsung Tizen) shows them natively,
including "Latest in <section>" rows on Home.

Status: Phase 1 (catalog). Playback uses a dev stand-in stream; Prowlarr + TorrServer come next.

## How it works

- **IDs**: `VirtualId` packs a magic prefix + kind + TMDB id into the Guid, so an ID decodes straight back
  to its TMDB id. No mapping table, IDs survive restarts.
- **Sections**: each enabled section in the plugin config becomes a `CollectionFolder` (`movies`) appended
  to `/UserViews`; `/Items?parentId=` and `/Items/Latest?parentId=` page through the TMDB list.
- **Items**: `/Items/{id}`, images (302 to image.tmdb.org; the TMDB image path is the image tag),
  `/Items?ids=`, and empties for intros/trailers/similar/theme media/segments.
- **Search**: TMDB movie matches are appended to Jellyfin's own `/Items?searchTerm=` results.
- **Playback**: media sources are declared `Protocol=File`, so every client requests
  `/Videos/{id}/stream`, which is redirected (302) to the stream URL. `Protocol=Http` breaks
  jellyfin-android (its ExoPlayer assumes HLS for Http sources).
- **Cache**: in-memory (30 min sections, 24 h details, 15 min search), concurrent requests share one fetch.

Unhandled API routes for a virtual ID are logged as `TorrCatalog: unhandled virtual route …`.

## Development

No local .NET SDK needed; everything builds in Docker. The dev compose targets a remote docker host
(no bind mounts, the plugin is baked into the image and copied into `/config/plugins` on start):

```bash
DOCKER_CONTEXT=remote-builder docker compose -f docker/docker-compose.dev.yml up -d --build
```

- Jellyfin: http://192.168.88.95:8096 (finish the setup wizard on first start)
- `media`: nginx serving a generated 5-minute h264/aac test mp4 on :8090, used as the stand-in stream
  for every catalog movie via `TORRCATALOG_DEV_STREAM_URL` (override with `DEV_STREAM_URL=...`).
- Tests run during the image build (`dotnet test --solution Jellyfin.Plugin.TorrCatalog.slnx`);
  a failing test fails the build.

Configure in Dashboard → Plugins → TorrCatalog: TMDB API key (v3 key or v4 read access token),
language, region, sections. Sections are hidden until a key is set.

## Phase 1 manual check

1. Set the TMDB key, press **Test TMDB**, save.
2. Home shows the section libraries and "Latest in Trending / New Releases / Popular" rows.
3. Open a section, scroll; open a movie: poster, backdrop, title, year, overview, rating.
4. Search finds TMDB movies; Play starts the test stream; seeking works.
5. Restart the container: config and item links still work.

## Client compatibility

| Client | Catalog | Playback (dev stream) |
| --- | --- | --- |
| Jellyfin Web 12.1 | pending | ✅ (spike) |
| Jellyfin for Android 2.7.3 | pending | ✅ (spike) |
| Samsung Tizen | pending | pending |
