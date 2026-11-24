# DocVault API

A document storage REST API built with **.NET 8**, **SQL Server stored procedures (ADO.NET)** and **Azure Blob Storage**.
It handles file upload, versioning, duplicate detection, secure download links and a full audit trail.

## Features

- **Upload with validation** — size limit, allowed file types, file name cleaning
- **Versioning** — every upload of the same document becomes a new version; old versions are never overwritten
- **Duplicate detection** — SHA-256 hash of each file; the same file is rejected with `409 Conflict` unless `allowDuplicate=true`
- **Two download options**
  - stream through the API (supports HTTP range requests for resume)
  - short-lived, read-only **SAS link** straight to Blob Storage, so big files don't use API bandwidth
- **Audit log** — who uploaded, downloaded, added a version or deleted, and when
- **Soft delete** — documents are hidden, not destroyed
- **Keyset pagination** — page speed stays the same on page 1 or page 10,000
- **Consistency between SQL and Blob Storage** — if the database step fails after upload, the blob is deleted, so no orphan files are left
- **Safe concurrent versioning** — `UPDLOCK, HOLDLOCK` in the stored procedure stops two uploads from getting the same version number
- **Consistent errors** — all errors return RFC 7807 `ProblemDetails` with a trace id

## Architecture

```
Client ──► DocumentsController ──► DocumentService ──┬──► DocumentRepository ──► SQL Server (stored procedures)
                                                      └──► BlobStorageService ──► Azure Blob Storage (private container)
```

| Layer | Responsibility |
|-------|----------------|
| Controller | HTTP: routes, status codes, file responses |
| Service | Business rules: validation, hashing, duplicate check, blob ↔ SQL consistency |
| Repository | ADO.NET calls to stored procedures with explicitly typed parameters |
| Blob service | Upload, stream, SAS link, delete |

### Design decisions

- **Stored procedures + ADO.NET** instead of an ORM: full control of SQL and plans.
  Every `SqlParameter` has an explicit `SqlDbType` and size (no `AddWithValue`), which avoids
  implicit conversions and keeps plan reuse stable.
- **Clustered index on an identity `SeqId`**, GUID as a nonclustered primary key: avoids page splits
  from random GUID inserts, and `SeqId` is also the pagination cursor.
- **Filtered index** (`WHERE IsDeleted = 0`) — smaller and faster, because deleted rows are never listed.
- **Unique blob name per upload** (`{documentId}/{guid}.ext`) — no overwrites, no race conditions.
- **Private container** — files are only reachable through the API or a link that expires in minutes.

## API

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/documents` | Upload a new document (multipart: `file`, `title`, `category`, `allowDuplicate`) |
| POST | `/api/documents/{id}/versions` | Upload a new version (multipart: `file`) |
| GET | `/api/documents?category=&search=&afterSeqId=&pageSize=` | List (keyset paging) |
| GET | `/api/documents/{id}` | Document + all versions |
| GET | `/api/documents/{id}/download?version=` | Stream file (latest if no version) |
| GET | `/api/documents/{id}/download-link?version=` | Short-lived SAS URL |
| DELETE | `/api/documents/{id}` | Soft delete |
| GET | `/api/documents/{id}/audit` | Audit history |
| GET | `/health` | Checks SQL Server and Blob Storage |

Send an `X-User-Name` header to record who did the action (demo only — see "Next steps").

## Run locally

Requirements: .NET 8 SDK and Docker.

```bash
# 1. Start SQL Server and Azurite (local Azure Storage emulator)
cp .env.example .env
docker compose up -d

# 2. Create the database and stored procedures
sqlcmd -S localhost,1433 -U sa -P "ChangeMe_Local123!" -C -i database/01_schema.sql
sqlcmd -S localhost,1433 -U sa -P "ChangeMe_Local123!" -C -i database/02_procedures.sql

# 3. Run the API
cd src/DocVault.Api
dotnet run
```

Open Swagger at the URL printed in the console (`/swagger`).

### Try it with curl

```bash
# Upload
curl -X POST http://localhost:5000/api/documents \
  -H "X-User-Name: nishanth" \
  -F "file=@report.pdf" -F "title=Q3 Report" -F "category=Finance"

# New version
curl -X POST http://localhost:5000/api/documents/{id}/versions \
  -H "X-User-Name: nishanth" -F "file=@report-v2.pdf"

# Get a download link
curl http://localhost:5000/api/documents/{id}/download-link
```

To use real Azure Storage, replace `Storage:ConnectionString` with your storage account connection string
(use User Secrets or environment variables — never commit real keys).

## Next steps

- JWT authentication (Azure AD / Entra ID) instead of the `X-User-Name` header
- Virus scanning before a file becomes downloadable
- Integration tests with Testcontainers (SQL Server + Azurite)
- Blob lifecycle rules to move old versions to the Cool/Archive tier
