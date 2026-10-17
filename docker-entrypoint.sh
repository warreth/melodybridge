#!/bin/sh
# Starts the app as an unprivileged user and hands the data mounts over
# to it.
#
# Docker creates missing bind-mount folders as root, and images before
# v1.1 ran everything as root, so the app user cannot always write its
# own files (the database was the visible one: SQLite falls back to
# read-only and the first write fails). Started as root, this script
# takes the state and playlist folders recursively (small, fully ours),
# hands the music tree over once, and then drops root. A marker in the
# state folder keeps the music walk out of later starts. A container
# started with --user runs as that user and skips all of this.
set -e

APP_USER=melodybridge
APP_UID=1000
APP_GID=1000

if [ "$(id -u)" = "0" ]; then
    # The app user's home lives on the state mount: tool logins and
    # caches have to survive image updates, and the image's /root home
    # is not theirs.
    mkdir -p /app/state/home

    for dir in /app/state /app/playlists; do
        mkdir -p "$dir"
        chown -R "$APP_UID:$APP_GID" "$dir"
    done

    # The music tree can be large and is often shared with other apps,
    # so it changes owner once: images before v1.1 left root-owned files
    # behind, and a new mount can arrive root-owned too. A read-only
    # mount must not stop the app; the app reports its own write errors.
    mkdir -p /music
    if [ ! -e /app/state/.music-handover-done ] || ! su-exec "$APP_USER" sh -c "test -w /music"; then
        echo "melodybridge: handing /music to UID $APP_UID (first start after an update, can take a while on a large library)"
        chown -R "$APP_UID:$APP_GID" /music || true
        : > /app/state/.music-handover-done
        chown "$APP_UID:$APP_GID" /app/state/.music-handover-done
    fi
    chown "$APP_UID:$APP_GID" /music 2>/dev/null || true

    exec su-exec "$APP_USER" dotnet /app/MelodyBridge.Server.dll "$@"
fi

exec dotnet /app/MelodyBridge.Server.dll "$@"
