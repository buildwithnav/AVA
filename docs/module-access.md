# Module Access (Domains & Child Domains)

AVA gates access with the same pattern as the CDS Angular Template: a user holds a set of
**modules** with `browse` / `update` / `insert` / `delete` flags, fetched from the CDO Gateway.

AVA adds a second tier. Domains map to gateway modules; a domain's **children** (e.g. individual
studies under *Clinical Studies*) are gated by AVA-native grants instead, because registering
50+ study module keys with CDS isn't practical. Children are full `domains` rows with a
self-referencing `parent_id` — nesting is capped at 2 tiers, so a child can never itself have
children.

```
CDO Gateway module  ──►  domain (domains.module_key, parent_id IS NULL)
                              │
                              ▼
        AVA grant (domain_grants)  ──►  child domain (a study; domains.parent_id set)
```

**Effective access = parent domain module AND child-domain grant.** A grant can never widen
access to a domain the user wasn't given. The `MresjWO` (super user) module bypasses both.

## Module keys

App-level keys are compile-time constants — [ModuleKeys](/AVA/ava-api/Ava.Api/Models/SecurityModels.cs)
in C#, [ModuleKey](/AVA/ava-orchestration-ui/src/app/models/module-key.enum.ts) in Angular:

| Key | Gates |
|---|---|
| `MresjWO` | Super user — bypasses every check |
| `MoU6LMc` | Domain & child-domain administration |
| `MwdTPIB` | Workflow builder page |
| `MO3OxHd` | Tools page |
| `MCT5pUl` | Data templates page |
| `Mqr9XUr` | Usage analytics |

Domain keys are **not** in the enum. Domains are database rows and studies are created at
runtime, so their keys live in `domains.module_key` and are resolved per request. This is the
one deliberate divergence from the Angular Template. Module keys are ignored for child domains —
those are gated by grants instead (see below).

## Local development

The API short-circuits to a local grant set — no gateway or CAS cookie needed. Both UIs read
that API security context, so the sidebar, routes, and API use the same access flags.

- **API**: `Ava:Security:Enabled = false` (default in `appsettings.json`) returns
  `Ava:Security:LocalModules` and identifies the caller as `Ava:Security:LocalUserId`.

### Testing denial paths locally — no gateway required

Both layers support the same workflow the Angular Template uses: edit a flag, see the change
take effect, no restart.

1. Disable the `MresjWO` entry's `Browse` flag in `LocalModules` (in `appsettings.json`). While
   `MresjWO`'s `Browse` is `true` (the default), the super-user bypass wins over every module.
2. With it `false`, access is decided purely by the `Browse`/`Update`/`Insert`/`Delete` flags on
   each entry in `LocalModules` — flip one and save. The API and UI use the update on their next
   security-context request; refresh the browser if the UI is already running.
3. Every domain that should be visible locally needs its module key present in `LocalModules`
   (the appsettings.json ships with all current domains' keys pre-populated — see the table
   below). A domain whose key is missing from `LocalModules` — or present with `Browse: false` —
   disappears from `/api/domains` and 403s on direct access, exactly as it would in production
   for a user without that module.
4. `LocalGrantAllSubDomains` (default `true`) is the equivalent switch for child-domain grants —
   set `false` to make real `domain_grants` rows for `LocalUserId` the deciding factor
   instead of an automatic allow.

`ava-web` (the embedded chat) has no separate local mode — it always calls
`/api/security/context`, so its behavior is driven entirely by the API's `Security:Enabled` /
`LocalModules` settings above.

To run the API as if deployed:

```powershell
$env:Ava__Security__Enabled = "true"
$env:Ava__Security__AppKey = "LzP9cRG"
$env:Ava__Security__GatewayBaseUrl = "https://cdstest.abbott.com/core/api"
dotnet run
```

With security enabled and no `cas-at` cookie, every domain returns 403 — that's correct
fail-closed behavior. Pass `X-Ava-User: <id>` to simulate a user when testing against a mock
gateway.

## Registering a new domain

1. Ask the CDS admin to register a module key against AVA's appKey (e.g. `AVACLS` for
   Clinical Studies) and grant it to the relevant users.
2. In the orchestration UI, open **Domains**, select the domain, and set **Module Key** to
   that value.
3. Verify with `GET /api/security/context` — the domain should appear in `domains[]` with the
   right `browse`/`update`/`insert`/`delete` flags.

Until a domain has a module key it's governed by `Ava:Security:AllowUnmappedDomains`:

