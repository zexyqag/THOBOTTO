FROM node:24-bookworm-slim AS gamedig
WORKDIR /gamedig
COPY gamedig/package.json gamedig/package-lock.json ./
RUN npm ci --omit=dev

# Voice: Opus and libsodium from Ubuntu, Discord's libdave (named as NetCord loads them, next to the bot),
# and libgomp for Whisper (loaded by the system's loader, so in its folder).
FROM ubuntu:noble AS voice
ARG LIBDAVE_VERSION=1.2.1
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl unzip libopus0 libsodium23 libgomp1 && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /voice /models \
    && curl -fsSL -o /tmp/dave.zip "https://github.com/discord/libdave/releases/download/v${LIBDAVE_VERSION}%2Fcpp/libdave-Linux-X64-boringssl.zip" \
    && unzip -j /tmp/dave.zip lib/libdave.so -d /voice \
    && cp -L /usr/lib/x86_64-linux-gnu/libopus.so.0 /voice/libopus.so \
    && cp -L /usr/lib/x86_64-linux-gnu/libsodium.so.23 /voice/libsodium.so

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/THOBOTTO.csproj src/
RUN dotnet restore src/THOBOTTO.csproj
COPY src/ src/
RUN dotnet publish src/THOBOTTO.csproj --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
COPY --from=gamedig /usr/local/bin/node /usr/local/bin/node
COPY --from=gamedig /gamedig /opt/gamedig
ENV GameDig__Path=/opt/gamedig
COPY --from=voice /usr/lib/x86_64-linux-gnu/libgomp.so.1 /usr/lib/x86_64-linux-gnu/
# Whisper models download here; a volume keeps them across deploys (owned by the image's app user).
COPY --from=voice --chown=1654:1654 /models /data/models
ENV Listening__Models=/data/models
WORKDIR /app
COPY --from=voice /voice/ .
COPY --from=build /app .
ENTRYPOINT ["dotnet", "THOBOTTO.dll"]
