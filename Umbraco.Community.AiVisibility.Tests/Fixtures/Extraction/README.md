# Extraction quality benchmark fixtures

## Purpose

Each subdirectory here is a paired `input.html` + `expected.md` fixture. The parameterised NUnit class [`ExtractionQualityBenchmarkTests`](../../Extraction/ExtractionQualityBenchmarkTests.cs) iterates every subdirectory, runs `DefaultMarkdownContentExtractor` against `input.html`, and asserts byte-equality with `expected.md`. Drift fails the test with a unified diff.

This is the regression suite that pins the extractor's output shape against realistic Clean.Core 7.0.5 markup. If the extractor's behaviour changes — deliberately or by mistake — these fixtures surface it.

## Catalogue

| Folder | Pins |
|---|---|
| [`clean-core-home/`](./clean-core-home/) | Clean.Core home-page synthetic mix — strip selectors (`script`/`style`/`svg`/`iframe`/`noscript`/`hidden`/`aria-hidden`/`data-llms-ignore`), GFM table conversion, `<code class="language-x">` fenced code, blockquote, image alt-drop, URL absolutification |
| [`clean-core-blog-list/`](./clean-core-blog-list/) | BlockList rich content — Clean.Core's blog-list card pattern with heading-inside-anchor lift, card images, time/datetime, author meta |
| [`clean-core-blockgrid-cards/`](./clean-core-blockgrid-cards/) | BlockGrid card layout — multi-column / multi-row grid items, `umb-block-grid__layout-item` wrappers, heading-level preservation, `<ul>` lists inside cards, "Learn more" links |
| [`clean-core-nested-tables-images/`](./clean-core-nested-tables-images/) | Long-form article — nested h1→h4, two GFM tables, two `<figure>`/`<figcaption>` image pairs, `<aside aria-hidden="true">` strip, `<div data-llms-ignore>` strip, inline `<code>` |

> **Naming convention:** scenario folders are `kebab-case`; fixtures sourced from Clean.Core 7.0.5 carry the `clean-core-` prefix to make the source explicit. The architecture document originally proposed bare names (`blocklist-rich-content` etc.); the implementation chose the prefixed convention for consistency with the existing `clean-core-home` fixture.

> **Synthetic vs captured:** the current input.html files are **realistic Clean.Core-shaped HTML**, not byte-for-byte captures from a running TestSite. Replacing them with real captures is tracked in `_bmad-output/implementation-artifacts/deferred-work.md` and is the natural follow-up when an `Umbraco.Cms.Tests.Integration`-based harness lands.

## Heading-only lift contract for card-shaped anchors

The default extractor lifts headings out of `<a>` ancestors (so `<a><h2>Title</h2>...</a>` becomes `## Title` followed by the link wrapping the rest of the card content). It does **not** also unwrap images and paragraphs from the remaining anchor — by design.

A common Clean.Core / Bootstrap / Tailwind pattern is the whole-card-as-link:

```html
<a href="/articles/intro">
  <img src="/intro.jpg" alt="..."/>
  <h2>Intro</h2>
  <p>Card body.</p>
</a>
```

After lift, this renders as a heading line followed by a multi-line link:

```markdown
## Intro
[![...](.../intro.jpg)
Card body.](/articles/intro)
```

This is **valid CommonMark** (line breaks inside link text are permitted) and preserves the card's actual semantic — title is document structure, the rest is a linked region.

**Why not unwrap further?** A blanket "unwrap image+paragraph from anchor" rule would also flatten linked figures, hero banners, and signpost CTAs where the wrapping link is intentional. The default extractor stays HTML-shape-agnostic; adopters who want a different rendering register an `IContentRegionSelector` (region-only override) or a full `IMarkdownContentExtractor` to strip card wrappers before extraction. Both seams ship with the package.

The `clean-core-blog-list` fixture pins the current shape so a future change to the lift behaviour surfaces as drift here.

## Adding a new fixture

