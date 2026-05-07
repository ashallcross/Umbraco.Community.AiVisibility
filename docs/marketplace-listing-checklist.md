# Umbraco Marketplace — package listing checklist

A consolidated reference for getting a NuGet package listed on `marketplace.umbraco.com`. Package-agnostic, ordered as you'd execute it from "fresh project" to "live listing." Every requirement is sourced from the official Umbraco docs (linked at the end) plus practical lessons from shipping `AgentRun.Umbraco` end-to-end.

> **Note:** this checklist was originally authored for the AgentRun.Umbraco package and is preserved here as a long-lived maintainer reference for `Umbraco.Community.AiVisibility` and any future Cogworks packages targeting the Umbraco Marketplace. Generic placeholders use `{Package}`, `{org}`, `{repo}`. For this package: `{Package}` = `Umbraco.Community.AiVisibility`, `{org}` = `ashallcross`, `{repo}` = `Umbraco.Community.AiVisibility`.

---

## TL;DR — what triggers a listing

A NuGet package appears on the Umbraco Marketplace automatically when **all four** are true:

1. The package's `<PackageTags>` contains the literal token `umbraco-marketplace`.
2. The package has a NuGet dependency on `Umbraco.Cms.*`, `UmbracoCms.*` (Umbraco 8 only), or `Umbraco.Commerce.*`.
3. The package is published to NuGet.org.
4. The package's `<PackageProjectUrl>` resolves to a hostable location where Umbraco can fetch an optional `umbraco-marketplace.json` (commonly your GitHub repo).

The marketplace scans NuGet for newly tagged packages **every 24h at 04:00 UTC**. Use the expedite endpoint (below) to skip the wait.

---

## Refresh cadence — once you're listed

| Operation | Cadence |
|---|---|
| New-package discovery scan on NuGet | Every 24h at 04:00 UTC |
| Refresh known-package metadata (title, description, JSON file, screenshots) | Every 2h |
| Refresh download counts | Every 1h |
| Manual expedite | On-demand, throttled to 1 request/min/package |

Practical implication: **you do not re-publish to NuGet to update title/description/screenshots.** You edit `umbraco-marketplace.json` in the source repo, push, and the marketplace re-fetches within 2h. Saves a lot of grief.

---

## Repo layout — what goes where

For a package developed in a single repo:

```
{repo-root}/
├── {Package}.csproj              # Source — see "Required csproj fields" below
├── README.md                     # Default Marketplace readme (unless overridden)
├── umbraco-marketplace.json      # Optional but strongly recommended — see schema below
├── umbraco-marketplace-readme.md # Optional — overrides README.md for Marketplace only
├── icon.png                      # Optional — referenced from csproj
├── screenshots/                  # Optional — referenced from umbraco-marketplace.json
│   ├── 01-overview.png
│   ├── 02-feature.png
│   └── ...
└── ...
```

**Hosting requirement for `umbraco-marketplace.json`:** the file must be reachable at the URL given by the csproj's `<PackageProjectUrl>`. If `<PackageProjectUrl>` is `https://github.com/{org}/{repo}`, Umbraco looks at the **default branch root** for `umbraco-marketplace.json`. If `<PackageProjectUrl>` is a custom domain like `https://mypackage.com`, Umbraco looks at `https://mypackage.com/umbraco-marketplace.json`.

**Multiple packages in one repo:** suffix the JSON file with the lowercase package ID — e.g. `umbraco-marketplace-my.package.json` — and Umbraco will pick the right one per package.

---

## Step 1 — NuGet csproj setup

Required and recommended fields. Add to the `<PropertyGroup>` block in your `.csproj`:

