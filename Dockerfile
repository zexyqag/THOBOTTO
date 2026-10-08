FROM node:24-bookworm-slim AS gamedig
WORKDIR /gamedig
COPY gamedig/package.json gamedig/package-lock.json ./
RUN npm ci --omit=dev

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/THOBOTTO.csproj src/
RUN dotnet restore src/THOBOTTO.csproj
COPY src/ src/
RUN dotnet publish src/THOBOTTO.csproj --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled
COPY --from=gamedig /usr/local/bin/node /usr/local/bin/node
COPY --from=gamedig /gamedig /opt/gamedig
ENV GameDig__Path=/opt/gamedig
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "THOBOTTO.dll"]
