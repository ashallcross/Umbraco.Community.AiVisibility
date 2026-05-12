# Release checklist — for contributors and maintainers

The checklist that gets run when cutting a new package release. If you're forking the package, contributing back via PR, or running your own internal release pipeline, this is the canonical sequence to verify before a `git tag` + push.

If you're using the package in your own Umbraco site, you don't need this doc — start with [`getting-started.md`](getting-started.md) instead.

It complements [`maintenance.md`](maintenance.md) (one-off operational notes — SHA refresh, two-instance Docker SQL Server setup); this one is the recurring per-release guide.

## Pre-release verification

Run these locally on a clean checkout of the release branch.

### 1. Test suite

- [ ] `dotnet test Umbraco.Community.AiVisibility.slnx --configuration Release` → all pass

### 2. Pack output review

- [ ] Run `dotnet pack Umbraco.Community.AiVisibility/Umbraco.Community.AiVisibility.csproj -c Release -o /tmp/aiv-pack-final` locally
- [ ] Run `unzip -l /tmp/aiv-pack-final/Umbraco.Community.AiVisibility.<version>.nupkg`
- [ ] Confirm zero `content/App_Plugins/...` entries (would be a regression)
- [ ] Confirm zero `contentFiles/any/net10.0/wwwroot/App_Plugins/...` entries (would be a regression)
- [ ] Confirm zero `*.map` files in the release artefact (see § Source maps decision below)

### 3. Source maps decision

The committed Vite bundle does NOT include source maps. The default is enforced by `vite.config.ts` (sourcemap behaviour driven by the `VITE_INCLUDE_SOURCEMAP` env var; absence-of-env-var = production-default off). Consequence: adopters debugging the Backoffice bundle in DevTools see the minified JS without a back-mapping to the original `.ts` source.

Trade-off:

- **Include source maps in `.nupkg`**: ~78 KB extra per ship; adopters can navigate to the package's `.ts` source in DevTools. But the `.ts` files themselves don't ship — the source map references unreachable paths. Net value to adopters: low.
- **Exclude source maps from `.nupkg` (default)**: leaner package; adopters debugging the bundle against minified JS. Maintainer-side debugging uses `npm run watch` against the `Client/src/` source directly.

Decision (2026-05-07): **Exclude source maps from release `.nupkg`.** Re-evaluate if adopter feedback indicates the trade-off is wrong.

To temporarily include source maps for a debugging release, flip `sourcemap: true` in `vite.config.ts` and re-pack; revert before shipping.

### 4. Dependency status review

- [ ] Open `docs/dependency-status.md`
- [ ] Cross-check NU1902/NU1903 catalogue against `dotnet build` output:

  ```bash
  dotnet build Umbraco.Community.AiVisibility.slnx --configuration Release 2>&1 \
    | grep -E "NU190[23]" \
    | grep -Eo "NU190[23]: Package '[^']+' [^ ]+" \
    | sort -u
  ```

- [ ] Every output line maps to a row in `docs/dependency-status.md` § Vulnerability warnings
- [ ] Every catalogued row maps to an output line (no stale catalogue entries)
- [ ] If a row's target review date has passed, attempt path-a (Umbraco patch bump) and document the outcome

### 5. README freshness

- [ ] Read the published README on a fresh GitHub view (after pushing the release branch, NOT the local file)
- [ ] All internal doc links (`docs/configuration.md`, `docs/extension-points.md`, etc.) resolve to existing files
- [ ] Install command works copy-paste against a fresh dotnet project
- [ ] No "forthcoming" / "Pre-development" / TODO / TBD markers visible (`grep -nE "forthcoming|Pre-development|TODO|TBD" README.md` → zero hits — use `-E` so the alternation works on macOS BSD `grep` as well as GNU)
- [ ] Compatibility table reflects current pinned versions in `Directory.Packages.props`

### 6. `docs/` link-check

- [ ] Walk every relative link in every `docs/*.md` file
- [ ] Every `[…](docs/…)` and `[…](../…)` reference resolves to an existing path
- [ ] Code-block paths (`Umbraco.Community.AiVisibility/...`) match the actual source-tree layout
- [ ] No `LlmsTxt.Umbraco` / `Llms*` / `llms-` references survive (rename leftovers)