```xml
<PropertyGroup>
  <!-- Identity -->
  <PackageId>YourPackage.Name</PackageId>
  <Version>1.0.0</Version>
  <Title>Your Package Display Name</Title>
  <Description>One-paragraph adopter-facing description of what the package does.</Description>
  <Authors>Author Name</Authors>

  <!-- THIS IS THE LINE THAT GETS YOU LISTED -->
  <PackageTags>umbraco-marketplace;umbraco;your;other;tags;here</PackageTags>

  <!-- Where the marketplace looks for umbraco-marketplace.json + canonical project home -->
  <PackageProjectUrl>https://github.com/{org}/{repo}</PackageProjectUrl>
  <RepositoryUrl>https://github.com/{org}/{repo}.git</RepositoryUrl>
  <RepositoryType>git</RepositoryType>

  <!-- License — SPDX expression preferred over file -->
  <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
  <!-- ...or if your license isn't an SPDX expression: -->
  <!-- <PackageLicenseFile>LICENSE</PackageLicenseFile> -->

  <!-- README on NuGet listing — fallback to Marketplace if no umbraco-marketplace-readme.md -->
  <PackageReadmeFile>README.md</PackageReadmeFile>

  <!-- Optional but strongly recommended — package icon shown on NuGet AND Marketplace -->
  <PackageIcon>icon.png</PackageIcon>
</PropertyGroup>

<ItemGroup>
  <!-- The README must be packed into the nupkg root -->
  <None Include="README.md" Pack="true" PackagePath="/" />

  <!-- The icon must be packed too if referenced -->
  <None Include="icon.png" Pack="true" PackagePath="/" />
</ItemGroup>
```

### The Umbraco dependency requirement

You MUST have a direct NuGet dependency on at least one Umbraco-prefixed package. The marketplace uses these dependencies to figure out which Umbraco versions your package supports.

```xml
<ItemGroup>
  <PackageReference Include="Umbraco.Cms.Web.Common" Version="14.0.0" />
  <!-- or any Umbraco.Cms.*, UmbracoCms.* (v8 only), Umbraco.Commerce.* -->
</ItemGroup>
```

Even **client-side-only packages** (e.g. a TipTap extension or a backoffice JS plugin) need a NuGet dependency on an Umbraco package — otherwise the marketplace cannot determine version compatibility.

### Tag the installable component only

If your solution has multiple packages — e.g. `MyPackage` references `MyPackage.Core` — only put `umbraco-marketplace` in the **installable** package's tags. Internal/library packages should NOT carry the tag.

### Umbraco 14+ compatibility — IMPORTANT

By default, the marketplace assumes packages targeting **Umbraco 13 or lower** are NOT compatible with Umbraco 14+ (the backoffice rewrite). If your package genuinely supports a wide version range that crosses 13→14, override this assumption in `umbraco-marketplace.json`:

```json
{ "VersionDependencyMode": "SemVer" }
```

`SemVer` mode tells the marketplace to trust your declared dependency range without applying the v14 break assumption.

For `Umbraco.Community.AiVisibility` specifically: the package targets v17+ only, so the v13→v14 break assumption never bites — no `VersionDependencyMode` override needed.

---

## Step 2 — `umbraco-marketplace.json`

Optional but worth the 10 minutes. Without it you get the bare NuGet metadata; with it you control category, screenshots, author bio, license-type display, and more.

### Minimal example

```json
{
  "$schema": "https://marketplace.umbraco.com/umbraco-marketplace-schema.json",
  "Title": "Your Package Display Name",
  "Description": "Punchy one-paragraph description for adopters browsing the marketplace.",
  "Category": "Developer Tools",
  "LicenseTypes": ["Free"],
  "Tags": ["your", "tags", "here", "multi word allowed"],
  "AuthorDetails": {
    "Name": "Author Name",
    "Description": "Short bio — 1 to 2 sentences.",
    "Url": "https://yoursite.com",
    "ImageUrl": "https://github.com/{username}.png"
  },
  "DocumentationUrl": "https://github.com/{org}/{repo}#readme",
  "IssueTrackerUrl": "https://github.com/{org}/{repo}/issues",
  "Screenshots": []
}
```

### Full schema reference

All fields are optional. Add only what you need.

