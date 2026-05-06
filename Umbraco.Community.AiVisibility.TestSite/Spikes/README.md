# Spike 0 — Risk-Reduction Spike Harnesses

This folder contains the throwaway harnesses for Story 0.A (in-process Razor rendering) and Story 0.B (v17 package mechanics + `IDistributedBackgroundJob`). Both harnesses live in TestSite scope so the package project (`Umbraco.Community.AiVisibility/`) stays clean of investigation code.

- **0.A** — `SpikeRender*`, `InProcessPageRenderer`, `HttpFetchComparator`, `HtmlDiffer`, `ConcurrentRenderProbe` (this README §0.A)
- **0.B** — `DistributedJob/*` (this README §0.B)

---

## Spike 0.A — In-process Razor Rendering Harness

**Story:** [0-a-in-process-razor-page-rendering-spike](../../_bmad-output/implementation-artifacts/0-a-in-process-razor-page-rendering-spike.md)
**Verdict document:** [0-a-spike-outcome.md](../../_bmad-output/implementation-artifacts/0-a-spike-outcome.md)
**Status:** Done (manual gate signed off 2026-04-28).

---

## What this is

A throwaway harness that lives inside the TestSite to validate the documented v17 Razor-rendering chain end-to-end against Clean.Core 7.0.5 demo content. The package project itself (`Umbraco.Community.AiVisibility/`) contains **no** rendering code yet — Story 1.1 will rebuild this as the canonical `PageRenderer` once the spike's verdict is signed off.

## Files in this folder

| File | Role |
|---|---|
| [SpikeRenderController.cs](SpikeRenderController.cs) | MVC controller, routes `/spikes/render` + `/spikes/concurrency` |
| [InProcessPageRenderer.cs](InProcessPageRenderer.cs) | The chain: `PublishedRequestBuilder` → `RouteRequestAsync` → set `IUmbracoContext.PublishedRequest` → `IRazorViewEngine.GetView`/`FindView` → `view.RenderAsync` to `StringWriter` |
| [HttpFetchComparator.cs](HttpFetchComparator.cs) | Baseline-truth control via `IHttpClientFactory` — NOT the production technique |
| [HtmlDiffer.cs](HtmlDiffer.cs) | Whitespace/comment/timestamp-tolerant diff, returns first difference context |
| [ConcurrentRenderProbe.cs](ConcurrentRenderProbe.cs) | Parallel renders with title-match assertion for AC5 |
| [SpikeRenderResponse.cs](SpikeRenderResponse.cs) | DTOs for the JSON envelope |

## How to run the gate (Adam)

### 1. First boot — Clean.Core seeds itself

```bash
cd ~/Documents/LlmsTxt
dotnet run --project Umbraco.Community.AiVisibility.TestSite/Umbraco.Community.AiVisibility.TestSite.csproj
```

`appsettings.Development.json` is configured with:
- `Unattended:InstallUnattended = true` (admin: `spike@local.test` / `Spike#Password1`)
- `ConnectionStrings:umbracoDbDSN` pointing at SQLite under `umbraco/Data/Umbraco.sqlite.db`

So the first boot creates the schema, the unattended admin user, and runs Clean.Core's package migration to seed BlockList / BlockGrid / nested-content / image / table demo content.

Wait for `Application started. Press Ctrl+C to shut down.` Then visit `http://localhost:5xxx/` in a browser — you should see the Clean.Core front page.

### 2. Add a second culture (one-time setup)

The story's AC3 needs a culture-variant page. In the Backoffice:

1. Settings → Languages → add a second language (e.g. `cy-GB`).
2. Content tree → root node → Set culture and host names → tick the new culture.
3. Content tree → pick one Clean.Core page → Add variant → fill in translations for at least the title and one content area → Save & publish.

Note the URL of this localised page (e.g. `/about-us/` for `en-GB`, `/amdanom-ni/` for `cy-GB`). You'll need both for the harness call.

### 3. Run the harness scenarios

