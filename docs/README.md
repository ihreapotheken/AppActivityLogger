# report-service documentation

Everything in this directory is either hand-written project orientation (tracked in version control)
or **generated** by `scripts/generate-docs.sh` at the repo root (gitignored — regenerate as needed).

## Layout

```
docs/
├── README.md          # ← you are here
├── index.md           # DocFX landing page
├── architecture.md    # narrative overview that complements the top-level README
├── docfx.json         # DocFX config (metadata + build pipeline)
├── toc.yml            # site-level table of contents
│
├── api/               # generated: per-project API metadata (DocFX YAML)
├── _site/             # generated: full HTML documentation site
└── openapi/           # generated: OpenAPI specs (JSON + YAML)
```

## Regenerating

From the repo root:

```bash
./scripts/generate-docs.sh
```

The script runs `dotnet tool restore` first, so the only prerequisite on a fresh clone is the
.NET 8 SDK.

It performs three steps:

1. `dotnet build -c Release` — produces the assemblies + XML doc files DocFX consumes.
2. `dotnet swagger tofile` (Swashbuckle CLI) — exports the live OpenAPI spec from the ingestion
   service to `openapi/ingestion-v1.json` and `openapi/ingestion-v1.yaml`.
3. `dotnet docfx docs/docfx.json` — pulls API metadata from every `*.csproj` under `src/` and
   builds an HTML site at `_site/index.html`.

## Reading the generated artifacts

- **API reference site** — open `docs/_site/index.html` in a browser. The site has a sidebar TOC
  per project (Core / ingestion / Admin) and renders the `<summary>` / `<param>` / `<returns>`
  XML doc comments.
- **OpenAPI spec** — load `docs/openapi/ingestion-v1.json` into Swagger UI / Insomnia / Postman /
  Redocly. The spec covers every public route on the ingestion service, including the multipart
  `/partners/api/v2/report-problem` and the JSON-API `/api/v1/reports`.

## Hand-written guides

- [architecture.md](architecture.md) — high-level component map and resilience model.
- The repo-root README is still the operations / deployment / config reference (lives outside this site).
