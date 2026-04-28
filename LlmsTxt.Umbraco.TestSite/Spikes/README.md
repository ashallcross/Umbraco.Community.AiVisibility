# Spike 0.A — In-process Razor Rendering Harness

**Story:** [0-a-in-process-razor-page-rendering-spike](../../_bmad-output/implementation-artifacts/0-a-in-process-razor-page-rendering-spike.md)
**Verdict document:** [0-a-spike-outcome.md](../../_bmad-output/implementation-artifacts/0-a-spike-outcome.md)
**Status:** Code in place, awaiting Adam's interactive E2E gate.

---

## What this is

A throwaway harness that lives inside the TestSite to validate the documented v17 Razor-rendering chain end-to-end against Clean.Core 7.0.5 demo content. The package project itself (`LlmsTxt.Umbraco/`) contains **no** rendering code yet — Story 1.1 will rebuild this as the canonical `PageRenderer` once the spike's verdict is signed off.

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
dotnet run --project LlmsTxt.Umbraco.TestSite/LlmsTxt.Umbraco.TestSite.csproj
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

Once Story 1.1 lands the real `LlmsTxt.Umbraco/Extraction/PageRenderer.cs`, this whole `Spikes/` folder gets deleted along with the spike-only DI registrations in `Program.cs` and the `Spikes/evidence/` `.gitignore` rule. The verdict document survives — it remains the authoritative "why we chose this technique" reference.
