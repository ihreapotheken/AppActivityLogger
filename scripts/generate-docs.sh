#!/usr/bin/env bash
#
# Regenerates everything under docs/ — OpenAPI specs + DocFX HTML site.
#
# Layout produced:
#   docs/openapi/ingestion-v1.json   ← Swashbuckle-CLI dump from ReportService.dll
#   docs/openapi/ingestion-v1.yaml   ← same, YAML
#   docs/api/                        ← DocFX-extracted YAML metadata per project
#   docs/_site/                      ← final navigable HTML site (open _site/index.html)
#
# Prerequisites: the .NET 8 SDK on PATH. Local tools (Swashbuckle.AspNetCore.Cli + docfx) are
# pinned in .config/dotnet-tools.json and restored automatically.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCS_DIR="$REPO_DIR/docs"

cd "$REPO_DIR"

# 1. Ensure dotnet is reachable. If the user installed via dotnet-install.sh, ~/.dotnet should
#    already be on PATH (we put it there in ~/.zshenv). Belt-and-braces fallback for non-zsh
#    shells / CI:
if ! command -v dotnet >/dev/null 2>&1; then
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        export DOTNET_ROOT="$HOME/.dotnet"
        export PATH="$DOTNET_ROOT:$PATH"
    else
        echo "error: dotnet not found on PATH and not at \$HOME/.dotnet" >&2
        echo "       install the .NET 8 SDK (see scripts/setup.sh)" >&2
        exit 1
    fi
fi

echo "==> Restoring local dotnet tools (Swashbuckle.AspNetCore.Cli + docfx)"
dotnet tool restore

echo "==> Building solution (Release)"
dotnet build ReportService.sln -c Release --nologo

echo "==> Exporting OpenAPI spec from ReportService.dll"
mkdir -p "$DOCS_DIR/openapi"
INGEST_DLL="$REPO_DIR/src/ReportService/bin/Release/net8.0/ReportService.dll"
[ -f "$INGEST_DLL" ] || { echo "error: $INGEST_DLL not found — did the build fail?" >&2; exit 1; }

# Swashbuckle.CLI invokes Program.Main to build the in-memory host. Force Development so
# SecretValidation skips the production-secret check (we don't want to wire fake keys just to
# extract the spec — we never make a request, only inspect the route table).
export ASPNETCORE_ENVIRONMENT=Development
dotnet swagger tofile \
    --output "$DOCS_DIR/openapi/ingestion-v1.json" \
    "$INGEST_DLL" v1
dotnet swagger tofile --yaml \
    --output "$DOCS_DIR/openapi/ingestion-v1.yaml" \
    "$INGEST_DLL" v1

echo "==> Generating DocFX site under docs/_site/"
# DocFX writes into the current directory by default; cd into docs/ so its relative paths line up
# with docs/docfx.json.
( cd "$DOCS_DIR" && dotnet docfx docfx.json )

echo
echo "Generated artifacts:"
echo "  - $DOCS_DIR/openapi/ingestion-v1.json"
echo "  - $DOCS_DIR/openapi/ingestion-v1.yaml"
echo "  - $DOCS_DIR/api/        (DocFX YAML metadata)"
echo "  - $DOCS_DIR/_site/      (HTML site — open _site/index.html)"
