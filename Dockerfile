# syntax=docker/dockerfile:1

# ---- build -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first: this layer stays cached until a project file or a package version changes.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Curto.Api/Curto.Api.csproj src/Curto.Api/
RUN dotnet restore src/Curto.Api/Curto.Api.csproj

COPY src/Curto.Api/ src/Curto.Api/
RUN dotnet publish src/Curto.Api/Curto.Api.csproj -c Release --no-restore -o /app

# The chiseled runtime image has no shell, so the data directory is prepared here. The placeholder makes sure the
# directory is not empty, so it is always carried into the runtime image.
RUN mkdir /data && touch /data/.keep

# ---- runtime ---------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, runs as the unprivileged "app" user (UID 1654) by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime

LABEL org.opencontainers.image.title="curto" \
      org.opencontainers.image.description="Self-hosted URL shortener with privacy-first live analytics" \
      org.opencontainers.image.source="https://github.com/q6r/curto" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app
COPY --from=build /app ./

# The only writable location: the SQLite file and its -wal/-shm companions live here. Mount a volume on it.
COPY --from=build --chown=1654:1654 /data /data
VOLUME /data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    CURTO_DB_PATH=/data/curto.db

USER 1654
EXPOSE 8080

# The image has neither curl nor wget: the application probes itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["./Curto.Api", "--healthcheck"]

ENTRYPOINT ["./Curto.Api"]
