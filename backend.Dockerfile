# syntax=docker/dockerfile:1.6
# ↑ Enables BuildKit's --mount=type=cache for persisting the NuGet package
#   directory across image rebuilds. Docker Compose v2 uses BuildKit by
#   default; if you're stuck on the legacy builder, set DOCKER_BUILDKIT=1.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Tell NuGet exactly where its global packages folder lives so the cache
# mount below targets the right directory. (Defaults to ~/.nuget/packages.)
ENV NUGET_PACKAGES=/root/.nuget/packages \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# ── Restore layer (cached unless a .csproj or the .sln changes) ──
# Copy ONLY the project manifests first. `dotnet restore` reads .sln/.csproj
# and pulls every NuGet package — by isolating it from the rest of the source
# tree we get a Docker layer-cache hit whenever the dependency graph is
# unchanged (i.e. almost every code edit), so restore turns into a no-op.
#
# The --mount=type=cache on /root/.nuget/packages persists the package cache
# across BUILDS too, not just across image LAYERS — so when the .csproj DOES
# change, only the new packages are downloaded; everything previously
# restored stays warm. The cache survives `docker system prune` (it lives in
# BuildKit's separate cache store) and is shared by every build that names
# the same target.
COPY backend/Mazda.sln ./
COPY backend/src/Mazda.Api/Mazda.Api.csproj                       src/Mazda.Api/
COPY backend/src/Mazda.Application/Mazda.Application.csproj       src/Mazda.Application/
COPY backend/src/Mazda.Domain/Mazda.Domain.csproj                 src/Mazda.Domain/
COPY backend/src/Mazda.Infrastructure/Mazda.Infrastructure.csproj src/Mazda.Infrastructure/
RUN --mount=type=cache,id=mazda-nuget,target=/root/.nuget/packages \
    dotnet restore Mazda.sln

# ── Build layer (re-runs on any source change, uses the cached restore) ──
# The same cache mount is wired into publish so the build can resolve the
# packages restored above. `--no-restore` skips the duplicate restore pass.
COPY backend/src ./src
RUN --mount=type=cache,id=mazda-nuget,target=/root/.nuget/packages \
    dotnet publish src/Mazda.Api/Mazda.Api.csproj -c Release -o /app/publish \
        /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# ── Node + Playwright (test runner only; no browser binaries — we attach via CDP) ──
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && rm -rf /var/lib/apt/lists/* \
 && npm install -g @playwright/test@1.48.0 \
 && node -v && npx playwright --version

COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080
ENV MAZDA_RUNNER_WORKDIR=/var/mazda/runs
RUN mkdir -p /var/mazda/runs
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mazda.Api.dll"]
