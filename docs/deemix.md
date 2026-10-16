# Deezer and deemix

The Deezer plugin downloads MP3 or FLAC from your own Deezer account
through [deemix](https://github.com/bambanah/deemix), the maintained
revival of the original tool. deemix owns the login (your ARL) and the
download; MelodyBridge searches Deezer's public catalogue and hands the
track URL over. A paid plan is required for 320 and FLAC downloads.

## Setup

deemix runs in Docker, and that is the easiest way to give MelodyBridge a
`deemix` command:

1. Start the deemix container and log in once through its web UI at
   `http://localhost:6595`, or run the CLI once with a terminal attached
   (`docker exec -it Deemix deemix --help`) and answer the ARL prompt.
2. Mount the same downloads folder into the container and into
   MelodyBridge at the same path, for example `/music/deemix` in both, so
   the finished file is visible to the app.
3. On the Plugins page, open the Deezer card's **Settings** panel: set the
   command to `docker exec Deemix deemix` and the downloads folder to the
   shared path.

A locally installed deemix CLI works the same way: set the command to the
binary, or build the CLI from the deemix repository.

## How a download runs

The plugin searches Deezer's public API, ranks the results, and runs deemix
for the chosen track into a per-track folder under the downloads path.
deemix downloads the best quality your account allows, up to the requested
maximum, and the plugin moves the file into the playlist folder. Runs are
serialized, so parallel tracks never pick each other's files.