With the TestSite running, hit each URL. Replace `5xxx` with whatever port `dotnet run` reports (typically `5000` or printed in the console). Each call returns a JSON envelope.

**AC1 — In-process matches HTTP fetch (home / BlockList / BlockGrid):**

```bash
curl 'http://localhost:5xxx/spikes/render?path=/&mode=both' | jq .
curl 'http://localhost:5xxx/spikes/render?path=/<your-blocklist-page>&mode=both' | jq .
curl 'http://localhost:5xxx/spikes/render?path=/<your-blockgrid-page>&mode=both' | jq .
```

Pass: `diff.identical == true` for each. If `false`, paste `diff.firstDifferenceContext` into the spike-outcome doc — it points at exactly where the in-process render diverges from the HTTP control.

**AC3 — Culture variant:**

```bash
curl 'http://localhost:5xxx/spikes/render?path=/<localised-page>&culture=cy-GB&mode=both' | jq .
```

Pass: `diff.identical == true` AND the body contains the translated title/content (eyeball `html` field).

**AC4 — Deleted / unpublished node returns clean envelope:**

In the Backoffice, unpublish one Clean.Core node (e.g. `/products`). Then:

```bash
curl 'http://localhost:5xxx/spikes/render?path=/products&mode=in-process' | jq .
```

Pass: `status == "not-found"`, `html == null`, `diagnostics.exceptionType == null`, `diagnostics.exceptionMessage == null`. **Not a 500.**

Repeat with a deleted node (move to recycle bin).

**AC5 — Concurrent renders preserve scope isolation:**

```bash
curl 'http://localhost:5xxx/spikes/concurrency?paths=/,/page-1,/page-2,/,/page-1,/page-2,/,/page-1&titles=Home,Page%201,Page%202,Home,Page%201,Page%202,Home,Page%201&parallelism=4' | jq .
```

Pass: `allOk == true` (every response contains its expected title). Tweak the `paths`/`titles` lists to match the actual Clean.Core URLs and titles you see at `/`.

### 4. Capture evidence

For each scenario, save the JSON envelope to `Spikes/evidence/<acN>-<scenario>.json` (this folder is gitignored — local-only). The verdict document only needs reproducible URLs + the AC pass/fail, not the full HTML payloads.

### 5. Fill in the verdict

Open `_bmad-output/implementation-artifacts/0-a-spike-outcome.md`. Each AC has a stub line — replace with the observed result. Set the **Verdict** field to one of:

- `Proceed with documented in-process Razor chain` — all ACs pass; Epic 1.1 may start.
- `Pivot — see §X` — one or more ACs fail in a way the chain can't accommodate; Epic 1.1 needs replanning.
- `Hybrid — in-process for default cases, fallback to Y for §Z` — most cases pass but a documented edge case requires a fallback.

### 6. Sign off the manual gate in the story file

Append a "Manual gate result" section to `0-a-in-process-razor-page-rendering-spike.md` with `Pass` / `Pass with caveats: …` / `Fail — reason`. Only then does the story move to `done`.

## Cleanup after Story 1.1 ships

Once Story 1.1 lands the real `Umbraco.Community.AiVisibility/Extraction/PageRenderer.cs`, the `0.A` files get deleted along with the spike-only DI registrations in `Program.cs` and the `Spikes/evidence/` `.gitignore` rule. The verdict document survives — it remains the authoritative "why we chose this technique" reference.

---

## Spike 0.B — Umbraco v17 Package Mechanics + `IDistributedBackgroundJob`

**Story:** [0-b-umbraco-v17-package-mechanics-spike](../../_bmad-output/implementation-artifacts/0-b-umbraco-v17-package-mechanics-spike.md)
**Verdict document:** [0-b-spike-outcome.md](../../_bmad-output/implementation-artifacts/0-b-spike-outcome.md)
**Status:** Code in place, awaiting Adam's interactive E2E gate (one-instance smoke + two-instance shared-DB run).