1. Create `Fixtures/Extraction/<scenario-name>/`. Use `kebab-case` and the `clean-core-` prefix when the source is Clean.Core demo content.
2. Capture or hand-author `input.html`. To capture from a live TestSite:
   ```bash
   dotnet run --project LlmsTxt.Umbraco.TestSite/LlmsTxt.Umbraco.TestSite.csproj
   curl -k -s https://localhost:44314/<path> > input.html
   ```
   Trim browser/DevTools artefacts (`<x-claude-extension>` injections, devtools-only attributes). Keep the `<html>...</html>` envelope so block boundaries / `data-llms-content` / `aria-hidden` / `hidden` / `data-llms-ignore` behaviour is exercised by the fixture.
3. Generate the seed `expected.md`:
   - Add the scenario name to the `[TestCase(...)]` list on [`FixtureCaptureHelper.CaptureFixture`](../../Extraction/_FixtureCaptureHelper.cs).
   - Add per-scenario metadata (title and stable Guid) to [`FixtureMetadata`](../../Extraction/FixtureMetadata.cs).
   - Run the explicit capture:
     ```bash
     dotnet test LlmsTxt.Umbraco.slnx \
       --filter "TestCategory=ExtractionFixtureCapture"
     ```
4. **Hand-curate** `expected.md`. The seed is whatever the live extractor produces; commit the curated version. Where the seed pins a real extraction defect (e.g. a malformed link), either:
   - Fix the extractor first, recapture, then commit the curated output, OR
   - Pin the literal output and add a `deferred-work.md` entry explaining the gap.
5. Run the benchmark suite to confirm the new fixture is green:
   ```bash
   dotnet test LlmsTxt.Umbraco.slnx --filter Category=ExtractionQuality
   ```
6. Commit the new fixture + the `FixtureMetadata` / capture-helper updates in a single public-repo commit. Two-repo split rules apply (see [`CLAUDE.md`](../../../CLAUDE.md)).

## Updating an existing `expected.md`

After a deliberate extraction-rule change (new strip selector, conversion-rule tweak, etc.):

1. Confirm the rule change is intentional — if the change was accidental, fix the code, not the fixture.
2. Run the capture helper for the affected scenario(s):
   ```bash
   dotnet test LlmsTxt.Umbraco.slnx \
     --filter "TestCategory=ExtractionFixtureCapture"
   ```
3. **Hand-diff the new `expected.md` against the previous version.** Every line of drift must be a deliberate consequence of the rule change. Drift not predicted by the rule change means the fix has unintended scope — investigate before committing.
4. Commit the rule change AND the fixture refresh in the same commit. The fixture refresh without the rule change disguises a regression as a fixture update.

## What NOT to commit

- **Synthetic snippets** that don't exercise real Clean.Core content shapes — synthetic-but-realistic Clean.Core-shaped HTML is acceptable; abstract HTML snippets with no plausible Umbraco origin are not. Use unit tests on `DefaultMarkdownContentExtractor` for that.
- **Fixtures that pin a defect without a `deferred-work.md` entry** explaining why the fix was held. The benchmark catalogue is a quality contract; pinning known-bad output silently is worse than failing loudly.
- **`expected.md` files generated without hand-review.** The capture helper is a seed step, not a "click to commit" workflow. Always diff and curate.

## Trailing-newline / `eol=lf` discipline

`expected.md` is read with `.ReplaceLineEndings("\n")` and the live extractor emits `\n`-terminated bodies, so trailing-newline drift is the most common false-failure source on Windows checkouts.

If you hit "byte-different but they look identical" failures:

```bash
git check-attr eol -- LlmsTxt.Umbraco.Tests/Fixtures/Extraction/<scenario>/expected.md
```

Should report `eol: lf`. If not, add `*.md text eol=lf` to a `.gitattributes` for the fixtures folder. The benchmark's `ReplaceLineEndings("\n")` handles both inputs in memory, so this is preventative — drift here surfaces as a test failure, not silent corruption.
