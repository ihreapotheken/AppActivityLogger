# report-service

A .NET 8 ASP.NET Core service that ingests Report-a-Problem submissions from mobile SDKs and a
JSON API, stores reports as files (with a SQLite metadata index), and exposes a CMS-style admin
UI for browsing and managing them.

## Where to start

- [Architecture overview](architecture.md) — components, resilience model, data flow.
- API reference — pick a project from the **API reference** entry in the top navigation (the
  pages there are auto-generated from XML doc comments by DocFX).
- OpenAPI spec — `openapi/ingestion-v1.json` / `openapi/ingestion-v1.yaml` (load into Swagger UI,
  Insomnia, Postman, or Redocly).

## Two ingest paths

| Endpoint | Auth | Body | When to use |
|---|---|---|---|
| `POST /partners/api/v2/report-problem` | `apiKey` header | `multipart/form-data` (json + optional gzip) | Android / iOS IA SDKs |
| `POST /api/v1/reports` | `apiKey` header | `application/json` (single `RSCProblemReport` document) | Server-to-server, webhooks, partner integrations |

Both paths share the same validation, storage, idempotency, and rate-limiting. The persisted row
is tagged with `ingestion_channel` so the admin UI can tell them apart.

## Generating these docs

```bash
./scripts/generate-docs.sh
```

See [README](README.md) for the layout.