| Field | Type | Notes |
|---|---|---|
| `$schema` | string | Use `https://marketplace.umbraco.com/umbraco-marketplace-schema.json` for IDE autocomplete |
| **`Title`** | string | Overrides csproj `<Title>` for marketplace display |
| **`Description`** | string | Overrides csproj `<Description>` |
| **`Category`** | string | Primary category — see allowed values below |
| **`AlternateCategory`** | string | Secondary category — same allowed values |
| **`Tags`** | string array | Free-text tags, multi-word allowed (`"property editor"` is valid) |
| **`LicenseTypes`** | string array | `["Free"]`, `["Purchase"]`, `["Subscription"]`, or combinations |
| **`PackageType`** | string | `"Package"` (default) or `"Integration"` |
| `Screenshots` | object array | See "Screenshots & video" below |
| `VideoUrl` | string | Single embed-format URL (YouTube embed / watch / Vimeo player) |
| `DocumentationUrl` | string | Link to your docs |
| `DiscussionForumUrl` | string | Link to a forum (Slack/Discord/etc) — leave out if you only do Issues |
| `IssueTrackerUrl` | string | Link to your issue tracker |
| `AuthorDetails.Name` | string | Display name on listing |
| `AuthorDetails.Description` | string | 1-2 sentence bio |
| `AuthorDetails.Url` | string | Blog / company / profile URL |
| `AuthorDetails.ImageUrl` | string | `.png` / `.jpg`. GitHub trick: `https://github.com/{username}.png` works automatically |
| `AuthorDetails.Contributors` | array of `{Name, Url?}` | Hand-curated contributor list |
| `AuthorDetails.SyncContributorsFromRepository` | bool | Set `true` to auto-sync contributors from your GitHub repo |
| `PackagesByAuthor` | string array | Other NuGet IDs you maintain — surfaces a "more by this author" panel |
| `RelatedPackages` | array of `{GroupTitle?, PackageId, Description?}` | Cross-link to complementary packages |
| `IsSubPackageOf` | string | If this is a variant of another package, give the parent NuGet ID |
| `AddOnPackagesRequiredForUmbracoCloud` | string array | NuGet IDs required as separate installs on Umbraco Cloud |
| `VersionSpecificPackageIds` | array of `{UmbracoMajorVersion, PackageId}` | Legacy-package mapping for older Umbraco majors |
| `VersionDependencyMode` | string | `"Default"` (assume v14+ break) or `"SemVer"` (trust declared ranges) |
| `LookingForContributors` | bool | Surfaces a "looking for help" badge |
| `LookingForMaintainer` | bool | Surfaces a "seeking maintainer" badge |

### Allowed `Category` / `AlternateCategory` values

Pick ONE primary, optionally one alternate:

- Analytics & Insights
- Artificial Intelligence
- Campaign & Marketing
- Commerce
- Developer Tools
- Editor Tools
- Headless
- PIM & DAM
- Search
- Themes & Starter Kits
- Translations

For `Umbraco.Community.AiVisibility`: primary `"Artificial Intelligence"`, alternate `"SEO"` is NOT in the canonical list — pick `"Developer Tools"` or `"Analytics & Insights"` as the alternate (the AI Traffic dashboard surface fits Analytics & Insights well).

### Screenshots & video

`Screenshots` is an array. Order in the array == order on the listing page.

```json
"Screenshots": [
  {
    "ImageUrl": "https://raw.githubusercontent.com/{org}/{repo}/main/screenshots/01-overview.png",
    "Caption": "Dashboard overview after install"
  },
  {
    "VideoUrl": "https://www.youtube.com/embed/{videoId}",
    "Caption": "60-second walkthrough"
  }
]
```

Each entry has `ImageUrl` OR `VideoUrl` (mutually exclusive in practice — use one), plus an optional `Caption`.

**Video URL formats accepted:**
- `https://www.youtube.com/embed/{videoId}`
- `https://www.youtube.com/watch?v={videoId}`
- `https://player.vimeo.com/video/{videoId}`

**Image hosting tips:**
- GitHub raw URLs work fine: `https://raw.githubusercontent.com/{org}/{repo}/{branch}/screenshots/foo.png`
- Use `main` not `HEAD` in the URL — branch refs are stable, HEAD is not
- Consistent dimensions across screenshots improves the listing visually (no enforced minimum, but ~1280x800 or 1920x1080 PNG works well)

The official docs do NOT specify minimum count, dimensions, or required formats — common practice is 3-6 screenshots, PNG, ~1280-1920px wide, focused on the package's most distinctive UI.

### Custom marketplace README

By default the marketplace shows your csproj `<PackageReadmeFile>` content. To show different content on the marketplace specifically (e.g. a more adopter-flavoured intro vs a developer-flavoured GitHub README):

- Create `umbraco-marketplace-readme.md` in the same repo location as `umbraco-marketplace.json`
- For multi-package repos: `umbraco-marketplace-readme-{lowercase.package.id}.md`

---

## Step 3 — Author / organisation

There is **no formal author registration**. The marketplace pulls author info from:

1. csproj `<Authors>` field — fallback default
2. `AuthorDetails` block in `umbraco-marketplace.json` — overrides #1

If you want a polished listing:

- Add an `AuthorDetails.ImageUrl` — for individuals, `https://github.com/{username}.png` is the path of least resistance
- Add a 1-2 sentence `AuthorDetails.Description` so people know who's behind the package
- Set `SyncContributorsFromRepository: true` if your repo has a healthy contributor history

