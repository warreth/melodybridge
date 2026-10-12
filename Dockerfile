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

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    curl \
    ffmpeg \
    python3 \
    python3-pip \
    xz-utils && \
    pip3 install --break-system-packages --no-cache-dir yt-dlp && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 80
ENTRYPOINT ["dotnet", "MelodyBridge.Server.dll"]
