# Stand-in for TorrServer until Phase 4: static mp4 over HTTP with Range support.
FROM jellyfin/jellyfin:12.1 AS gen
ENV FF=/usr/lib/jellyfin-ffmpeg/ffmpeg
RUN $FF -hide_banner -loglevel error \
      -f lavfi -i testsrc2=size=1920x1080:rate=24:duration=300 \
      -f lavfi -i sine=frequency=440:duration=300 \
      -c:v libx264 -preset veryfast -b:v 3M -pix_fmt yuv420p -g 48 \
      -c:a aac -b:a 128k -movflags +faststart /test.mp4 \
 && $FF -hide_banner -loglevel error -f lavfi -i testsrc2=size=600x900 -frames:v 1 /poster.jpg \
 && $FF -hide_banner -loglevel error -f lavfi -i testsrc=size=1920x1080 -frames:v 1 /backdrop.jpg

FROM nginx:alpine
COPY docker/media-nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=gen /test.mp4 /poster.jpg /backdrop.jpg /usr/share/nginx/html/
