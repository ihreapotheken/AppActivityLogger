# syntax=docker/dockerfile:1.6

# ---------- Build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Directory.Build.props carries the build-time feature flags (FeatureAnalytics / FeatureProblemReports
# → FEATURE_* compile constants). It's auto-imported by every project during restore + build, so it
# MUST be present before either runs — otherwise the FEATURE_* constants are undefined and every
# optional feature compiles OFF (endpoints 500 / admin pages show "not enabled").
COPY Directory.Build.props ./

# Copy project files first for better NuGet layer caching. Restore per project rather than
# through the solution — the solution references tests/ which is intentionally not in the image.
COPY src/ReportService.Core/ReportService.Core.csproj src/ReportService.Core/
COPY src/ReportService/ReportService.csproj src/ReportService/
COPY src/ReportService.Admin/ReportService.Admin.csproj src/ReportService.Admin/
RUN dotnet restore src/ReportService/ReportService.csproj \
 && dotnet restore src/ReportService.Admin/ReportService.Admin.csproj

# Copy the rest of the sources and publish both apps.
COPY src/ src/
# README.md + the modular docs/guide/ chapters are bundled into the admin image for the
# /Documentation preview page. The admin csproj declares them as <Content> with relative paths
# resolving to /src/README.md and /src/docs/guide/*.md here.
COPY README.md ./README.md
COPY docs/guide/ docs/guide/
RUN dotnet publish src/ReportService       -c Release -o /app/ingestion --no-restore /p:UseAppHost=false
RUN dotnet publish src/ReportService.Admin -c Release -o /app/admin     --no-restore /p:UseAppHost=false

# ---------- Shared runtime base ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime-base

# Unprivileged user + writable data dir so the root filesystem can stay read-only.
RUN (getent group app >/dev/null || addgroup -S app) \
    && (id -u app >/dev/null 2>&1 || adduser -S -G app -u 10001 app) \
    && mkdir -p /srv/reports && chown -R app:app /srv/reports

WORKDIR /app
USER app

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_PRINT_TELEMETRY_MESSAGE=false \
    ReportService__ReportsRoot=/srv/reports

# ---------- Merged runtime ----------
# The admin process now hosts both the SDK-facing ingestion routes and the operator dashboard
# in one ASP.NET Core process — multi-scheme auth (Cookie for Razor pages, ApiKey for ingestion
# endpoints) keeps the two surfaces correctly gated. The "ingestion" target below is retained
# for back-compat with anyone deploying the standalone process; docker-compose only runs this
# merged target.
FROM runtime-base AS admin

COPY --from=build /app/admin ./

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "ReportService.Admin.dll"]

# ---------- Ingestion runtime (default target) ----------
FROM runtime-base AS ingestion

COPY --from=build /app/ingestion ./

# HTTP only inside the container; TLS is terminated by the reverse proxy.
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

# The alpine aspnet base ships BusyBox, which provides wget.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "ReportService.dll"]