> ⚠️ **Heads-up: Umbraco re-writes `Imaging.HMACSecretKey` on first boot.**
> When you run the TestSite for the first time (especially against a fresh SQL Server), Umbraco's image-processing module generates a per-installation HMAC key and persists it to `appsettings.json`. The 0-B code review flagged this as a secret-leak risk for the public repo. **Before committing, always check `git diff -- Umbraco.Community.AiVisibility.TestSite/appsettings.json` for an `Imaging` block — if present, delete it.** Umbraco will regenerate per environment; the value has no production meaning.

### What 0.B is

A throwaway harness that proves five v17 package mechanics end-to-end:

1. RCL static asset serving under `/App_Plugins/LlmsTxtUmbraco/`
2. `umbraco-package.json` discovery via Vite's `public/` convention
3. Lit dashboard tile registration with `@umbraco-cms/backoffice/external/lit` imports only
4. Stub `ManagementApiControllerBase` controller appearing in `/umbraco/swagger/llmstxtumbraco/swagger.json` under v1
5. `IDistributedBackgroundJob` exactly-once execution across two TestSite instances pointing at the same host DB

### Files in `Spikes/DistributedJob/`

| File | Role |
|---|---|
| [DistributedJob/SpikeDistributedJobOptions.cs](DistributedJob/SpikeDistributedJobOptions.cs) | `LlmsTxtSpike:DistributedJob:*` appsettings binding (gates the harness so it stays inert by default) |
| [DistributedJob/SpikeJobLogEntry.cs](DistributedJob/SpikeJobLogEntry.cs) | NPoco POCO for the `llmsSpikeJobLog` execution-log table |
| [DistributedJob/SpikeJobLogStore.cs](DistributedJob/SpikeJobLogStore.cs) | Flavor-aware DDL (SQLite + SQL Server), insert/read via `IScopeProvider` |
| [DistributedJob/SpikeDistributedJob.cs](DistributedJob/SpikeDistributedJob.cs) | `IDistributedBackgroundJob` implementation — writes one row per cycle with `(cycleSequence, executedAt, instanceId)` |
| [DistributedJob/SpikeDistributedJobComposer.cs](DistributedJob/SpikeDistributedJobComposer.cs) | DI registration; switches between real/inert job based on `Enabled` flag |
| [DistributedJob/SpikeJobInspectorController.cs](DistributedJob/SpikeJobInspectorController.cs) | `GET /spikes/distributed-job/rows` returns the table contents as JSON |

The package-side stubs added by 0.B:

| File | Role |
|---|---|
| `Umbraco.Community.AiVisibility/Client/src/elements/llms-spike-dashboard.element.ts` | Lit element rendering a placeholder tile + stub Management API ping result |
| `Umbraco.Community.AiVisibility/Client/src/manifests/dashboard-spike.manifest.ts` | Manifest registering the element under `Umb.Section.Settings` with alias `Llms.Dashboard.Spike` |
| `Umbraco.Community.AiVisibility/Controllers/Backoffice/LlmsSpikeManagementApiController.cs` | Canonical v17 Management API pattern: `ManagementApiControllerBase` + `[VersionedApiBackOfficeRoute("llmstxt/spike")]` + `[ApiVersion("1.0")]` + `[MapToApi(Constants.ApiName)]` |

### How to run the gate (Adam)

#### Stage 1 — One-instance Backoffice + OpenAPI smoke (AC1, AC2, AC3, AC5)

This stage runs against the existing TestSite SQLite DB. No special setup beyond what 0.A already established.

##### a. Build the Vite bundle

```bash
cd ~/Documents/LlmsTxt/Umbraco.Community.AiVisibility/Client
npm install        # required Node ≥ 22.17.1 per @umbraco-cms/backoffice engines field
npm run build
```

