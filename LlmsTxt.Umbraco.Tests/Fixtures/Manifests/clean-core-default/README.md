# Manifest fixture: `clean-core-default`

**Purpose:** Regression guard for the zero-config Story 2.1 manifest shape — Clean.Core 7.0.5 demo content, single language, no hreflang, no section grouping.

**Coverage:** AC1 / AC4 / AC5 / AC6 of Story 2.3 (the "no-frills" baseline that proves the index manifest builder produces the expected shape unchanged from Story 2.1 once Story 2.3's surfaces are introduced).

## Files

- `fixture.json` — page tree + settings descriptor consumed by `ManifestFixtureBuilder`.
- `expected-llms.txt` — the expected `/llms.txt` body for this fixture, byte-equal-compared by `ManifestQualityBenchmarkTests.Manifest_MatchesExpectedFixture`.

## Regeneration workflow

When the manifest output shape changes deliberately (e.g. Story 2.x adds a new field to the per-page link line, or the H1 / blockquote shape changes):

1. Edit `fixture.json` to reflect any new input data the fixture should exercise.
2. Run `dotnet test LlmsTxt.Umbraco.slnx --filter Category=ManifestQuality` and let the test fail with the unified diff.
3. Inspect the diff — confirm the new actual output is correct.
4. Copy the actual output back into `expected-llms.txt`.
5. Re-run the test — it should now pass.
6. Commit `fixture.json` + `expected-llms.txt` together.

## NOT covered

- `/llms-full.txt` (no `expected-llms-full.txt` in this scenario — single-language Clean.Core's full-text shape is exercised separately by `DefaultLlmsFullBuilderTests`).
- Hreflang variants — see sibling scenario `clean-core-multiculture-hreflang/`.
- Section grouping — out of scope for the v1 baseline.
