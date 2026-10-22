# Ava.Api

Backend API for AVA.

## User tracking (Pylon JWT)

Every request's user identity is resolved **server-side** from the Pylon-issued JWT,
not from anything the client sends in the request body.

- **Middleware** (`Program.cs`, right after `UseCors`) decodes the `Authorization: Bearer <jwt>`
  header. Pylon is a trusted upstream gateway, so the token signature is **not** validated —
  we only base64url-decode the payload and read two claims:
  - `nameid` → `UserId`
  - `unique_name` → `Username`
- The resolved identity is stashed in `HttpContext.Items` and read via
  `HttpContext.GetPylonUser()` (see `Helpers/PylonUser.cs`).
- Both `UserId` and `Username` are persisted to the `usage_events` and `conversations`
  tables (columns `user_id` / `username`).

### Enforcement

The user-tracked **write** endpoints reject requests without a resolved user (HTTP **401**):

- `POST /api/chat`
- `POST /api/prompt/execute`

All other endpoints (auth/bootstrap/read) are intentionally left public.

## ⚠️ Local development — authentication

There is **no Pylon gateway in front of the API when running locally**, so requests carry
no JWT and would otherwise get a 401 on the endpoints above.

To keep local runs working, the middleware **injects a configurable dev user** when either:

- the app runs in the **Development** environment (`ASPNETCORE_ENVIRONMENT=Development`,
  which `start.ps1` sets), **or**
- `Ava:LocalDev:BypassAuth` is truthy (`true` / `1` / `yes`).

The injected dev user is configurable (defaults shown):

```jsonc
// appsettings.Development.json
"Ava": {
  "LocalDev": {
    "BypassAuth": true,
    "UserId": "0",
    "Username": "local.dev"
  }
}
```

**This bypass only applies locally.** In non-Development environments no fallback user is
injected, so a valid Pylon JWT is required for `/api/chat` and `/api/prompt/execute`.

> **Note for other developers / LLMs:** if you are running AVA locally and getting `401`
> on chat or prompt execution, make sure `ASPNETCORE_ENVIRONMENT=Development` (or set
> `Ava:LocalDev:BypassAuth=true`). Do **not** remove the JWT enforcement to work around
> this — the local dev-user injection is the supported mechanism.
