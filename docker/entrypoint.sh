#!/bin/sh
# /config is a volume, so the plugin baked into the image is copied in on every start.
set -e
mkdir -p /config/plugins/TorrCatalog_0.1.0.0
cp /opt/torrcatalog/* /config/plugins/TorrCatalog_0.1.0.0/
exec /jellyfin/jellyfin "$@"
