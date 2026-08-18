# Dependency status

When you `dotnet add package Umbraco.Community.AiVisibility` and build, you may see vulnerability and obsolete-API warnings. This doc explains what they are, why we ship with them, and when we expect them to be resolved.

## Vulnerability warnings (NU1902 / NU1903)

Two warnings surface during build. Both come from packages that Umbraco itself pulls in transitively — we can't bump them directly without Umbraco's own dependency tree being updated upstream first.

| Severity | Code | Package + version | Why it's transitive | Status |
|---|---|---|---|---|
| Moderate | NU1902 | `MailKit` 4.15.1 — [GHSA-9j88-vvj5-vhgr](https://github.com/advisories/GHSA-9j88-vvj5-vhgr) | Pulled in by Umbraco's email infrastructure (`Umbraco.Cms.Infrastructure`). | Awaiting upstream Umbraco patch. Re-checked at every package release; will resolve when an Umbraco 17.3.x patch ships with a fixed `MailKit` transitive. |
| High | NU1903 | `System.Security.Cryptography.Xml` 8.0.0 — [GHSA-37gx-xxp4-5rgx](https://github.com/advisories/GHSA-37gx-xxp4-5rgx) + [GHSA-w3x6-4m5h-cxqf](https://github.com/advisories/GHSA-w3x6-4m5h-cxqf) | Pulled in by Umbraco's security / health-check infrastructure. | Same — awaiting upstream Umbraco patch. |

### What you can do

- **Treat both as you would any other transitive vuln warning in the Umbraco stack.** Both are functions of Umbraco's own deps, not the package's. Bumping your Umbraco floor when 17.3.5+ (or whichever patch addresses them) ships will resolve them.
- **If your security policy requires zero NU1903 warnings before deploy**, you can suppress them via `<NoWarn>NU1902;NU1903</NoWarn>` in your csproj — but be deliberate about that decision; the warnings are surfacing real CVEs in the underlying libraries.
- **Each release is checked for any NEW NU1902 / NU1903 beyond these.** Releases are cut manually (the package has no CI pipeline), so this is a manual pre-ship check; a newly-introduced third advisory is caught during that review before publishing.

## Obsolete API warnings (CS0618)

Eleven `CS0618` warnings appear on build, indicating Umbraco APIs scheduled for removal in v18 (or v19). These are opt-in deprecations on Umbraco's side — the APIs still work fine on 17.x; they'll only become hard errors when the corresponding Umbraco major lands.

The package will migrate these in lockstep with the Umbraco floor bump (a v18 floor bump arrives with a single sweep of all v18-removal sites).

### Sites slated for v18 migration

| Obsolete API | Where it's used in the package | Replacement we'll move to |
|---|---|---|
| `ILocalizationService` | `LlmsTxt/HostnameRootResolver.cs` (3 sites) | `ILanguageService` + `IDictionaryItemService` |
| `IDomainService.GetAll(bool)` | `LlmsTxt/HostnameRootResolver.cs`, `LlmsTxt/HreflangVariantsResolver.cs` (CS0618 warning sites). Also pragma-suppressed at `Robots/RobotsAuditHealthCheck.cs`, `Robots/StartupRobotsAuditRunner.cs`, `Telemetry/RobotsAuditRefreshJob.cs` (`#pragma warning disable CS0618` — same v18 migration scope, just absent from the build-warning count). | `IDomainService.GetAllAsync(...)` |
| `IDataTypeService.GetDataType(int)` | `Persistence/Migrations/CreateAiVisibilitySettingsDoctype.cs` (3 sites) | `IDataTypeService.GetAsync(...)` |
| `IContentTypeBaseService<IContentType>.Save(IContentType?, int)` | `Persistence/Migrations/CreateAiVisibilitySettingsDoctype.cs` (2 sites) | The "respective Create or Update" replacement (verified at v18 release time) |

### Sites slated for v19 migration

| Obsolete API | Where it's used in the package | Replacement we'll move to |
|---|---|---|
| `IContentService.GetPagedChildren(int, long, int, out long, IQuery<IContent>?, Ordering?)` | `Backoffice/SettingsManagementApiController.cs` | The all-parameters overload |

### What you can do

- **Nothing required for v17.x adopters** — the warnings are benign on 17.x.
- **If you fork the package** and want to migrate ahead of the upstream cadence, the table above pinpoints every call site. PRs welcome.
- **CS0618 warnings are not treated as build errors** — they're expected, and failing the build on them would block legitimate Umbraco-side deprecation rollouts. They'll escalate to compile errors automatically when the Umbraco floor bumps to v18.

## v13 lite SKU (`Umbraco.Community.AiVisibility.V13`)

The warnings above are the **v17 / .NET 10** transitive set. The v13 lite package pulls a different Umbraco major (13.x) on .NET 8, so its transitive advisory set differs:

- The v13 line requires **Umbraco 13.15.0+**. A moderate advisory ([GHSA-54mj-vcvj-q3v5](https://github.com/advisories/GHSA-54mj-vcvj-q3v5)) currently affects the Umbraco 13.x line at the 13.15.0 floor; because the package's Umbraco dependency is a minimum floor, an up-to-date site resolves forward automatically — run the latest published 13.x patch to pick up the newest framework fixes.
- The remaining v13-line warnings (`MailKit` / `MimeKit` NU1902, and a build-time `Microsoft.Build.Tasks.Core` NU1903) are the same class as the v17 ones — transitive from Umbraco's own dependency tree and the build tooling, awaiting upstream patches. Treat them exactly as the v17 warnings above: bump your Umbraco 13 floor when a patched 13.x ships.

Both SKUs are checked for newly-introduced advisories at each manual release.

## See also

- [`README.md`](../README.md) § Compatibility — pinned Umbraco floor + .NET version.
- [`v13-support.md`](v13-support.md) — v13 vs v17 capability matrix + support window.
