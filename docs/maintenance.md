# Maintenance — for package maintainers

Operational notes for maintainers of LlmsTxt.Umbraco. End-user / adopter docs live in [`getting-started.md`](getting-started.md).

## Refreshing the pinned AI-bot-list SHA

The build-time `SyncAiBotList` MSBuild target (in `LlmsTxt.Umbraco/LlmsTxt.Umbraco.csproj`) fetches `https://raw.githubusercontent.com/ai-robots-txt/ai.robots.txt/main/robots.txt` and verifies its SHA-256 against the `<ExpectedAiBotListSha256>` constant. **Mismatch is a hard build failure** — deliberate, to surface upstream changes for review rather than silently embedding new tokens.

When the upstream feed updates, the build fails with:

```
LlmsTxt: AI bot list SHA mismatch — expected <pinned>, got <actual>.
Refresh the pinned SHA via docs/maintenance.md after reviewing the upstream change.
```

To refresh:

1. **Review the upstream diff.** Visit [https://github.com/ai-robots-txt/ai.robots.txt/commits/main/robots.txt](https://github.com/ai-robots-txt/ai.robots.txt/commits/main/robots.txt). New tokens, removed tokens, deprecation changes — anything that looks suspicious is a signal to delay (the curated map in `AiBotList.cs` may need updating; a new vendor's bot may belong in a new category).

2. **Refresh both files in lockstep.** From the repo root:
   ```bash
   curl -fsSL https://raw.githubusercontent.com/ai-robots-txt/ai.robots.txt/main/robots.txt \
     -o LlmsTxt.Umbraco/HealthChecks/AiBotList.fallback.txt
   shasum -a 256 LlmsTxt.Umbraco/HealthChecks/AiBotList.fallback.txt
   ```
   Update **both** `<ExpectedAiBotListSha256>` AND `<ExpectedAiBotListFallbackSha256>` in `LlmsTxt.Umbraco/LlmsTxt.Umbraco.csproj` to the new SHA (lowercase hex, no spaces). The build verifies both: the upstream pin gates the online fetch; the fallback pin gates the committed snapshot. They can deliberately differ during an upstream-broken interregnum, but the common case is that they're identical.

3. **Update the curated category map** in `LlmsTxt.Umbraco/HealthChecks/AiBotList.cs` for any new tokens that should not surface as `BotCategory.Unknown`. New deprecations go in `AiBotList.DeprecatedTokens`.

4. **Run the build matrix** locally:
   ```bash
   # Online build — should succeed with "synced from upstream" log line
   dotnet build LlmsTxt.Umbraco.slnx --configuration Release
   # Offline simulation — should succeed with "Falling back to AiBotList.fallback.txt" warning
   dotnet build LlmsTxt.Umbraco.slnx --configuration Release \
     /p:AiBotListSourceUrl=http://localhost:65535/unreachable
   ```

5. **Run the test suite** — `dotnet test LlmsTxt.Umbraco.slnx --configuration Release`. The `AiBotListTests.Load_RealEmbeddedResource_HasKnownTokens` test pins anchor tokens (`GPTBot`, `ClaudeBot`, `anthropic-ai`, `Bytespider`) — if upstream removes any of them, this test fails. Update the test fixture in the same PR.

6. **Open a PR** with the changes scoped to the three files (`AiBotList.fallback.txt`, the csproj's `<ExpectedAiBotListSha256>`, optional `AiBotList.cs` curated-map updates). Title format: `chore: refresh AI bot list (YYYY-MM-DD upstream snapshot)`. Include the upstream commit SHA in the body.

## Source URL drift handling

The `<AiBotListSourceUrl>` constant in `LlmsTxt.Umbraco.csproj` points at `https://raw.githubusercontent.com/ai-robots-txt/ai.robots.txt/main/robots.txt`. If the upstream repository moves (org rename, repo rename, file rename, branch retirement, archival), the build's online fetch starts returning 404. The fallback path absorbs the failure into a warning + embeds the committed snapshot — adopters keep working — but maintainers MUST react.

Symptoms:

- Local + CI builds emit `MSB3923: Failed to download file …` followed by the `Falling back to AiBotList.fallback.txt` warning. Online builds pass via fallback (silent regression: pinned SHA never re-verified).
- Online build's grep assertion (`grep "AI bot list synced from upstream"`) starts failing — the CI gate flips red where it had been green.

Response:

1. **Confirm the move.** Check `https://github.com/ai-robots-txt/` for an org redirect; check the original repo's README for a notice. Inspect `https://github.com/ai-robots-txt/ai.robots.txt/commits/main` — last commit date older than ~6 months is a strong drift signal.
2. **Find the new canonical URL** (or accept that the project is dead). If a successor exists, switch `<AiBotListSourceUrl>` in the csproj. Refresh `<ExpectedAiBotListSha256>`, `<ExpectedAiBotListFallbackSha256>`, and `AiBotList.fallback.txt` per the section above. The curated category map likely needs review (different upstream conventions).
3. **If no successor exists**, the package's audit still runs against the committed fallback. Consider: (a) freezing the fallback as the long-lived snapshot + dropping the online fetch entirely; (b) maintaining the fallback in-house going forward (manual curation against operator docs); (c) adopting a different upstream feed (`darkvisitors.com`, `cloudflare/known-bots`, etc.) — each comes with its own provenance trade-offs.

Whatever the choice, document it in `_bmad-output/planning-artifacts/architecture.md` § Robots.txt Audit before shipping the change.

## Why no scheduled auto-refresh

A scheduled GitHub Action that auto-bumps the SHA would defeat the protection. The pin's purpose is to force human review of upstream changes — without that gate, an upstream maintainer or supply-chain attacker could ship arbitrary tokens into the embedded list. **The cost** is one PR per month or so (upstream cadence is roughly monthly); **the benefit** is provenance review at every change. The trade is favourable.

If a future story decides this is too expensive, the upgrade is a scheduled GitHub Action that opens the PR + runs the tests but **never auto-merges** — the human review step stays. Document that decision in `_bmad-output/planning-artifacts/architecture.md` § Robots.txt Audit before shipping the action.

## Two-instance shared-SQL-Server manual gate setup

Story 4.2's manual E2E gate requires verifying `IDistributedBackgroundJob` exactly-once execution across two TestSite instances against shared SQL Server (NOT SQLite — the lock semantics differ materially per `feedback_production_env_assumptions.md`).

### One-time SQL Server setup

The simplest reproducible setup is a Docker container:

```bash
docker run --name umbraco-sqlserver \
  -e "ACCEPT_EULA=Y" \
  -e "MSSQL_SA_PASSWORD=YourStrong!Passw0rd" \
  -p 1433:1433 \
  -d mcr.microsoft.com/mssql/server:2022-latest

# Create the LlmsTxt-test database
docker exec -i umbraco-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U SA -P "YourStrong!Passw0rd" -No \
  -Q "CREATE DATABASE [LlmsTxtTest]"
```

### TestSite configuration

Update `LlmsTxt.Umbraco.TestSite/appsettings.Development.json` to point at SQL Server:

```json
{
  "ConnectionStrings": {
    "umbracoDbDSN": "Server=localhost,1433;Database=LlmsTxtTest;User Id=SA;Password=YourStrong!Passw0rd;TrustServerCertificate=true",
    "umbracoDbDSN_ProviderName": "Microsoft.Data.SqlClient"
  }
}
```

### Two-instance run

Spin two instances against the same database:

```bash
# Terminal 1 — instance A
dotnet run --project LlmsTxt.Umbraco.TestSite \
  --urls "https://localhost:44314" \
  --launch-profile "Development"

# Terminal 2 — instance B (different port, same DB)
dotnet run --project LlmsTxt.Umbraco.TestSite \
  --urls "https://localhost:44315" \
  --launch-profile "Development"
```

Watch both instances' logs for `Robots audit refresh job RUN — InstanceId=…`. The architect-mandated invariant per cycle: **exactly one** RUN entry across the two instances. If you see two entries for the same `CycleStart`, stop — `IDistributedBackgroundJob` coordination is broken and re-running Spike 0.B is cheaper than absorbing the drift.

### Verifying `LogRetentionJob` exactly-once (Story 5.1)

The same Docker SQL Server 2022 setup verifies Story 5.1's `LogRetentionJob` (`IDistributedBackgroundJob`). Configure both TestSite instances with `LlmsTxt:LogRetention:DurationDays: 30` and `LlmsTxt:LogRetention:RunIntervalSecondsOverride: 30` (the dev-only escape hatch — do NOT use in production) so cycles tick every 30 seconds rather than every 24 hours. Hit a few `.md` / `/llms.txt` / `/llms-full.txt` URLs across both instances to populate `llmsTxtRequestLog`.

Watch both instances' logs for `LlmsTxt log retention job RUN — InstanceId=… CycleStart=… RowsDeleted=…`. Same invariant: **exactly one** RUN entry per cycle across the two instances. If you see two entries for the same `CycleStart`, stop — re-running Spike 0.B is cheaper than absorbing the drift.

Story 5.1 is the second consumer of this two-instance setup; future stories with `IDistributedBackgroundJob` exactly-once gates reuse the same procedure.

### Teardown

```bash
docker stop umbraco-sqlserver
docker rm umbraco-sqlserver
```

Subsequent manual gates can reuse the same setup; the container holds no state once removed.

## Future scheduled actions (out of scope for v1)

These are documented for forward visibility only — they do NOT ship with v1:

- **Monthly auto-PR for AI-bot-list refresh** — see "Why no scheduled auto-refresh" above. If this lands later, it must be PR-only (no auto-merge) and pass the existing build matrix.
- **Quarterly upstream-curated-map review** — scan `BotCategory.Unknown` findings from real adopter audits + landed upstream additions; update the curated map. Manual today.
