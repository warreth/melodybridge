#!/bin/sh
# Starts the app as an unprivileged user.
#
# Docker creates missing bind-mount folders as root. When the container
# starts as root, hand the three data roots to the app user first and then
# drop privileges. The chown is deliberately not recursive: the mounts hold
# the music library, and every new file is created by the app user anyway.
# A container started with `--user` skips the fix and runs as that user.
set -e

APP_USER=melodybridge
APP_UID=1000
APP_GID=1000

if [ "$(id -u)" = "0" ]; then
    for dir in /app/state /app/playlists /music; do
        mkdir -p "$dir"
        chown "$APP_UID:$APP_GID" "$dir"
    done
    exec su-exec "$APP_USER" dotnet /app/MelodyBridge.Server.dll "$@"
fi

exec dotnet /app/MelodyBridge.Server.dll "$@"
