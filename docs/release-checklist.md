# Release checklist — for contributors and maintainers

The checklist that gets run when cutting a new package release. If you're forking the package, contributing back via PR, or running your own internal release pipeline, this is the canonical sequence to verify before a `git tag` + push.

If you're using the package in your own Umbraco site, you don't need this doc — start with [`getting-started.md`](getting-started.md) instead.

It complements [`maintenance.md`](maintenance.md) (one-off operational notes — SHA refresh, two-instance Docker SQL Server setup); this one is the recurring per-release guide.

## Pre-release verification

Run these locally on a clean checkout of the release branch.

### 1. Test suite + CI gates

- [ ] `dotnet test Umbraco.Community.AiVisibility.slnx --configuration Release` → all pass
- [ ] `dotnet test … --filter "Category=LaunchSmoke"` → 3 tests pass (the smoke trio)
- [ ] `dotnet test … --filter "Category=ReleaseGuard"` → all pass
- [ ] `.github/scripts/assert-pack-output.sh` → exit 0 (pack output matches allow-list)
- [ ] CI run on the release branch is green across `build-online`, `build-offline`, and `pack-gate` jobs

### 2. Pack output review

- [ ] Run `dotnet pack Umbraco.Community.AiVisibility/Umbraco.Community.AiVisibility.csproj -c Release -o /tmp/aiv-pack-final` locally
- [ ] Run `unzip -l /tmp/aiv-pack-final/Umbraco.Community.AiVisibility.<version>.nupkg`
- [ ] Confirm zero `content/App_Plugins/...` entries (would be a regression)
- [ ] Confirm zero `contentFiles/any/net10.0/wwwroot/App_Plugins/...` entries (would be a regression)
- [ ] Confirm zero `*.map` files in the release artefact (Story 6.0b § Source maps decision — see below)

### 3. Source maps decision

The committed Vite bundle does NOT include source maps (`vite.config.ts:sourcemap = false`, set in Story 6.0b). Consequence: adopters debugging the Backoffice bundle in DevTools see the minified JS without a back-mapping to the original `.ts` source.

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

- [ ] Every output line maps to a row in `docs/dependency-status.md` § Vulnerability warnings AND `.github/expected-vuln-warnings.txt`
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
- [ ] No `LlmsTxt.Umbraco` / `Llms*` / `llms-` references survive (Story 6.0c rename leftovers)

## Release execution

### 7. Version bump

- [ ] `Directory.Packages.props` Umbraco floor reviewed and bumped if a fix lands
- [ ] `Umbraco.Community.AiVisibility.csproj:<Version>` bumped per [SemVer](https://semver.org/) with the release scope:
  - **Patch** (e.g. `1.0.x` → `1.0.x+1`): bug fixes, no API changes.
  - **Minor** (e.g. `1.0.x` → `1.1.0`): backward-compatible additions. Doctype migration changes, settings-key additions land here.
  - **Major** (e.g. `1.x` → `2.0.0`): breaking API changes. Reserved.
- [ ] `CHANGELOG.md` updated with the new version + summary of changes (link to merged PRs)

### 8. Tag + push

```bash
git tag -a v<version> -m "Release v<version>"
git push origin v<version>
```

GitHub Actions' `release.yml` workflow picks up the tag and runs the NuGet push pipeline.

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

- [ ] Verify the package appears on [nuget.org](https://www.nuget.org/packages/Umbraco.Community.AiVisibility) within ~10 min of the GH Actions release run
- [ ] Verify the Marketplace listing is updated (if changed)
- [ ] Cut a GitHub Release pointing at the tag; copy the CHANGELOG.md entry as the release notes

## When this checklist fails

- **Pack output assertion fails**: investigate the unexpected entry. Either (a) update `.github/scripts/assert-pack-output.sh:ALLOWED_PATTERNS` for a legitimate new ship surface (in the same diff), or (b) tighten the csproj/Vite config to suppress the leak.
- **Vuln-gate fails on a new NU1902/NU1903**: investigate upstream. Either path-a (bump Umbraco floor to a fixed transitive — preferred) or path-b (document in `docs/dependency-status.md` + add to `.github/expected-vuln-warnings.txt` allow-list).
- **Smoke trio fails**: a real defect at the integration boundary. Slip the release until green; the gate exists for this.

## Cross-references

- [`docs/marketplace-listing-checklist.md`](marketplace-listing-checklist.md) — Umbraco Marketplace listing checklist (csproj fields, JSON schema, submission procedure, gotchas).
- [`docs/dependency-status.md`](dependency-status.md) — NU1902/NU1903 + CS0618 catalogue.
- [`docs/maintenance.md`](maintenance.md) — bot-list SHA refresh, two-instance Docker SQL Server setup.
- `.github/workflows/ci.yml` — CI gate definitions (`pack-gate`, vuln gate inside `build-online`, LaunchSmoke gate split).
