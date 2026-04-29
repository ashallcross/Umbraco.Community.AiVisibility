---
title: Welcome to Clean.Core
url: https://example.test/home
updated: 2026-04-29T12:00:00Z
---
# Welcome home

This is the Clean.Core demo content for the LlmsTxt.Umbraco package. The extractor's job is to render this page and convert it to clean Markdown.

## What this package does

It exposes Umbraco published content to AI crawlers via three surfaces:

- [A per-page Markdown route](https://example.test/blog) at `/{path}.md`
- A `/llms.txt` manifest
- A `/llms-full.txt` bulk export

## Hero image

![Clean.Core hero illustration](https://example.test/media/hero.jpg)

A decorative image with empty alt is dropped from the output entirely.

## A table

| Surface | URL |
| --- | --- |
| Per-page | [/page.md](https://example.test/page.md) |
| Manifest | [/llms.txt](https://example.test/llms.txt) |
| Bulk | [/llms-full.txt](https://example.test/llms-full.txt) |

## A code block

```csharp
services.TryAddTransient<IMarkdownContentExtractor, DefaultMarkdownContentExtractor>();
```

## A blockquote

>
> Render through Umbraco's normal pipeline, never walk properties.
