FROM node:24-bookworm-slim AS gamedig
WORKDIR /gamedig
COPY gamedig/package.json gamedig/package-lock.json ./
RUN npm ci --omit=dev

# Voice: Opus and libsodium from Ubuntu, Discord's libdave (named as NetCord loads them, next to the bot),
# libgomp for Whisper (loaded by the system's loader, so in its folder), and Piper for helper voices.
FROM ubuntu:noble AS voice
# Each download is checked against the sum taken with its version.
ARG LIBDAVE_VERSION=1.2.1
ARG LIBDAVE_SHA256=7fdf10a7df894406291463130a322c04d7de58085b602500866a11874e8907b4
ARG PIPER_VERSION=2023.11.14-2
ARG PIPER_SHA256=a50cb45f355b7af1f6d758c1b360717877ba0a398cc8cbe6d2a7a3a26e225992
ARG YTDLP_VERSION=2026.08.19
ARG YTDLP_SHA256=58162f9bfdc27458ea47bfcb311cf47028f17d8154a8bf7d689861d46399230a
ARG FFMPEG_BUILD=autobuild-2026-07-31-14-10
ARG FFMPEG_NAME=ffmpeg-n7.1.5-12-g1fdbca85aa-linux64-gpl-7.1
ARG FFMPEG_SHA256=c1e6caf48923dd8e6bc5e54d51ba70c321175b8162ae9c414c392990e72f0e79
ARG DENO_VERSION=2.9.7
ARG DENO_SHA256=c6527f24f4b16031d3ae4fa9f658d5f11534c8d84ce7dc8502420280919c3490
ARG POT_PROVIDER_VERSION=2.0.0
ARG POT_PROVIDER_SHA256=bce874dfa25896c2798e0f4f8147b7b22e785479eb1e459ab232bf2506c95016
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl unzip xz-utils libopus0 libsodium23 libgomp1 espeak-ng && rm -rf /var/lib/apt/lists/*
# Watching together: yt-dlp finds videos (Deno solves YouTube's challenges, the plugin asks the token helper),
# ffmpeg makes them playable in a browser.
RUN mkdir -p /opt/watch/yt-dlp-plugins /watch /tmp-app \
    && curl -fsSL -o /opt/watch/yt-dlp "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" \
    && echo "${YTDLP_SHA256}  /opt/watch/yt-dlp" | sha256sum -c - && chmod +x /opt/watch/yt-dlp \
    && curl -fsSL -o /opt/watch/yt-dlp-plugins/bgutil-ytdlp-pot-provider.zip "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/${POT_PROVIDER_VERSION}/bgutil-ytdlp-pot-provider.zip" \
    && echo "${POT_PROVIDER_SHA256}  /opt/watch/yt-dlp-plugins/bgutil-ytdlp-pot-provider.zip" | sha256sum -c - \
    && curl -fsSL -o /tmp/deno.zip "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-x86_64-unknown-linux-gnu.zip" \
    && echo "${DENO_SHA256}  /tmp/deno.zip" | sha256sum -c - \
    && unzip -j /tmp/deno.zip deno -d /opt/watch && rm /tmp/deno.zip \
    && curl -fsSL -o /tmp/ffmpeg.tar.xz "https://github.com/BtbN/FFmpeg-Builds/releases/download/${FFMPEG_BUILD}/${FFMPEG_NAME}.tar.xz" \
    && echo "${FFMPEG_SHA256}  /tmp/ffmpeg.tar.xz" | sha256sum -c - \
    && tar xJf /tmp/ffmpeg.tar.xz -C /tmp "${FFMPEG_NAME}/bin/ffmpeg" && mv "/tmp/${FFMPEG_NAME}/bin/ffmpeg" /opt/watch/ffmpeg && rm -rf /tmp/ffmpeg* \
    && cp -L /usr/lib/x86_64-linux-gnu/libz.so.1 /opt/watch/libz.so.1
RUN mkdir -p /voice /models /relay /keys /archive /lavalink-plugins \
    && curl -fsSL -o /tmp/dave.zip "https://github.com/discord/libdave/releases/download/v${LIBDAVE_VERSION}%2Fcpp/libdave-Linux-X64-boringssl.zip" \
    && echo "${LIBDAVE_SHA256}  /tmp/dave.zip" | sha256sum -c - \
    && unzip -j /tmp/dave.zip lib/libdave.so -d /voice && rm /tmp/dave.zip \
    && cp -L /usr/lib/x86_64-linux-gnu/libopus.so.0 /voice/libopus.so \
    && cp -L /usr/lib/x86_64-linux-gnu/libsodium.so.23 /voice/libsodium.so \
    && curl -fsSL -o /tmp/piper.tar.gz "https://github.com/rhasspy/piper/releases/download/${PIPER_VERSION}/piper_linux_x86_64.tar.gz" \
    && echo "${PIPER_SHA256}  /tmp/piper.tar.gz" | sha256sum -c - \
    && tar xzf /tmp/piper.tar.gz -C /opt && rm /tmp/piper.tar.gz
# eSpeak NG with its data and every library it links (the audio stack included, unused with --stdout), for a
# runtime image that has none of them.
RUN mkdir -p /opt/espeak/lib && cp /usr/bin/espeak-ng /opt/espeak/ && cp -r /usr/lib/x86_64-linux-gnu/espeak-ng-data /opt/espeak/ \
    && for lib in $(ldd /usr/bin/espeak-ng | awk '/=> \// {print $3}' | grep -v -E '/lib(c|m|pthread|dl|rt|gcc_s)\.so'); do cp -L "$lib" /opt/espeak/lib/; done

# The Activity (shown inside Discord's voice channels): built into the bot's web root.
FROM node:24-bookworm-slim AS activity
WORKDIR /activity
COPY activity/package.json activity/package-lock.json ./
RUN npm ci
COPY activity/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# The build's version (CI passes the release tag's number, or a prerelease naming the branch and commit).
ARG VERSION=0.0.0
WORKDIR /source
COPY Directory.Build.props .editorconfig ./
COPY src/THOBOTTO.csproj src/
RUN dotnet restore src/THOBOTTO.csproj -r linux-x64
COPY src/ src/
COPY --from=activity /src/wwwroot/activity src/wwwroot/activity
RUN dotnet publish src/THOBOTTO.csproj --no-restore -c Release -r linux-x64 --self-contained false -o /app -p:Version=$VERSION

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
# Lavalink's plugin jars are kept here for it; the stack shares this volume with Lavalink as its plugins folder.
COPY --from=voice --chown=1654:1654 /lavalink-plugins /data/lavalink-plugins
ENV Lavalink__PluginsDirectory=/data/lavalink-plugins
# The certificate the data protection keys are encrypted with lives here, outside the database; back it up with Postgres.
COPY --from=voice --chown=1654:1654 /keys /data/keys
ENV DataProtection__Directory=/data/keys
COPY --from=voice --chown=1654:1654 /archive /data/archive
ENV Archive__Folder=/data/archive
COPY --from=voice /opt/piper /opt/piper
ENV Speaking__Piper=/opt/piper/piper
COPY --from=voice /opt/espeak /opt/espeak
ENV Speaking__Espeak=/opt/espeak/espeak-ng
COPY --from=voice /opt/watch /opt/watch
ENV Watch__YtDlp=/opt/watch/yt-dlp Watch__Ffmpeg=/opt/watch/ffmpeg Watch__Deno=/opt/watch/deno Watch__Directory=/data/watch DENO_DIR=/data/tmp/deno
COPY --from=voice --chown=1654:1654 /watch /data/watch
# yt-dlp unpacks itself into the temp folder; the image has none of its own.
COPY --from=voice --chown=1654:1654 /tmp-app /data/tmp
ENV TMPDIR=/data/tmp
# Where the bot's releases are published (the repository CI built from), so the status board can say when a newer one is out.
ARG SOURCE=
ENV Releases__Repository=$SOURCE
WORKDIR /app
COPY --from=voice /voice/ .
COPY --from=build /app .
HEALTHCHECK --interval=30s --timeout=10s --start-period=2m --retries=3 CMD ["dotnet", "THOBOTTO.dll", "--health"]
ENTRYPOINT ["dotnet", "THOBOTTO.dll"]
