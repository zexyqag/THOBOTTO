FROM node:24-bookworm-slim AS gamedig
WORKDIR /gamedig
COPY gamedig/package.json gamedig/package-lock.json ./
RUN npm ci --omit=dev

# Voice: Opus and libsodium from Ubuntu, Discord's libdave (named as NetCord loads them, next to the bot),
# libgomp for Whisper (loaded by the system's loader, so in its folder), and Piper for helper voices.
FROM ubuntu:noble AS voice
ARG LIBDAVE_VERSION=1.2.1
ARG PIPER_VERSION=2023.11.14-2
ARG YTDLP_VERSION=2026.08.19
ARG FFMPEG_BUILD=autobuild-2026-07-31-14-10
ARG FFMPEG_NAME=ffmpeg-n7.1.5-12-g1fdbca85aa-linux64-gpl-7.1
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl unzip xz-utils libopus0 libsodium23 libgomp1 && rm -rf /var/lib/apt/lists/*
# Watching together: yt-dlp finds videos, ffmpeg makes them playable in a browser.
RUN mkdir -p /opt/watch /watch /tmp-app \
    && curl -fsSL -o /opt/watch/yt-dlp "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" && chmod +x /opt/watch/yt-dlp \
    && curl -fsSL -o /tmp/ffmpeg.tar.xz "https://github.com/BtbN/FFmpeg-Builds/releases/download/${FFMPEG_BUILD}/${FFMPEG_NAME}.tar.xz" \
    && tar xJf /tmp/ffmpeg.tar.xz -C /tmp "${FFMPEG_NAME}/bin/ffmpeg" && mv "/tmp/${FFMPEG_NAME}/bin/ffmpeg" /opt/watch/ffmpeg && rm -rf /tmp/ffmpeg* \
    && cp -L /usr/lib/x86_64-linux-gnu/libz.so.1 /opt/watch/libz.so.1
RUN mkdir -p /voice /models /relay \
    && curl -fsSL -o /tmp/dave.zip "https://github.com/discord/libdave/releases/download/v${LIBDAVE_VERSION}%2Fcpp/libdave-Linux-X64-boringssl.zip" \
    && unzip -j /tmp/dave.zip lib/libdave.so -d /voice \
    && cp -L /usr/lib/x86_64-linux-gnu/libopus.so.0 /voice/libopus.so \
    && cp -L /usr/lib/x86_64-linux-gnu/libsodium.so.23 /voice/libsodium.so \
    && curl -fsSL "https://github.com/rhasspy/piper/releases/download/${PIPER_VERSION}/piper_linux_x86_64.tar.gz" | tar xz -C /opt

# The Activity (shown inside Discord's voice channels): built into the bot's web root.
FROM node:24-bookworm-slim AS activity
WORKDIR /activity
COPY activity/package.json activity/package-lock.json ./
RUN npm ci
COPY activity/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/THOBOTTO.csproj src/
RUN dotnet restore src/THOBOTTO.csproj
COPY src/ src/
COPY --from=activity /src/wwwroot/activity src/wwwroot/activity
RUN dotnet publish src/THOBOTTO.csproj --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
COPY --from=gamedig /usr/local/bin/node /usr/local/bin/node
COPY --from=gamedig /gamedig /opt/gamedig
ENV GameDig__Path=/opt/gamedig
COPY --from=voice /usr/lib/x86_64-linux-gnu/libgomp.so.1 /usr/lib/x86_64-linux-gnu/
# yt-dlp needs zlib, which the image doesn't have (.NET carries its own).
COPY --from=voice /opt/watch/libz.so.1 /usr/lib/x86_64-linux-gnu/
# Whisper models download here; a volume keeps them across deploys (owned by the image's app user).
COPY --from=voice --chown=1654:1654 /models /data/models
ENV Listening__Models=/data/models
# The voice relay's certificate goes here; Lavalink reads it from a shared volume to trust the relay.
COPY --from=voice --chown=1654:1654 /relay /data/relay
ENV Relay__Directory=/data/relay
COPY --from=voice /opt/piper /opt/piper
ENV Speaking__Piper=/opt/piper/piper
COPY --from=voice /opt/watch /opt/watch
ENV Watch__YtDlp=/opt/watch/yt-dlp Watch__Ffmpeg=/opt/watch/ffmpeg Watch__Directory=/data/watch
COPY --from=voice --chown=1654:1654 /watch /data/watch
# yt-dlp unpacks itself into the temp folder; the image has none of its own.
COPY --from=voice --chown=1654:1654 /tmp-app /data/tmp
ENV TMPDIR=/data/tmp
WORKDIR /app
COPY --from=voice /voice/ .
COPY --from=build /app .
ENTRYPOINT ["dotnet", "THOBOTTO.dll"]