`npm run build` should produce `LlmsTxt.Umbraco/wwwroot/App_Plugins/LlmsTxtUmbraco/`:
- `llms-txt-umbraco.js` (entry, ~0.7 KB)
- `entrypoint-<hash>.js` (existing template entry point)
- `llms-spike-dashboard.element-<hash>.js` (Lit element chunk)
- `umbraco-package.json` (manifest, copied via Vite's `public/` convention; `allowTelemetry: false`)

If `npm run build` fails, capture the error in `0-b-spike-outcome.md § Architectural drift` — the most likely cause is Node < 22.17.1.

##### b. Boot the TestSite

```bash
cd ~/Documents/LlmsTxt
dotnet run --project Umbraco.Community.AiVisibility.TestSite/Umbraco.Community.AiVisibility.TestSite.csproj
```

Default `appsettings.Development.json` keeps the spike distributed job inert (`LlmsTxtSpike:DistributedJob:Enabled=false`). The Backoffice + Management API surface still loads.

##### c. Verify Backoffice tile (AC1, AC2)

1. Open the Backoffice (URL printed by `dotnet run`).
2. Sign in with `spike@local.test` / `Spike#Password1`.
3. Navigate to `Settings`.
4. Look for "LlmsTxt Spike" in the dashboard list. Click it.
5. The dashboard should render `<uui-box>` with the headline "LlmsTxt — Spike 0.B (package mechanics)" and an API ping line.

Pass: tile renders, ping line shows `<ISO8601 time> from <machine>/<pid>`.

If the page shows a console error mentioning `lit` resolution, the Bellissima external import map failed — capture the error in `0-b-spike-outcome.md`.

##### d. Verify the OpenAPI document (AC3)

```bash
curl 'http://localhost:5xxx/umbraco/swagger/llmstxtumbraco/swagger.json' | jq '.paths | keys'
```

Pass: the keys array contains `/umbraco/management/api/v1/llmstxt/spike/ping`. Capture the excerpt in the outcome doc.

##### e. Verify Vite output / Node version (AC5)

Capture the `npm run build` log from step (a) into the outcome doc — including any `EBADENGINE` warnings. The spike's AC5 passes if the build completes successfully on Node ≥ 24.11.1 (the project-context.md target). If only Node 22.17.1+ is available locally, document the discrepancy and flag for retro — the actual `engines` field in `Client/package.json` is `>=24.11.1` per `project-context.md`, but `@umbraco-cms/backoffice@17.3.4` only requires `>=22.17.1`.

#### Stage 2 — Two-instance shared-DB exactly-once run (AC4)

This is the non-skippable gate. SQLite locks across processes, so this stage requires a SQL Server (LocalDB or Docker).

##### a. Start a SQL Server

LocalDB (Windows only):

```pwsh
sqllocaldb start MSSQLLocalDB
```

Docker (mac/Linux):

```bash
docker run -d --name llmstxt-spike-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Spike#Password1' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

Wait for the container to be healthy:

```bash
docker logs --tail 5 llmstxt-spike-mssql
# look for "SQL Server is now ready for client connections"
```

##### b. Create two appsettings overrides for Instance A and Instance B

The TestSite reads `appsettings.Development.json` by default. To run two instances with separate ASP.NET Core ports + temp-storage paths but the same DB connection string, copy `appsettings.Development.json` to `appsettings.Spike.A.json` and `appsettings.Spike.B.json`:

```json
{
  "$schema": "appsettings-schema.json",
  "Umbraco": {
    "CMS": {
      "Hosting": {
        "LocalTempStorageLocation": "EnvironmentTemp"
      }
    }
  },
  "ConnectionStrings": {
    "umbracoDbDSN": "Server=localhost,1433;Database=LlmsTxtSpike;User Id=sa;Password=Spike#Password1;Encrypt=False;TrustServerCertificate=True",
    "umbracoDbDSN_ProviderName": "Microsoft.Data.SqlClient"
  },
  "LlmsTxtSpike": {
    "DistributedJob": {
      "Enabled": true,
      "Period": "00:01:00"
    }
  }
}
```

Both instances point at the same `LlmsTxtSpike` database. Differ them only by `Logging:LogLevel` or extra fields if you need to tell their console output apart — Umbraco's coordination is what matters.

##### c. Boot Instance A on port 5101

In one terminal:

```bash
cd ~/Documents/LlmsTxt
ASPNETCORE_ENVIRONMENT=Spike.A ASPNETCORE_URLS=http://localhost:5101 \
  dotnet run --project Umbraco.Community.AiVisibility.TestSite/Umbraco.Community.AiVisibility.TestSite.csproj --no-launch-profile
```

Wait for `Application started`. The first boot will run Umbraco's unattended install + Clean.Core seed against the empty SQL Server DB. This takes ~60s.

##### d. Boot Instance B on port 5102

In a second terminal, ~30s after Instance A logs `Application started`:

```bash
cd ~/Documents/LlmsTxt
ASPNETCORE_ENVIRONMENT=Spike.B ASPNETCORE_URLS=http://localhost:5102 \
  dotnet run --project Umbraco.Community.AiVisibility.TestSite/Umbraco.Community.AiVisibility.TestSite.csproj --no-launch-profile
```

Instance B finds the schema already present (Umbraco migrations are idempotent) and starts in ~10s.

##### e. Run for ≥ 130 seconds

Both instances tick at 60-second intervals. After 130 seconds total wall-clock from when Instance A booted, you should have observed at least two cycles.

##### f. Inspect the rows

From either instance:

```bash
curl 'http://localhost:5101/spikes/distributed-job/rows' | jq .
curl 'http://localhost:5102/spikes/distributed-job/rows' | jq .
```

Pass criteria for AC4:
- `count` is exactly **2** (one row per cycle, never more)
- The two rows have distinct `cycleSequence` values
- The `instanceId` column shows each instance's unique `Environment.MachineName/Environment.ProcessId`. **Either instance can win each cycle** — the test is "exactly one per cycle", not "alternation".
- Both endpoints return identical row sets (same DB)

Capture the two JSON envelopes (`/rows` from instance A and from instance B) into `0-b-spike-outcome.md § AC4 evidence`.

##### g. Mid-cycle kill test

Kill Instance A with Ctrl+C while a cycle is mid-flight (e.g. 30s after a row was written). Wait for the next cycle on Instance B. The next row should appear with Instance B's `instanceId` and the next `cycleSequence` — no duplicate row for the killed cycle.

Capture this in the outcome doc as the resilience evidence.

##### h. Cleanup

```bash
docker rm -f llmstxt-spike-mssql      # if you used Docker
```

Both `appsettings.Spike.A.json` and `appsettings.Spike.B.json` are local-only — gitignored under `appsettings.Spike.*.json` (add the rule if missing).

#### Stage 3 — Architectural drift, verdict, sign-off

Each AC's evidence rolls up into [0-b-spike-outcome.md](../../_bmad-output/implementation-artifacts/0-b-spike-outcome.md). The outcome doc explicitly captures every architecture-vs-reality drift the spike surfaced (mirrors 0.A's outcome format) so the Epic 0 retrospective can amend `architecture.md` and `project-context.md` in one pass.

When all five ACs pass, set the **Verdict** field to `Proceed with documented v17 Management API + IDistributedBackgroundJob patterns. Epic 3 / Epic 5 Backoffice work unblocked.` and append the **Manual Gate Result** block to `0-b-umbraco-v17-package-mechanics-spike.md`.

### Cleanup after Stories 3.2 / 5.2 ship

When Story 3.2 (Settings dashboard) and Story 5.2 (AI traffic dashboard) land their real elements, the spike Lit element + manifest get deleted. The stub `LlmsSpikeManagementApiController` and the existing template-scaffold `LlmsTxtUmbracoApiController` both go away when Story 6 reconciles to a single canonical Management API pattern. The TestSite `Spikes/DistributedJob/` folder gets deleted when Story 5.1's real `LogRetentionJob` ships. The verdict document survives.
