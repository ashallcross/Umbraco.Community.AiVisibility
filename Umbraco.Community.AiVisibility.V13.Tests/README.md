# Umbraco.Community.AiVisibility.V13.Tests — how to run

This project holds the **v13 lite SKU** tests. It is `net8.0` (the v13 shell TFM) and
splits into two layers with **different run requirements**:

| Layer | Category | Boots Umbraco? | Run host |
|---|---|---|---|
| Unit — DI wiring, recurring jobs, sync migrations | _(uncategorised)_ | No | net10 SDK (rolls forward) **or** net8 keg |
| Extraction TFM-stability | `ExtractionQuality` | No (pure HTML→Markdown) | net10 SDK or net8 keg |
| Integration smokes | `Integration` | **Yes — boots a real Umbraco 13 stack** | **.NET 8 runtime required** |

## Build (always the .NET 10 SDK)

The shared `Core` library multi-targets `net8.0;net10.0`; only the **.NET 10 SDK** can
build its net10 target (the net8 SDK fails with `NETSDK1045`). Build everything with the
net10 SDK — it down-targets net8 cleanly:

```bash
dotnet build Umbraco.Community.AiVisibility.V13.Tests/Umbraco.Community.AiVisibility.V13.Tests.csproj \
  -p:AiBotListSourceUrl=http://127.0.0.1:1/none
```

(The `AiBotListSourceUrl` override forces the offline bot-list fallback for a
pre-existing upstream SHA-pin drift unrelated to these tests.)

## Run the unit + extraction tests (no Umbraco boot — net10 host is fine)

```bash
dotnet test Umbraco.Community.AiVisibility.V13.Tests/Umbraco.Community.AiVisibility.V13.Tests.csproj \
  --no-build --filter "Category!=Integration" -p:AiBotListSourceUrl=http://127.0.0.1:1/none
```

## Run the integration smokes (REQUIRES the .NET 8 runtime)

The `Integration` smokes boot the `Umbraco.Community.AiVisibility.V13.TestSite` in-process
via `WebApplicationFactory<Program>` — a full Umbraco 13 boot. Booting v13 under a net10
roll-forward host is **not a sanctioned run mode**; use the **.NET 8 runtime** (on this
machine, the keg-only `dotnet@8` install):

```bash
export DOTNET_ROOT=/usr/local/Cellar/dotnet@8/8.0.128/libexec
/usr/local/Cellar/dotnet@8/8.0.128/libexec/dotnet test \
  Umbraco.Community.AiVisibility.V13.Tests/Umbraco.Community.AiVisibility.V13.Tests.csproj \
  --no-build --filter "Category=Integration" -p:AiBotListSourceUrl=http://127.0.0.1:1/none
```

The first integration run boots Umbraco once (unattended install + AiVisibility v13 sync
migration + Clean 4.2.2 seed, ~30-60s), shared across all integration fixtures via the
`SharedV13Site` `[SetUpFixture]`. Without the .NET 8 runtime the integration tests are
expected to be skipped/inconclusive — never silently passed.
