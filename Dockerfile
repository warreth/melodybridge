FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# The release workflow passes the release tag (without the "v") so the
# published image reports the version it actually is. Source builds leave
# it empty and publish the SDK default.
ARG VERSION=

# copy entire repository and restore/build
COPY . ./
RUN dotnet restore

# publish server, stamped with the release version when one was passed
WORKDIR /src/MelodyBridge.Server
RUN dotnet publish -c Release -o /app/publish ${VERSION:+-p:Version=$VERSION}

# The deemix CLI for the Deezer plugin, built from a pinned commit. Their
# build bundles every runtime dependency, so the result runs on plain Node
# without a node_modules tree. The bundle is architecture independent, so
# this stage runs once on the build platform.
FROM --platform=$BUILDPLATFORM node:22-alpine AS deemix
ARG DEEMIX_COMMIT=61858706d6db1a6152d55add5029cec89c773360
RUN apk add --no-cache git && \
    git init /deemix && \
    cd /deemix && \
    git remote add origin https://github.com/bambanah/deemix && \
    git fetch --depth 1 origin ${DEEMIX_COMMIT} && \
    git checkout FETCH_HEAD
WORKDIR /deemix
RUN npm install -g pnpm@11.23.0 && \
    ELECTRON_SKIP_BINARY_DOWNLOAD=1 pnpm install --frozen-lockfile && \
    pnpm --filter deezer-sdk build && \
    pnpm --filter deemix build && \
    pnpm --filter deemix-cli build

# Runtime on Alpine: the Debian image needed 234 packages (about 490 MB
# installed) for ffmpeg alone, Alpine needs 113 (about 130 MB). The tools
# ship in the image so the plugins work without sidecars: yt-dlp as its
# self-contained musllinux binary (per architecture), qobuz-dlp and tiddl
# through pip (Python stays for them), and the deemix CLI on Node.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
ARG YTDLP_VERSION=2026.08.19
ARG TIDDL_VERSION=3.4.4
ARG TARGETARCH
RUN set -eux; \
    case "${TARGETARCH}" in \
        amd64) ytdlp_asset="yt-dlp_musllinux" ;; \
        arm64) ytdlp_asset="yt-dlp_musllinux_aarch64" ;; \
        *) echo "unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    apk add --no-cache \
        ca-certificates \
        curl \
        ffmpeg \
        nodejs \
        python3 \
        py3-pip \
        tzdata; \
    curl -fsSL -o /usr/local/bin/yt-dlp \
        "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/${ytdlp_asset}"; \
    chmod +x /usr/local/bin/yt-dlp; \
    pip3 install --no-cache-dir --break-system-packages qobuz-dlp; \
    pip3 install --no-cache-dir --break-system-packages "tiddl==${TIDDL_VERSION}"; \
    apk del py3-pip; \
    rm -rf /root/.cache
COPY --from=deemix /deemix/packages/cli/dist/main.cjs /opt/deemix/main.cjs
RUN printf '#!/bin/sh\nexec node /opt/deemix/main.cjs "$@"\n' > /usr/local/bin/deemix && \
    chmod +x /usr/local/bin/deemix
WORKDIR /app
COPY --from=build /app/publish ./

# Listen on 80 by default, so a plain `docker run -p 3333:80` works
# without extra environment variables (the compose files set it too).
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

# The app is healthy when its own home page answers. curl is installed
# above; the start period covers the first schema migration.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1/ >/dev/null || exit 1

ENTRYPOINT ["dotnet", "MelodyBridge.Server.dll"]
