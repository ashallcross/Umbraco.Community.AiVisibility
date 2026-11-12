# Dependency status

When you `dotnet add package Umbraco.Community.AiVisibility` and build, you may see vulnerability and obsolete-API warnings. This doc explains what they are, why we ship with them, and when we expect them to be resolved.

## Extraction dependencies

The Markdown pipeline depends on these packages directly (as of v1.3.0). They flow to your site as package dependencies, so an adopter `IMarkdownContentExtractor` or `IContentRegionSelector` that uses them compiles against these versions or newer:

| Package | Version | Used for |
|---|---|---|
| `ReverseMarkdown` | 6.3.0 | HTML → Markdown conversion |
| `AngleSharp` | 1.5.2 | HTML parsing and region selection (ReverseMarkdown 6.3.0 requires 1.5.2 or newer) |
| `SmartReader` | 0.11.0 | Readability-style fallback when no content region matches |

## Vulnerability warnings (NU1902 / NU1903)

As of v1.3.0, building the package against its Umbraco 17.3.2 floor reports the advisories below. All come from packages that Umbraco itself pulls in transitively — we can't bump them directly without Umbraco's own dependency tree being updated upstream first.

| Severity | Code | Package + version | Why it's transitive | Status |
|---|---|---|---|---|
| Moderate | NU1902 | `MailKit` 4.15.1 — [GHSA-9j88-vvj5-vhgr](https://github.com/advisories/GHSA-9j88-vvj5-vhgr) | Pulled in by Umbraco's email infrastructure (`Umbraco.Cms.Infrastructure`). | Awaiting upstream Umbraco patch. Re-checked at every v17 package release. |
| High | NU1903 | `System.Security.Cryptography.Xml` 8.0.0 — [GHSA-37gx-xxp4-5rgx](https://github.com/advisories/GHSA-37gx-xxp4-5rgx), [GHSA-w3x6-4m5h-cxqf](https://github.com/advisories/GHSA-w3x6-4m5h-cxqf) and further advisories against the same version | Pulled in by Umbraco's security / health-check infrastructure. | Same — awaiting upstream Umbraco patch. |
| High + moderate | NU1903 / NU1902 | `System.Security.Cryptography.Pkcs` 10.0.0 — [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx), [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) | Pulled in by Umbraco's dependency tree. | Same — awaiting upstream Umbraco patch. |
| High + moderate | NU1903 / NU1902 | `MessagePack` 3.1.4 — several advisories, e.g. [GHSA-382j-8mxh-c7x2](https://github.com/advisories/GHSA-382j-8mxh-c7x2) | Pulled in by Umbraco's dependency tree. | Same — awaiting upstream Umbraco patch. |
| High | NU1903 | `Microsoft.OpenApi` 2.4.1 — [GHSA-v5pm-xwqc-g5wc](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc) | Pulled in by Umbraco's dependency tree. | Same — awaiting upstream Umbraco patch. |

None of these come from the package's own direct dependencies (ReverseMarkdown, AngleSharp, SmartReader). Your site resolves Umbraco's dependency tree at the Umbraco version you install, so a newer 17.x may already carry patched versions of some of them.

### What you can do

- **Treat these as you would any other transitive vuln warning in the Umbraco stack.** They are functions of Umbraco's own deps, not the package's. Running the latest Umbraco 17.x patch picks up fixed transitive versions as Umbraco ships them.
- **If your security policy requires zero NU1903 warnings before deploy**, you can suppress them via `<NoWarn>NU1902;NU1903</NoWarn>` in your csproj — but be deliberate about that decision; the warnings are surfacing real CVEs in the underlying libraries.
- **Each release is checked for any NEW NU1902 / NU1903 beyond these.** Releases are cut manually (the package has no CI pipeline), so this is a manual pre-ship check; a newly-introduced advisory is caught during that review before publishing.

## Obsolete API warnings (CS0618)

Eleven `CS0618` warning sites appear on a Release build of the package (as of v1.3.0), indicating Umbraco APIs scheduled for removal in v18 (or v19). These are opt-in deprecations on Umbraco's side — the APIs still work fine on 17.x; they'll only become hard errors when the corresponding Umbraco major lands.

The package will migrate these in lockstep with the Umbraco floor bump (a v18 floor bump arrives with a single sweep of all v18-removal sites).

### Sites slated for v18 migration

Paths starting `Core/` are in the shared `Umbraco.Community.AiVisibility.Core` project (compiled into the package DLL); the rest are in the `Umbraco.Community.AiVisibility` project.

| Obsolete API | Where it's used in the package | Replacement we'll move to |
|---|---|---|
| `ILocalizationService` | `Core/LlmsTxt/HostnameRootResolver.cs` (3 sites: two references to the `ILocalizationService` type plus one `GetDefaultLanguageIsoCode()` call) | `ILanguageService` + `IDictionaryItemService` |
| `IDomainService.GetAll(bool)` | `Core/LlmsTxt/HostnameRootResolver.cs`, `Core/LlmsTxt/HreflangVariantsResolver.cs` (CS0618 warning sites). Also pragma-suppressed at `Robots/RobotsAuditHealthCheck.cs`, `Core/Robots/StartupRobotsAuditRunner.cs`, `Telemetry/RobotsAuditRefreshJob.cs` (`#pragma warning disable CS0618` — same v18 migration scope, just absent from the build-warning count). | `IDomainService.GetAllAsync(...)` |
| `IDataTypeService.GetDataType(int)` | `Core/Persistence/Migrations/CreateAiVisibilitySettingsDoctype.cs` (3 sites) | `IDataTypeService.GetAsync(...)` |
| `IContentTypeBaseService<IContentType>.Save(IContentType?, int)` | `Core/Persistence/Migrations/CreateAiVisibilitySettingsDoctype.cs` (2 sites). Also pragma-suppressed at `Core/Persistence/Migrations/MoveExclusionGroupIntoTab_1_3.cs` (1 site — same v18 migration scope, absent from the build-warning count). | The "respective Create or Update" replacement (verified at v18 release time) |

### Sites slated for v19 migration

| Obsolete API | Where it's used in the package | Replacement we'll move to |
|---|---|---|
| `IContentService.GetPagedChildren(int, long, int, out long, IQuery<IContent>?, Ordering?)` | `Backoffice/SettingsManagementApiController.cs` | The all-parameters overload |

### What you can do

- **Nothing required for v17.x adopters** — the warnings are benign on 17.x.
- **If you fork the package** and want to migrate ahead of the upstream cadence, the table above pinpoints every call site. PRs welcome.
- **CS0618 warnings are not treated as build errors** — they're expected, and failing the build on them would block legitimate Umbraco-side deprecation rollouts. They'll escalate to compile errors automatically when the Umbraco floor bumps to v18.

## v13 lite SKU (`Umbraco.Community.AiVisibility.V13`)

The warnings above are the **v17 / .NET 10** set, and only the v17 package is re-checked at each release. The v13 lite package is **frozen at 1.2.x** — it receives no further releases, so its dependency set is not re-checked or updated, and it is unsupported from December 2026 (Umbraco 13 end of support). See [`v13-support.md`](v13-support.md).

As of its last release, the v13 line required **Umbraco 13.15.0+**; because that dependency is a minimum floor, an up-to-date Umbraco 13 site resolves forward to the latest 13.x patch. Its transitive warnings (`MailKit` / `MimeKit` NU1902, a build-time `Microsoft.Build.Tasks.Core` NU1903, and an Umbraco 13.x advisory, [GHSA-54mj-vcvj-q3v5](https://github.com/advisories/GHSA-54mj-vcvj-q3v5), at the 13.15.0 floor) came from Umbraco's own dependency tree and the build tooling.

## See also

- [`README.md`](../README.md) § Compatibility — pinned Umbraco floor + .NET version.
- [`v13-support.md`](v13-support.md) — v13 vs v17 capability matrix + support window.
