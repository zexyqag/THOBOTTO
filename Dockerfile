FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/THOBOTTO.csproj src/
RUN dotnet restore src/THOBOTTO.csproj
COPY src/ src/
RUN dotnet publish src/THOBOTTO.csproj --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "THOBOTTO.dll"]