---

## Step 4 — Pack and publish to NuGet

```bash
# Pack
dotnet pack {Package}.csproj --configuration Release --output ./bin/release/{version}/

# Sanity-check the nupkg before push
unzip -p ./bin/release/{version}/{Package}.{version}.nupkg {Package}.nuspec | grep -E "version|tag|projectUrl"
# Should show:
#   <version>{version}</version>
#   <tags>... umbraco-marketplace ...</tags>
#   <projectUrl>https://github.com/{org}/{repo}</projectUrl>

# Publish
dotnet nuget push ./bin/release/{version}/{Package}.{version}.nupkg \
  --source https://api.nuget.org/v3/index.json \
  --api-key $NUGET_API_KEY
```

Wait 5-15 minutes for NuGet propagation. Confirm at `https://www.nuget.org/packages/{Package}/{version}`.

**Irreversibility note:** NuGet does not allow re-uploading the same version after delete. The only recovery is `dotnet nuget delete` (which UNLISTS, not hard-deletes) and shipping a patch version. Test before pushing.

---

## Step 5 — Trigger marketplace sync (skip the 24h wait)

```bash
curl -X POST https://functions.marketplace.umbraco.com/api/InitiateSinglePackageSyncFunction \
  -H "Content-Type: application/json" \
  -d '{"PackageId": "{Package}"}'
```

- **No authentication required**
- **Throttle:** 1 request per minute per `PackageId`
- **Response:** typically 200/202 with empty or minimal body
- **What it does:** tells the marketplace indexer to immediately fetch your package metadata from NuGet + your `umbraco-marketplace.json` from `<PackageProjectUrl>`

If the endpoint times out or 5xx's, retry once after 60s. If it persists, file an issue on `github.com/umbraco/Umbraco.Marketplace.Issues`. NuGet remains the primary distribution channel regardless — marketplace listing is supplementary.

---

## Step 6 — Validate the listing

`https://marketplace.umbraco.com/validate` is the public validation tool. Paste your package ID and it checks:

- NuGet package found and tagged correctly
- Umbraco-prefixed dependency present and version range parsed
- `umbraco-marketplace.json` resolved successfully (if you ship one)
- Screenshot URLs are reachable
- License type recognised

Run this once after the first sync completes (~30 min after `dotnet nuget push` + expedite). Fix anything it flags before announcing the listing publicly.

---

## Step 7 — Updating the listing (post-launch)

Three common update paths:

| Want to update | What to do | Time to live |
|---|---|---|
| New code version | `dotnet pack` → `dotnet nuget push` → trigger expedite | ~30 min |
| Listing copy (title, description, tags, screenshots) | Edit `umbraco-marketplace.json`, push to repo | Next 2h refresh |
| Author / contributors | Same as above | Next 2h refresh |

**You do NOT need to re-publish to NuGet** to change non-code metadata. The marketplace re-fetches `umbraco-marketplace.json` every 2h. This is the killer feature of the JSON-file approach.

---

## Step 8 — Stay in the loop

- **Marketplace newsletter:** sign up at `https://umbraco.activehosted.com/f/6` for change announcements
- **Issue tracker for marketplace itself:** `github.com/umbraco/Umbraco.Marketplace.Issues`

---

## Common gotchas