- `true` (default) — unmapped domains are visible to everyone. Use during rollout.
- `false` — unmapped domains are invisible. **Switch to this once every domain has a key.**

### Current domain key assignments

Every domain that existed at the time this feature was built has a key already set (and mirrored
into `LocalModules` for local testing). Most have since been registered with the CDS gateway team
and carry their real key; the remaining ones are still placeholders — **replace those with the
real keys once the CDS gateway team registers them, then update `LocalModules` to match:**

| Domain ID | Name | Module key |
|---|---|---|
| `analyze-study` | Analyze Study | `MxMthfJ` |
| `ava-prompts` | AVA Prompts | `MdGlYAK` |
| `ado` | Azure DevOps | `MOFeCFv` |
| `clinical-data` | Clinical Data | `MQSMhHZ` |
| `ctms` | CTMS | `M2u0e9Q` |
| `mon-log` | Monitoring Log | `M4ecL7l` |
| `study-pi-insight` | Study Physician Insight | `MFxqRAV` |
| `support` | Support | `MtfeXmi` |
| `InternalTesting` | Internal Testing | `Mqp1yZl` |
| `testing-analytics` | Testing analytics | `M6aLTHE` |

Any new domain created after this point starts with `moduleKey = null` (unmapped) — assign it
via the Domains page as described above.

## Managing child domains and grants

Under **Domains → Child Domains & Access** in the orchestration UI (only shown for top-level
domains — nesting is capped at 2 tiers):

- Click **+ Add Child Domain** to create a child (e.g. a study) — it opens the same domain
  editor used for top-level domains, pre-seeded with the parent, so a child gets the full set
  of fields (Description, Agentic Instruction, Knowledge Bases, Files) instead of a stripped-down
  form. Children show an **External Ref** field (e.g. the study ID) instead of a Module Key —
  module keys are ignored for children since they're gated by grants, not the CDS gateway.
- Grant a user access to one child domain, or choose "All child domains" to cover every child
  under that parent with a single wildcard grant.
- Grants carry their own browse/update/insert/delete flags, intersected with the parent's.

## Enforcement points

| Layer | Mechanism |
|---|---|
| Any endpoint with a `domainId` parameter | [DomainAccessFilter](/AVA/ava-api/Ava.Api/Services/Security/DomainAccessFilter.cs) — global MVC filter, infers required access from the HTTP verb |
| Domain / child-domain / grant actions (all in [DomainController](/AVA/ava-api/Ava.Api/Controllers/DomainController.cs)) | Explicit `AccessService` calls (they self-authorize; the filter skips this controller) |
| `POST /api/chat` | `AuthorizeContextAsync` — overwrites the client's `userId` with the server-resolved identity and 403s on a domain the caller can't browse |
| `POST /api/chat/detect-domain` | Only considers accessible domains, so detection can't reveal that a domain exists |
| Angular routes | `authGuard` reads `route.data['moduleKey']` (set per-route in `app.routes.ts`) |
| Angular nav | `Shell` filters its own nav array via `SecurityService.getModuleAccess()` |

UI filtering is cosmetic — the API is the enforcement point. Never rely on a hidden button.

### File layout

Identity resolution, gateway module fetch, and access evaluation all live in one concrete
class — [AccessService](/AVA/ava-api/Ava.Api/Services/Security/AccessService.cs)
— registered directly (no interface), matching how the rest of Ava.Api's services
(`AgentService`, `ConversationService`, ...) are registered. Child domains and their grants share
one repository ([PostgresDomainRepository](/AVA/ava-api/Ava.Api/Data/PostgresDomainRepository.cs))
with top-level domains — children are just `domains` rows with `parent_id` set, and a grant is
meaningless without its child domain. All models live in the existing
`AvaModels.cs` / `orchestration.ts` / `chat.models.ts` files rather than new ones — each app
already had a "one file holds all models" convention before this feature.

## Known gaps

- **Group grants** — the schema supports `principal_type = 'group'`, but AVA can't resolve CAS
  group membership yet, so `AccessService.GetGrantsAsync` passes an empty group list. Grants
  are effectively user-only until that's wired up.
- **Vector search** is not yet child-domain scoped. A study's uploaded documents can still surface
  in retrieval for a user who lacks that study's grant. Domain-level scoping does apply.
- **No shared Angular library** — the module/security code is duplicated between `ava-web` and
  `ava-orchestration-ui`. Changes must be made in both.