## Release execution

### 7. Version bump

- [ ] `Directory.Packages.props` Umbraco floor reviewed and bumped if a fix lands
- [ ] `Umbraco.Community.AiVisibility.csproj:<Version>` bumped per [SemVer](https://semver.org/) with the release scope:
  - **Patch** (e.g. `1.0.x` → `1.0.x+1`): bug fixes, no API changes.
  - **Minor** (e.g. `1.0.x` → `1.1.0`): backward-compatible additions. Doctype migration changes, settings-key additions land here.
  - **Major** (e.g. `1.x` → `2.0.0`): breaking API changes. Reserved.
- [ ] `CHANGELOG.md` updated with the new version + summary of changes (link to merged PRs)

### 8. Pack, push, tag

Manual publish flow (no CI involved):

```bash
# 1. Build the Backoffice bundle.
cd Umbraco.Community.AiVisibility/Client && npm ci && npm run build && cd ../..

# 2. Pack.
rm -rf ./artifacts
dotnet pack Umbraco.Community.AiVisibility/Umbraco.Community.AiVisibility.csproj --configuration Release --output ./artifacts

# 3. Inspect the .nupkg one last time.
unzip -l ./artifacts/Umbraco.Community.AiVisibility.<version>.nupkg

# 4. Push to nuget.org.
dotnet nuget push ./artifacts/*.nupkg --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json

# 5. Tag locally + push the tag for the historical record.
git tag -a v<version> -m "Release v<version>"
git push origin v<version>

# 6. Create the GitHub Release with the .nupkg attached + the CHANGELOG excerpt as the body.
gh release create v<version> \
  --title "v<version>" \
  --notes-file <(awk '/^## \[v<version>\]/,/^## \[/' CHANGELOG.md | sed '$d') \
  ./artifacts/*.nupkg
```

### 9. Marketplace listing update (when applicable)

`umbraco-marketplace.json` is shipped at the repo root (matches the AgentRun.Umbraco precedent and the canonical Umbraco Marketplace listing pattern) — verify the listing's `Title`, `Description`, `Tags`, `Category`, and `AuthorDetails` reflect the release. The file is read by the Marketplace from `<PackageProjectUrl>` at submission/listing time and is NOT packed inside the `.nupkg` (it's submission metadata, not adopter-runtime data).

**Refresh cadence — important ops detail:** the Marketplace re-fetches `umbraco-marketplace.json` every 2h. For listing copy changes (Title, Description, Tags, Screenshots, AuthorDetails) you do NOT re-publish to NuGet — edit the JSON, push to repo, wait up to 2h. For new code versions you DO publish to NuGet, then trigger the expedite endpoint to skip the 24h discovery scan:

```bash
curl -X POST https://functions.marketplace.umbraco.com/api/InitiateSinglePackageSyncFunction \
  -H "Content-Type: application/json" \
  -d '{"PackageId": "Umbraco.Community.AiVisibility"}'
```

**Validation:** before announcing the listing publicly, run the listing through `https://marketplace.umbraco.com/validate` — it confirms NuGet tag, Umbraco dependency, JSON resolution, screenshot reachability, and license-type recognition.

**See also: [`docs/marketplace-listing-checklist.md`](marketplace-listing-checklist.md)** — the full canonical process (csproj fields, JSON schema, Step 1 → Step 8 procedure, refresh cadences, common gotchas). That checklist is reusable across packages; this release-checklist's § 9 only carries the per-release recurring touch.

## Post-release

