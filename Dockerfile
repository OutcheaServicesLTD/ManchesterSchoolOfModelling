# Manchester School of Modelling — container image.
#
# Deliberately host-agnostic: it runs the same on Render, Fly, Azure Container Apps or a
# plain Linux server, so choosing a host now does not lock the project in.

# ── Build ─────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Project files first, so a change to source code does not invalidate the restore layer
# and force every package to download again.
COPY global.json ./
COPY src/Msm.Portfolio.Web/Msm.Portfolio.Web.csproj src/Msm.Portfolio.Web/
RUN dotnet restore src/Msm.Portfolio.Web/Msm.Portfolio.Web.csproj

COPY src/ src/
RUN dotnet publish src/Msm.Portfolio.Web/Msm.Portfolio.Web.csproj \
    -c Release \
    -o /app \
    --no-restore

# ── Run ───────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SkiaSharp draws the image renditions and needs a font configuration present, plus
# curl for the container health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Writable state lives here. Mount a volume at this path: on a host that rebuilds
# containers, anything outside it is discarded — which for this application means
# every client's photographs.
RUN mkdir -p /data/media
VOLUME ["/data"]

# Runs unprivileged. A process that never needs root should never have it, and this one
# accepts file uploads from the internet.
RUN useradd --create-home --uid 10001 msm \
    && chown -R msm:msm /app /data
USER msm

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Database__Provider=Sqlite \
    Database__ConnectionString="Data Source=/data/msm-portfolio.db" \
    Media__LocalStorageRoot=/data/media

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${PORT:-8080}/health" || exit 1

# Most platforms hand the port over in PORT rather than letting the image choose, so it
# is honoured here and 8080 is only the fallback.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet Msm.Portfolio.Web.dll --urls http://0.0.0.0:${PORT:-8080}"]
