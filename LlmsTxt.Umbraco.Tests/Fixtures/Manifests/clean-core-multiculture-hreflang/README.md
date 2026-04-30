# Manifest fixture: `clean-core-multiculture-hreflang`

**Purpose:** Regression guard for the AC3 hreflang variant-suffix shape on `/llms.txt`. Clean.Core 7.0.5 content seeded with two cultures (`en-gb` matched + `fr-fr` sibling); hreflang flag ON; Contact deliberately has NO fr-fr variant so the test pins the "not-every-page-has-a-variant" case.

**Coverage:** AC3 / AC5 / AC6 of Story 2.3 — variant suffix shape, lexicographic ordering, and pages without variants emit no suffix.

## Files

- `fixture.json` — page tree + settings + variant map.
- `expected-llms.txt` — expected `/llms.txt` body. Each linked page that has a `fr-fr` variant carries a ` (fr-fr: /fr/path.md)` suffix; Contact has no suffix.

## Regeneration workflow

Same as `clean-core-default/`:

1. Edit `fixture.json`.
2. Run `dotnet test LlmsTxt.Umbraco.slnx --filter Category=ManifestQuality`.
3. Copy actual output → `expected-llms.txt`.
4. Re-run, commit both.

## What this scenario explicitly does NOT exercise

- The resolver itself (`HreflangVariantsResolver` is unit-tested in `HreflangVariantsResolverTests`). The benchmark feeds `HreflangVariants` directly into the builder via `ManifestFixtureBuilder` — bypassing `IDomainService.GetAll` so the test is hermetic.
- `/llms-full.txt` — hreflang doesn't apply there (AC3 last bullet); no `expected-llms-full.txt`.
- Multiple sibling cultures (de-de + nl-nl + ...) — covered by unit tests in `DefaultLlmsTxtBuilderTests.BuildAsync_HreflangVariantsLexicographicOrder`.
