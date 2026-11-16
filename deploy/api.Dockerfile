# Build context: the repository root (see deploy/docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached while only code changes).
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/backend ./src/backend
RUN dotnet publish src/backend/MevaCare.AI.HealthCompanion.Api -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /out ./

# Listens on 8080 inside the container (the image's default for .NET 8+). Runs as the image's non-root user.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_GCHeapHardLimitPercent=60
USER app

# Arguments after the image name reach the API, so the maintenance commands work too:
#   docker compose run --rm api import-openfoodfacts /data/off/<file>.csv.gz romania,germany
ENTRYPOINT ["dotnet", "MevaCare.AI.HealthCompanion.Api.dll"]
