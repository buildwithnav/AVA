# Ava.MetaQuery

Oracle SQL-execution API (built on the **CDS.Codex** .NET 9 framework), integrated
into the AVA solution as `Ava.MetaQuery.*`:

| Project | Purpose |
|---|---|
| `Ava.MetaQuery.Api` | Web API (`/cds_api`, `/cds_api/sql`, Swagger, `/health`) |
| `Ava.MetaQuery.Business` | Services, models, AutoMapper |
| `Ava.MetaQuery.Data` | EF Core `MetaQueryContext` (Oracle) |
| `Ava.MetaQuery.Tests` | xUnit tests |

Runs on **http://localhost:5140** (Swagger at `/swagger`). Started automatically by
`..\start.ps1`; published by `..\publish.ps1`.

---

## ⚠️ Local development: authentication bypass required for testing

**All `/cds_api/*` endpoints are protected by a global CDS.Codex JWT authorize
filter.** There is **no local login/token endpoint** and tokens are issued by the
enterprise identity provider — so when running locally, every controller endpoint
returns **`401 Unauthorized`** unless you present a valid enterprise token.

Only `GET /health` is anonymous (added for launcher/health orchestration).

### To exercise the endpoints locally (no token)

Temporarily mark **both** controllers `[AllowAnonymous]`, then run:

- `Ava.MetaQuery.Api/Controllers/MetaQueryController.cs`
- `Ava.MetaQuery.Api/Controllers/SqlExecutionController.cs`

```csharp
using Microsoft.AspNetCore.Authorization;   // add this using

[Route("cds_api")]
[ApiController]
[AllowAnonymous]                             // TEMP: local testing only
public class MetaQueryController : CodexBaseController
```

> 🔴 **This is a temporary, local-only measure. REVERT it before committing or
> deploying** — shipping `[AllowAnonymous]` would expose the SQL-execution API
> without authentication. (For reference, the original author used this same
> approach when validating the endpoints against `CAP.ST_EVENTS`.)

An alternative that does not touch source is to call the API with a valid
`Authorization: Bearer <token>` header obtained from the enterprise auth flow.

---

## Degraded (no-Oracle) mode

`Program.cs` resolves the Oracle `APP_USER` connection string via CDS.Codex. If it
is unavailable (no Oracle/Codex configured locally), the app **logs a warning and
still boots** so `/health` responds; Oracle-backed endpoints will fail until a real
connection string is supplied. This keeps the AVA launcher non-blocking.

---

## Endpoints

`/cds_api/sql/*` (dynamic read-only Oracle SQL execution — see below) and the
template `/cds_api/*` endpoints. Provide object names as `SCHEMA.OBJECT`
(e.g. `CAP.ST_EVENTS`).

| Method | Route | Notes |
|---|---|---|
| GET  | `/health` | Anonymous liveness probe |
| POST | `/cds_api/sql/execute` | `{ "query": "SELECT ...", "maxRows": 100 }` (read-only enforced) |
| POST | `/cds_api/sql/execute/paged` | `{ "query": "SELECT ... ORDER BY ...", "page": 1, "pageSize": 50 }` |
| GET  | `/cds_api/sql/select?objectName=CAP.ST_EVENTS&maxRows=100` | `SELECT *` with row cap |
| GET  | `/cds_api/sql/select/paged?objectName=CAP.ST_EVENTS&page=1&pageSize=50` | Paged `SELECT *` |
| GET  | `/cds_api/sql/metadata?objectName=CAP.ST_EVENTS` | Column metadata; `404` if unknown |

Read-only enforcement, injection guards, row caps, and LOB handling are described
in the SQL execution design notes in the source repository.
