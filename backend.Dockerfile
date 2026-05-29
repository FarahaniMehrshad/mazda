FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/Mazda.sln ./
COPY backend/src ./src
RUN dotnet restore Mazda.sln
RUN dotnet publish src/Mazda.Api/Mazda.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

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