1. **`<PackageTags>` is a SEMICOLON-separated string in csproj.** Don't use commas. `umbraco-marketplace;umbraco;ai` — the `umbraco-marketplace` token is the load-bearing one.
2. **`umbraco-marketplace.json` MUST be reachable from `<PackageProjectUrl>`.** If your project URL is a generic landing page like `https://yourcompany.com/products/package` and you put the JSON at the repo root instead — it won't be found. Either fix the URL to point at your repo, or host the JSON on the landing page domain.
3. **Indirect Umbraco dependency is NOT enough on its own.** If your package depends on `MyHelpers.Core` which depends on `Umbraco.Cms.Core`, the marketplace will use the transitive dependency BUT only as a fallback. Add a direct dependency on `Umbraco.Cms.*` for predictable version detection.
4. **The Umbraco 14+ break-assumption is silent.** A package targeting `Umbraco.Cms.Core` v13 with no `VersionDependencyMode: SemVer` will be invisible to v14+ users even if it secretly works fine. Set `SemVer` if you've actually tested across the v13→v14 boundary. (Not relevant for v17+-only packages like `Umbraco.Community.AiVisibility`.)
5. **NuGet cache stale after re-pack.** When testing locally with `--source ./bin/release/{version}/`, clear `~/.nuget/packages/{lowercase.package.id}/{version}` before re-installing or NuGet will use the cached old bytes.
6. **Marketplace listing 404s for the first ~30 minutes** even after the expedite endpoint returns 200. Be patient. If it's still 404 at 24h, there's a real problem to investigate — check the validate endpoint first.
7. **Screenshots use raw GitHub URLs not blob URLs.** `https://raw.githubusercontent.com/...` (raw) NOT `https://github.com/.../blob/...` (blob — that's the GitHub-rendered HTML page, not the image bytes).
8. **Apache 2.0 / MIT / etc as SPDX expression beats `<PackageLicenseFile>`.** SPDX is recognised by NuGet AND the marketplace AND every license analyser; a file reference is fine but the expression is friction-free.
9. **Single-commit force-pushed repos lose contributor history.** If you used `SyncContributorsFromRepository: true`, a force-push that resets history wipes your contributors. Either keep history honest or set the field to `false` and curate manually.
10. **The expedite endpoint is per-PackageId.** If you ship multiple related packages, you have to fire the curl for each separately. Easy to forget on multi-package solutions.

---

## Reference checklist (paste into your project tracker)

```markdown
## Umbraco Marketplace listing readiness

### NuGet csproj
- [ ] `<PackageId>` set
- [ ] `<Version>` set (SemVer)
- [ ] `<Title>` set
- [ ] `<Description>` set (paragraph, adopter-facing)
- [ ] `<Authors>` set
- [ ] `<PackageTags>` includes `umbraco-marketplace`
- [ ] `<PackageProjectUrl>` resolves and hosts `umbraco-marketplace.json`
- [ ] `<RepositoryUrl>` + `<RepositoryType>git</RepositoryType>` set
- [ ] `<PackageLicenseExpression>` set (SPDX) OR `<PackageLicenseFile>` set
- [ ] `<PackageReadmeFile>` set + README packed
- [ ] `<PackageIcon>` set + icon packed (optional but strongly recommended)
- [ ] Direct NuGet dependency on `Umbraco.Cms.*` / `UmbracoCms.*` / `Umbraco.Commerce.*`
- [ ] `umbraco-marketplace` tag NOT applied to internal sub-packages

### umbraco-marketplace.json (optional but recommended)
- [ ] File at the URL given by `<PackageProjectUrl>`
- [ ] `$schema` reference for IDE autocomplete
- [ ] `Title` (overrides csproj for marketplace)
- [ ] `Description` (overrides csproj for marketplace)
- [ ] `Category` from allowed list
- [ ] `LicenseTypes` array
- [ ] `Tags` array
- [ ] `AuthorDetails.Name` + `Description` + `ImageUrl` set
- [ ] `DocumentationUrl` + `IssueTrackerUrl` set
- [ ] `Screenshots` array populated (3-6 recommended)
- [ ] `VersionDependencyMode: SemVer` if package crosses v13→v14 boundary

### Publishing
- [ ] `dotnet pack` produces a clean nupkg with `<umbraco-marketplace>` tag visible
- [ ] `dotnet nuget push` succeeds, listing live at `nuget.org/packages/{Package}/{version}`
- [ ] `curl POST https://functions.marketplace.umbraco.com/api/InitiateSinglePackageSyncFunction` fired with `{"PackageId": "{Package}"}`
- [ ] `marketplace.umbraco.com/validate` returns clean check
- [ ] Listing visible at `marketplace.umbraco.com/package/{Package}` (allow up to 24h)
```

---

## Sources

- [Umbraco Marketplace docs — Listing Your Package](https://docs.umbraco.com/umbraco-dxp/marketplace/listing-your-package)
- [Umbraco Marketplace JSON schema](https://marketplace.umbraco.com/umbraco-marketplace-schema.json)
- [Umbraco Marketplace validation tool](https://marketplace.umbraco.com/validate)
- [Marketplace expedite endpoint](https://functions.marketplace.umbraco.com/api/InitiateSinglePackageSyncFunction) (POST, no auth)
- [Marketplace issue tracker](https://github.com/umbraco/Umbraco.Marketplace.Issues)
- [Marketplace newsletter signup](https://umbraco.activehosted.com/f/6)