- [ ] Verify the package appears on [nuget.org](https://www.nuget.org/packages/Umbraco.Community.AiVisibility) within ~10 min of the `dotnet nuget push` (it's usually faster — the page reaches "Listed" status once indexing completes)
- [ ] Verify the Marketplace listing is updated (if changed)
- [ ] GitHub Release was created at the tag-push step above; double-check the body matches the CHANGELOG entry and the `.nupkg` is attached

### 10. Post-publish install verification (non-skippable)

**The release is not considered shipped until this step passes.** Locally-tested `.nupkg` artefacts can pass the pack output review (§ 2) and still fail to install cleanly against a real-world consumer for non-obvious reasons (NuGet feed caching, deps.json resolution drift, missing `<None Pack="true">` entry, etc.). The post-publish install verification confirms that the package an adopter would `dotnet add` from nuget.org actually boots and serves traffic on a representative consumer working tree.

The canonical consumer to verify against is the working tree previously used as a live-fire integration target during this release's manual gates (for v1.1.0, that was PRC0250). Subsequent releases follow the same convention: the working tree used for the release's pre-release manual gate becomes the install-verification target post-publish.

```bash
# 1. Wait for NuGet propagation (~5-15 min after push). The package's nuget.org
#    page should show "Listed" status; the search index lag is the bottleneck.
open "https://www.nuget.org/packages/Umbraco.Community.AiVisibility/<version>"

# 2. On the consumer working tree (NOT the package repo), clear local NuGet
#    caches to defeat any stale resolution from a prior `<ProjectReference>` or
#    test-feed install:
dotnet nuget locals all --clear

# 3. Switch the consumer from <ProjectReference> (or older <PackageReference>) to
#    the freshly-published version:
dotnet add package Umbraco.Community.AiVisibility --version <version>

# 4. Build + boot the consumer. Confirm clean startup + migration runs (or
#    short-circuits idempotently if the doctype already exists in the host DB).
dotnet build
dotnet run
```

Walk the following five surfaces and confirm each one preserves behaviour observed during the pre-release gate:

- [ ] `GET /llms.txt` returns 200 with `Content-Type: text/markdown; charset=utf-8`; manifest body shape unchanged
- [ ] `GET /llms-full.txt` returns 200 with the bulk export; body cap respected; truncation footer present if applicable
- [ ] `GET /{any-published-page}.md` returns 200 with valid Markdown + frontmatter (under the default `RenderStrategy:Mode = Auto`)
- [ ] Backoffice **Settings → AI Visibility** dashboard loads (read-write)
- [ ] Backoffice **Settings → AI Traffic** dashboard loads (read-only); rows accumulated during the pre-release gate walk are visible

Record the verdict in the GitHub Release body (or the local release log if no GitHub Release is cut):

```
Post-publish install verification: PASS / FAIL
Consumer working tree: <name>
NuGet propagation latency observed: <minutes>
Evidence: <one-line summary of preserved-behaviour walk>
```

**Failure path (one of the five surfaces regresses):**

1. **Unlist immediately** — `dotnet nuget unlist Umbraco.Community.AiVisibility <version>`. Adopters who already installed via `dotnet add package` keep working; new adopters get the previous version from the search index until a patch ships.
2. **Do NOT attempt to live-patch the published version.** NuGet package immutability — `<version>` is reserved forever. The fix path is a patch release (`<version>+0.0.1`).
3. **Open a patch story immediately** with the regression as the only AC; pin the failing surface as the reproduction; ship a clean `<version>+0.0.1` through the same checklist.
4. **Document the missed surface** so the pre-release gate (§ Pre-release verification + the per-release manual strategy-parity walk) can add a step covering it for future releases — either as an issue against this repo, an entry in your fork's release retrospective, or both.

## v1 release-readiness audit

This is the cross-cutting verification matrix for a v1.0.0 (or v1.x patch) release. Walk it line by line at sign-off time and tick each row against the listed evidence. The rows are organised by concern; every row is verifiable against a file path, a test name, a doc link, or an `unzip -l` of the packed `.nupkg`.

### Licensing + attribution

| Item | Evidence |
|---|---|
| Apache-2.0 license expression in `<PackageLicenseExpression>` | `Umbraco.Community.AiVisibility/Umbraco.Community.AiVisibility.csproj` |
| `LICENSE` file at repo root with full Apache-2.0 text | `LICENSE` |
| `NOTICE` file with third-party Apache-2.0 attributions | `NOTICE` (currently: `SmartReader`) |
| Bundled DLLs check (only `Umbraco.Community.AiVisibility.dll` ships under `lib/net10.0/`) | `unzip -l artifacts/Umbraco.Community.AiVisibility.<version>.nupkg \| grep -E "lib/net10.0/.+\.dll"` |
| README badges (NuGet + Marketplace + Apache-2.0) | `README.md` |

### Build + distribution

| Item | Evidence |
|---|---|
| Single-target on .NET 10 | `Umbraco.Community.AiVisibility/Umbraco.Community.AiVisibility.csproj` `<TargetFramework>net10.0</TargetFramework>` |
| Central Package Management (zero `PackageReference Version=` attributes) | `grep -rn 'PackageReference.*Version=' Umbraco.Community.AiVisibility*/**/*.csproj` returns zero matches |
| `unzip -l` of the `.nupkg` carries no unexpected entries (no `content/`, `contentFiles/`, `*.map` files) | manual pre-publish inspection per § Pack output review above |
| Vulnerability warnings catalogued + reviewed | [`dependency-status.md`](dependency-status.md) § Vulnerability warnings — manual review per § Dependency status review above |
| Vite bundle pre-built and committed | `Umbraco.Community.AiVisibility/wwwroot/App_Plugins/UmbracoCommunityAiVisibility/umbraco-community-aivisibility.js` exists in the repo |
| Source maps excluded from production bundle | `Umbraco.Community.AiVisibility/Client/vite.config.ts` — sourcemap controlled by `VITE_INCLUDE_SOURCEMAP` env var, prod-default off |
| Marketplace listing JSON at repo root | `umbraco-marketplace.json` (validated against `https://marketplace.umbraco.com/umbraco-marketplace-schema.json`) |
| Package icon at repo root + wired via `<PackageIcon>` | `icon.png` + csproj `<PackageIcon>icon.png</PackageIcon>` + `<None Include="..\icon.png" Pack="true">` pack item |
| `umbraco-marketplace` token first in semicolon-separated `<PackageTags>` | csproj `<PackageTags>` |

### Public surfaces (HTTP + Backoffice)

| Item | Evidence |
|---|---|
| Per-page Markdown via `/{path}.md` route | `Routing/` + `Extraction/`; surface documented in [`getting-started.md`](getting-started.md) |
| `Accept: text/markdown` content negotiation on canonical URLs | `Routing/` |
| `/llms.txt` index manifest (RFC-style links + summaries) | `LlmsTxt/`; output cached + hot-path-protected |
| `/llms-full.txt` concatenated full-Markdown export with hard byte cap | `LlmsTxt/`; `AiVisibility:MaxLlmsFullSizeKb` controls the cap |
| HTTP `Link: rel="alternate"; type="text/markdown"` discoverability header | Auto-emitted on every opted-in HTML response; `Vary: Accept` idempotent |
| `<llms-link />` and `<llms-hint />` Razor TagHelpers (optional in-Razor declaration) | `LlmsTxt/` (TagHelpers folder); documented in [`data-attributes.md`](data-attributes.md) |
| `Content-Signal` Cloudflare header (off by default; opt-in per-doctype) | `AiVisibility:ContentSignal:Default` + `:PerDocTypeAlias` config |
| Settings dashboard at **Settings → AI Visibility** | `Backoffice/` controller + Lit element + manifest registration |
| AI Traffic dashboard at **Settings → AI Traffic** | `Backoffice/` controller + Lit element + manifest registration; reads `aiVisibilityRequestLog` |
| Robots audit Health Check at **Settings → Health Checks → AI Visibility — Robots audit** | `Robots/` Health Check; auto-discovered via `TypeLoader` |
| `aiVisibilityRequestLog` host-DB table created via `PackageMigrationPlan` | `Persistence/Migrations/AiVisibilityPackageMigrationPlan.cs` + `AddRequestLogTable_1_0` step |

### Configuration + DI

| Item | Evidence |
|---|---|
| `IValidateOptions<T>` validators registered via `TryAddEnumerable` (NOT `TryAddSingleton`) | See § "IValidateOptions sweep" below |
| `IRobotsAuditor` Singleton lifetime contract (composer hard-validates) | `Robots/` composer + matching test |
| `IRequestLog` Singleton with bounded channel (composer hard-validates) | `Notifications/` (or equivalent) composer + matching test |
| `IDistributedBackgroundJob` for exactly-once cluster work | `RobotsAuditRefreshJob` + `LogRetentionJob` |
| `LegacyConfigurationProbe` boot-time warn for stale `LlmsTxt:` keys | `Configuration/LegacyConfigurationProbe.cs`; documented in [`configuration.md`](configuration.md) § "Migrating from a pre-v1 install" |
| Per-page caching with publish-driven invalidation | `Caching/` + `IPublishingNotificationHandler` integration |

### Multi-host + multi-culture

| Item | Evidence |
|---|---|
| Hostname → root resolution via `IDomainService.GetAll(true)` | `Routing/` (or equivalent); documented in [`multi-site.md`](multi-site.md) |
| Multi-culture routing (BCP-47) | Documented in [`multi-site.md`](multi-site.md) |
| Per-host cache-key shape (no key collisions across bound hostnames) | `Caching/` cache-key helpers; documented in [`multi-site.md`](multi-site.md) |
| Optional hreflang sibling-culture variants on `/llms.txt` | `AiVisibility:Hreflang:Enabled` config |

### Security + privacy

| Item | Evidence |
|---|---|
| PII discipline — no query strings, cookies, tokens, session IDs, full referrer paths in `aiVisibilityRequestLog` | Documented in [`README.md`](../README.md) § "Security & privacy notes"; verified by request-log writer code path + paired test |
| SSRF defence on robots-audit fetcher (refuses RFC1918 / loopback / link-local / cloud-metadata IPs) | `Robots/DefaultRobotsAuditor.cs` + paired test |
| 3xx redirect rejection in robots-audit fetcher | `Robots/DefaultRobotsAuditor.cs` + paired test |
| XSS defence on Health Check rendered HTML (`WebUtility.HtmlEncode` for adopter-controlled values) | `Robots/RobotsAuditHealthCheck.cs` + paired test |
| Backoffice Management API behind Umbraco's authorisation policy | `Backoffice/` controllers carry `[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]` |
| Bearer-token authenticated fetch helper unifies dashboard auth shape | `Client/src/utils/authenticated-fetch.ts` (or equivalent) |
| No phone-home / telemetry / analytics-out from the package | Codebase grep — zero outbound HTTP that isn't the explicit AI-bot-list build target |

### Tests

| Item | Evidence |
|---|---|
| Test suite passes (post-launch-hygiene baseline + new validators) | `dotnet test Umbraco.Community.AiVisibility.slnx` returns green |
| Each NEW validator ships happy-path + each-Fail-branch + AppendedNotReplaced + StartupValidation tests | `Umbraco.Community.AiVisibility.Tests/Configuration/<Name>SettingsValidatorTests.cs` |

### Documentation

| Item | Evidence |
|---|---|
| `README.md` marketplace-grade adopter onboarding | `README.md` |
| `CHANGELOG.md` v1.0.0 entry + Migration from pre-1.0 call-out | `CHANGELOG.md` |
| `docs/getting-started.md` (Prerequisites + For editors + extension contract) | [`getting-started.md`](getting-started.md) |
| `docs/configuration.md` (key reference + Migrating from a pre-v1 install) | [`configuration.md`](configuration.md) |
| `docs/architecture.md` (1-page adopter summary) | [`architecture.md`](architecture.md) |
| `docs/extension-points.md` (per-interface contracts + lifetime constraints) | [`extension-points.md`](extension-points.md) |
| `docs/multi-site.md` (hostname → root + culture routing) | [`multi-site.md`](multi-site.md) |
| `docs/data-attributes.md` (`data-llms-content` / `data-llms-ignore`) | [`data-attributes.md`](data-attributes.md) |
| `docs/robots-audit.md` (Health Check + bot-list refresh) | [`robots-audit.md`](robots-audit.md) |
| `docs/maintenance.md` (SHA refresh + two-instance gate + IValidateOptions authoring) | [`maintenance.md`](maintenance.md) |
| `docs/release-checklist.md` (this file — pre-release + release execution + post-release + audit) | [`release-checklist.md`](release-checklist.md) |
| `docs/dependency-status.md` (NU1902/NU1903 + CS0618 catalogue) | [`dependency-status.md`](dependency-status.md) |
| `docs/marketplace-listing-checklist.md` (csproj fields + Marketplace JSON + submission) | [`marketplace-listing-checklist.md`](marketplace-listing-checklist.md) |
| `docs/screenshots/*` (4-6 static PNG screenshots covering both dashboards + Health Check + sample outputs) | `docs/screenshots/` |
| Zero pre-rename or planning-repo identifiers in shipping markdown / code comments / xmldoc | `grep -rnE "LlmsTxtUmbraco\|llms-txt-umbraco\.js\|LlmsTxt for Umbraco" Umbraco.Community.AiVisibility/ docs/ README.md CHANGELOG.md` returns zero matches in adopter-facing surfaces (historical CHANGELOG entries v0.10 down preserve pre-rename type names as honest historical record) |

### IValidateOptions sweep

For each `AiVisibility:*` settings sub-block, ship a validator OR record a `no invariants worth encoding` justification. Coverage targets are CEILINGS, not floors.

| Sub-block | Outcome |
|---|---|
| `LogRetention` | Validator: `Configuration/LogRetentionSettingsValidator.cs` (3 invariants — DurationDays / RunIntervalHours pos-when-set; RunIntervalSecondsOverride pos-when-set) |
| `RobotsAuditor` | Validator: `Configuration/RobotsAuditorSettingsValidator.cs` (4 invariants — RefreshIntervalHours / FetchTimeoutSeconds / DevFetchPort range / RefreshIntervalSecondsOverride) |
| `RequestLog` | Validator: `Configuration/RequestLogSettingsValidator.cs` (4 invariants — QueueCapacity / BatchSize / MaxBatchIntervalSeconds / OverflowLogIntervalSeconds) |
| Top-level (`AiVisibility:`) | No invariants worth encoding beyond the top-level — covered by sub-block validators + the existing `AiVisibilitySettingsValidator` (Analytics) |
| `LlmsTxtBuilder` | No invariants worth encoding — `CachePolicySeconds` clamp matches `LlmsFullBuilder`'s; `PageSummaryPropertyAlias` empty fall-through is a documented behaviour |
| `LlmsFullScope` | No invariants worth encoding — covered by binding (`RootContentTypeAlias` is an Umbraco runtime concern; doctype-alias arrays are validated by the consumer) |
| `LlmsFullBuilder` | No invariants worth encoding — `Order` enum binding is sufficient; `CachePolicySeconds` clamp matches `LlmsTxtBuilder` |
| `Hreflang` | No invariants worth encoding — bool flag |
| `DiscoverabilityHeader` | No invariants worth encoding — bool flag |
| `ContentSignal` | No invariants worth encoding — header values pass verbatim to the consumer (Cloudflare); operator typos fail at the consumer not the producer |
| `Migrations` | No invariants worth encoding — bool flag |
| `Analytics` | Validator: existing `AiVisibilitySettingsValidator.cs` (covers Analytics — kept as one consolidated validator; not retroactively split per the colocation principle) |

## When this checklist fails

- **Pack output review surfaces an unexpected entry**: investigate. Either tighten the csproj/Vite config to suppress the leak (preferred), or accept the new ship surface as intentional + record the rationale in CHANGELOG so adopters get the heads-up.
- **A new NU1902/NU1903 surfaces**: investigate upstream. Either path-a (bump Umbraco floor to a fixed transitive — preferred) or path-b (document the warning in [`dependency-status.md`](dependency-status.md) with a target review date).
- **Smoke trio fails**: a real defect at the integration boundary. Slip the release until green; the gate exists for this.

## Cross-references

- [`docs/marketplace-listing-checklist.md`](marketplace-listing-checklist.md) — Umbraco Marketplace listing checklist (csproj fields, JSON schema, submission procedure, gotchas).
- [`docs/dependency-status.md`](dependency-status.md) — NU1902/NU1903 + CS0618 catalogue.
- [`docs/maintenance.md`](maintenance.md) — bot-list SHA refresh, two-instance Docker SQL Server setup.
