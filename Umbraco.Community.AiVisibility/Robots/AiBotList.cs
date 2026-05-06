using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — Singleton holder for the AI-crawler list. The list is embedded
/// into the assembly at build time by the <c>SyncAiBotList</c> MSBuild target
/// (see <c>LlmsTxt.Umbraco.csproj</c>) under the resource name
/// <c>Umbraco.Community.AiVisibility.Robots.AiBotList.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lookup</b> is case-insensitive (<see cref="StringComparer.OrdinalIgnoreCase"/>)
/// per RFC 9309 robots.txt User-agent matching semantics.
/// </para>
/// <para>
/// <b>Categorisation</b> is hand-curated. Upstream
/// <c>ai-robots-txt/ai.robots.txt</c> ships a flat list of <c>User-agent:</c>
/// declarations followed by a single <c>Disallow: /</c>; it carries no
/// metadata comments mapping each token to its operator / function. The
/// curated map in <see cref="BuildCuratedMap"/> is the source of truth; new
/// entries that arrive in upstream without a curated mapping surface as
/// <see cref="BotCategory.Unknown"/> and the dev should patch the map.
/// </para>
/// <para>
/// <b>Deprecation flagging</b> is also hand-curated (<see cref="DeprecatedTokens"/>).
/// Currently flagged: <c>anthropic-ai</c> → <c>ClaudeBot</c>;
/// <c>Claude-Web</c> → <c>ClaudeBot</c>.
/// </para>
/// <para>
/// <b>Failure modes:</b> if the embedded resource is missing (build
/// misconfiguration), <see cref="Load"/> falls back to a hardcoded minimum
/// set + logs a <c>Warning</c>. The audit still functions; it just covers
/// fewer crawlers.
/// </para>
/// </remarks>
public sealed class AiBotList
{
    /// <summary>
    /// Resource name as embedded by <c>SyncAiBotList</c> MSBuild target's
    /// <c>LogicalName</c>. MUST match — changing this breaks the loader.
    /// </summary>
    internal const string EmbeddedResourceName = "Umbraco.Community.AiVisibility.Robots.AiBotList.txt";

    /// <summary>
    /// Tokens flagged as deprecated. Lookup is case-insensitive.
    /// Adding a new entry here instructs the Health Check to surface a
    /// "(deprecated — see {modern})" annotation on findings matching the
    /// token.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DeprecatedTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-ai"] = "ClaudeBot",
            ["Claude-Web"] = "ClaudeBot",
        };

    /// <summary>
    /// Hardcoded fallback set. Used only when the embedded resource is
    /// missing (build misconfiguration). Covers the most operationally
    /// significant tokens so a misconfigured build is still useful.
    /// </summary>
    private static readonly string[] FallbackTokens = new[]
    {
        "GPTBot", "OAI-SearchBot", "ChatGPT-User", "ChatGPT Agent",
        "ClaudeBot", "Claude-User", "Claude-SearchBot", "anthropic-ai", "Claude-Web",
        "PerplexityBot", "Perplexity-User",
        "Google-Extended",
        "cohere-ai", "cohere-training-data-crawler",
        "Bytespider",
        "Applebot-Extended",
        "FacebookBot", "Meta-ExternalAgent", "meta-externalagent",
        "MistralAI-User",
        "DeepSeekBot",
    };

    private readonly Dictionary<string, AiBotEntry> _byToken;

    /// <summary>
    /// All entries the loader produced, ordered as they appeared in the
    /// embedded resource (or the hardcoded fallback set when the resource
    /// was missing).
    /// </summary>
    public IReadOnlyList<AiBotEntry> Entries { get; }

    /// <summary>
    /// <c>true</c> when <see cref="Load"/> fell back to
    /// <see cref="FallbackTokens"/> because the embedded resource was
    /// missing or empty. Surfaces in the Health Check Description as a
    /// caveat ("AI bot list ships with N tokens — embedded resource was
    /// not found; using a minimal hardcoded set. Build configuration may
    /// be broken.").
    /// </summary>
    public bool UsedHardcodedFallback { get; }

    /// <summary>
    /// Public constructor used by DI (auto-discovered via
    /// <c>RobotsComposer.TryAddSingleton</c>). Loads the embedded
    /// resource via <see cref="Assembly.GetManifestResourceStream"/>.
    /// </summary>
    public AiBotList(ILogger<AiBotList> logger)
        : this(LoadEmbeddedTokens(logger, out var usedFallback), usedFallback)
    {
    }

    /// <summary>
    /// Per-token Notes carried on <see cref="AiBotEntry.Notes"/>. Surfaces
    /// in the Health Check view as a per-finding caveat. Used today for
    /// crawlers documented to ignore robots.txt; extends naturally as new
    /// non-compliance signals land.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CuratedNotes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bytespider"] = "Documented to ignore robots.txt; blocking in robots.txt may not be effective. See the ai-robots-txt project notes.",
            ["GrokBot"] = "Documented to ignore robots.txt; blocking in robots.txt may not be effective. See https://platform.x.ai/docs/agentic-services.",
            ["Grok"] = "Documented to ignore robots.txt; blocking in robots.txt may not be effective. See https://platform.x.ai/docs/agentic-services.",
        };

    private AiBotList(IReadOnlyList<string> tokens, bool usedFallback)
    {
        var curated = BuildCuratedMap();
        var entries = new List<AiBotEntry>(tokens.Count);
        var byToken = new Dictionary<string, AiBotEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var trimmed = token.Trim();

            // Dedupe — the upstream feed sometimes ships casing variants
            // (e.g. "Meta-ExternalAgent" + "meta-externalagent"). The robots.txt
            // matching is case-insensitive so the variants collapse on lookup.
            // Keep the first occurrence; later duplicates are skipped.
            if (byToken.ContainsKey(trimmed))
            {
                continue;
            }

            curated.TryGetValue(trimmed, out var meta);
            var isDeprecated = DeprecatedTokens.ContainsKey(trimmed);
            DeprecatedTokens.TryGetValue(trimmed, out var replacement);
            CuratedNotes.TryGetValue(trimmed, out var notes);

            var entry = new AiBotEntry(
                Token: trimmed,
                Category: meta?.Category ?? BotCategory.Unknown,
                IsDeprecated: isDeprecated,
                Operator: meta?.Operator,
                DeprecationReplacement: replacement,
                Notes: notes);

            entries.Add(entry);
            byToken[trimmed] = entry;
        }

        Entries = entries;
        _byToken = byToken;
        UsedHardcodedFallback = usedFallback;
    }

    /// <summary>
    /// Test-only constructor — bypasses the embedded-resource load and
    /// accepts an explicit token list. Used by
    /// <c>AiBotListTests</c> to pin parsing / categorisation behaviour
    /// without depending on the build-time embedded resource.
    /// </summary>
    internal static AiBotList ForTesting(IReadOnlyList<string> tokens, bool usedFallback = false)
        => new(tokens, usedFallback);

    /// <summary>
    /// Case-insensitive lookup. Returns <c>null</c> when the token is not
    /// in the loaded list.
    /// </summary>
    public AiBotEntry? GetByToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        _byToken.TryGetValue(token.Trim(), out var entry);
        return entry;
    }

    /// <summary>
    /// Case-insensitive containment check. Cheap O(1) lookup.
    /// </summary>
    public bool Contains(string token) => GetByToken(token) is not null;

    private static IReadOnlyList<string> LoadEmbeddedTokens(ILogger logger, out bool usedFallback)
    {
        var assembly = typeof(AiBotList).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);

        if (stream is null)
        {
            logger.LogWarning(
                "Embedded AI bot list resource '{ResourceName}' not found in assembly. " +
                "Falling back to a minimal hardcoded set ({FallbackCount} tokens). " +
                "Build configuration may be broken — check the SyncAiBotList MSBuild target output.",
                EmbeddedResourceName,
                FallbackTokens.Length);
            usedFallback = true;
            return FallbackTokens;
        }

        // detectEncodingFromByteOrderMarks (default true) handles a leading
        // UTF-8 BOM via stream sniffing. Pass an explicit Encoding (UTF-8) so
        // a maintainer who edits the fallback file in an editor that adds a
        // BOM still parses cleanly even if the StreamReader heuristics ever
        // change.
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var tokens = ParseTokens(reader.ReadToEnd());

        if (tokens.Count == 0)
        {
            logger.LogWarning(
                "Embedded AI bot list resource '{ResourceName}' produced zero tokens. " +
                "Falling back to a minimal hardcoded set ({FallbackCount} tokens).",
                EmbeddedResourceName,
                FallbackTokens.Length);
            usedFallback = true;
            return FallbackTokens;
        }

        usedFallback = false;
        return tokens;
    }

    /// <summary>
    /// Tolerant line-by-line parser for the upstream <c>ai.robots.txt</c>
    /// format. Recognises:
    /// <list type="bullet">
    /// <item>Blank lines — skipped.</item>
    /// <item>Comment lines (starting <c>#</c>) — skipped (and reserved for
    /// future metadata extraction).</item>
    /// <item><c>User-agent: &lt;token&gt;</c> — the token is harvested.</item>
    /// <item>Other directives (<c>Disallow:</c>, <c>Allow:</c>, etc.) —
    /// skipped without warning (they're intentional in the upstream feed
    /// to satisfy robots.txt grammar but carry no token info).</item>
    /// </list>
    /// Tolerant of CR/LF line endings.
    /// </summary>
    internal static List<string> ParseTokens(string content)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return tokens;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            const string userAgentPrefix = "User-agent:";
            if (line.StartsWith(userAgentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = line.Substring(userAgentPrefix.Length).Trim();
                if (token.Length > 0)
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens;
    }

    /// <summary>
    /// Hand-curated map: User-agent token → (category, operator). Source for
    /// most entries is the operator's own documentation
    /// (Anthropic / OpenAI / Google / Cloudflare / Mintlify / robots.txt
    /// guidance pages). Updated when new tokens land in upstream that
    /// shouldn't surface as <see cref="BotCategory.Unknown"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, BotMeta> BuildCuratedMap()
    {
        var map = new Dictionary<string, BotMeta>(StringComparer.OrdinalIgnoreCase);

        void Add(BotCategory category, string @operator, params string[] tokens)
        {
            foreach (var token in tokens)
            {
                // Throw on duplicate registration so a maintenance PR that
                // accidentally lists the same token under two operators or two
                // categories surfaces at static-init time, not silently wins
                // last-Add.
                if (!map.TryAdd(token, new BotMeta(category, @operator)))
                {
                    throw new InvalidOperationException(
                        $"AiBotList.BuildCuratedMap: token '{token}' is registered twice. " +
                        $"Existing: {map[token].Operator}/{map[token].Category}; " +
                        $"new: {@operator}/{category}. Resolve in the curated map source.");
                }
            }
        }

        // OpenAI
        // Note: "OpenAI" appears as a literal User-agent token in upstream
        // (ai-robots-txt/ai.robots.txt). Categorised UserTriggered alongside
        // ChatGPT-User / ChatGPT Agent / Operator for surface clarity.
        Add(BotCategory.Training, "OpenAI", "GPTBot");
        Add(BotCategory.SearchRetrieval, "OpenAI", "OAI-SearchBot");
        Add(BotCategory.UserTriggered, "OpenAI", "ChatGPT-User", "ChatGPT Agent", "Operator", "OpenAI");

        // Anthropic
        Add(BotCategory.Training, "Anthropic", "ClaudeBot", "anthropic-ai", "Claude-Web");
        Add(BotCategory.SearchRetrieval, "Anthropic", "Claude-SearchBot");
        Add(BotCategory.UserTriggered, "Anthropic", "Claude-User");

        // Google
        Add(BotCategory.OptOut, "Google", "Google-Extended");
        Add(BotCategory.SearchRetrieval, "Google", "Google-Agent", "Google-CloudVertexBot",
            "GoogleAgent-Mariner", "GoogleOther", "GoogleOther-Image", "GoogleOther-Video",
            "Google-NotebookLM", "NotebookLM", "Gemini-Deep-Research");
        Add(BotCategory.Training, "Google", "Google-Firebase", "CloudVertexBot");

        // Perplexity
        Add(BotCategory.SearchRetrieval, "Perplexity", "PerplexityBot");
        Add(BotCategory.UserTriggered, "Perplexity", "Perplexity-User");

        // Microsoft / Azure
        Add(BotCategory.SearchRetrieval, "Microsoft", "AzureAI-SearchBot");

        // Apple
        Add(BotCategory.OptOut, "Apple", "Applebot-Extended");
        Add(BotCategory.SearchRetrieval, "Apple", "Applebot");

        // Cohere
        Add(BotCategory.Training, "Cohere", "cohere-ai", "cohere-training-data-crawler");

        // Meta
        // Curated map keys are compared case-insensitively (OrdinalIgnoreCase),
        // so only one casing per logical token belongs here. Upstream ships
        // some tokens in both PascalCase and lowercase forms (e.g.
        // Meta-ExternalAgent + meta-externalagent); the parser harvests both
        // strings, the constructor dedupes case-insensitively, lookup honours
        // either casing.
        Add(BotCategory.Training, "Meta", "FacebookBot", "facebookexternalhit",
            "Meta-ExternalAgent", "Meta-ExternalFetcher", "meta-webindexer");

        // Mistral
        // Note: upstream ships BOTH "MistralAI-User" and "MistralAI-User/1.0"
        // as separate User-agent lines (because robots.txt parsers vary in
        // version-strip behaviour). Both forms are mapped here so a curated
        // category is reported regardless of which form an adopter blocks.
        Add(BotCategory.UserTriggered, "Mistral", "MistralAI-User", "MistralAI-User/1.0");

        // ByteDance / TikTok
        Add(BotCategory.Training, "ByteDance", "Bytespider", "TikTokSpider");

        // Common Crawl
        Add(BotCategory.Training, "Common Crawl", "CCBot");

        // Apify (general-purpose AI scraping platforms — categorised as Training
        // because the operational use is corpus building rather than retrieval)
        Add(BotCategory.Training, "Apify", "ApifyBot", "ApifyWebsiteContentCrawler");

        // Diffbot
        Add(BotCategory.Training, "Diffbot", "Diffbot");

        // Amazon
        Add(BotCategory.SearchRetrieval, "Amazon", "Amazonbot", "amazon-kendra", "Amzn-SearchBot",
            "Amzn-User", "AmazonBuyForMe", "bedrockbot");

        // AI2
        Add(BotCategory.Training, "Allen Institute for AI", "AI2Bot",
            "AI2Bot-DeepResearchEval", "Ai2Bot-Dolma");

        // DeepSeek
        Add(BotCategory.Training, "DeepSeek", "DeepSeekBot");

        // ChatGLM
        Add(BotCategory.Training, "Zhipu AI", "ChatGLM-Spider");

        // Yandex
        Add(BotCategory.Training, "Yandex", "YandexAdditional", "YandexAdditionalBot");

        // YouBot / You.com
        Add(BotCategory.SearchRetrieval, "You.com", "YouBot");

        // Phind
        Add(BotCategory.SearchRetrieval, "Phind", "PhindBot");

        // Brave
        Add(BotCategory.SearchRetrieval, "Brave", "Bravebot");

        // PetalBot (Huawei)
        Add(BotCategory.Training, "Huawei", "PetalBot", "PanguBot");

        // Tavily
        Add(BotCategory.SearchRetrieval, "Tavily", "TavilyBot");

        // Linkup / Liner / Kagi
        Add(BotCategory.SearchRetrieval, "Linkup", "LinkupBot");
        Add(BotCategory.SearchRetrieval, "Liner", "LinerBot");
        Add(BotCategory.SearchRetrieval, "Kagi", "kagi-fetcher");

        // Devin / Manus / Andibot / iAsk / TwinAgent / Operator-class agents
        Add(BotCategory.UserTriggered, "Cognition Labs", "Devin");
        Add(BotCategory.UserTriggered, "Manus", "Manus-User");
        Add(BotCategory.UserTriggered, "Andi", "Andibot");
        Add(BotCategory.UserTriggered, "iAsk", "iAskBot", "iaskspider", "iaskspider/2.0");
        Add(BotCategory.UserTriggered, "TwinAgent", "TwinAgent");
        Add(BotCategory.UserTriggered, "Adept", "NovaAct");

        // ImagesiftBot (Hive)
        Add(BotCategory.Training, "Hive", "ImagesiftBot", "imageSpider");

        // LAION (image dataset)
        Add(BotCategory.Training, "LAION", "LAIONDownloader",
            "laion-huggingface-processor", "img2dataset");

        // Cloudflare AutoRAG
        Add(BotCategory.SearchRetrieval, "Cloudflare", "Cloudflare-AutoRAG");

        // Misc training crawlers / archivers
        Add(BotCategory.Training, "OmgIli", "omgili", "omgilibot");
        Add(BotCategory.Training, "Webz.io", "Webzio-Extended");
        Add(BotCategory.Training, "Sidetrade", "Sidetrade indexer bot");
        Add(BotCategory.Training, "Timpi", "Timpibot");
        Add(BotCategory.Training, "WRTN", "WRTNBot");
        Add(BotCategory.Training, "Klaviyo", "KlaviyoAIBot");
        Add(BotCategory.Training, "QuillBot", "QuillBot", "quillbot.com");
        Add(BotCategory.Training, "Velen", "VelenPublicWebCrawler");
        Add(BotCategory.Training, "Friendly", "FriendlyCrawler");
        Add(BotCategory.Training, "Awario", "Awario");
        Add(BotCategory.Training, "Echobox", "EchoboxBot", "Echobot Bot");
        Add(BotCategory.Training, "Crawlspace", "Crawlspace");
        Add(BotCategory.Training, "Crawl4AI", "Crawl4AI");
        Add(BotCategory.Training, "Firecrawl", "FirecrawlAgent");
        Add(BotCategory.Training, "Scrapy", "Scrapy");
        Add(BotCategory.Training, "ExaBot", "ExaBot");
        Add(BotCategory.Training, "Channel3", "Channel3Bot");
        Add(BotCategory.Training, "Linguee", "Linguee Bot");
        Add(BotCategory.Training, "Cotoyogi", "Cotoyogi");
        Add(BotCategory.Training, "Kangaroo", "Kangaroo Bot");
        Add(BotCategory.Training, "Anomura", "Anomura");
        Add(BotCategory.Training, "Brightbot", "Brightbot 1.0");
        Add(BotCategory.Training, "Buddy", "BuddyBot");
        Add(BotCategory.Training, "Datenbank", "Datenbank Crawler");
        Add(BotCategory.Training, "DuckDuckGo", "DuckAssistBot");
        Add(BotCategory.Training, "Factset", "Factset_spyderbot");
        Add(BotCategory.Training, "ICC", "ICC-Crawler");
        Add(BotCategory.Training, "ISS", "ISSCyberRiskCrawler");
        Add(BotCategory.Training, "IbouBot", "IbouBot");
        Add(BotCategory.Training, "Kunato", "KunatoCrawler");
        Add(BotCategory.Training, "LCC", "LCC");
        Add(BotCategory.Training, "MyCentralAI", "MyCentralAIScraperBot");
        Add(BotCategory.Training, "Panscient", "Panscient", "panscient.com");
        Add(BotCategory.Training, "Poseidon", "Poseidon Research Crawler");
        Add(BotCategory.Training, "Poggio", "Poggio-Citations");
        Add(BotCategory.Training, "Qualified", "QualifiedBot");
        Add(BotCategory.Training, "SBI", "SBIntuitionsBot");
        Add(BotCategory.Training, "Semrush", "SemrushBot-OCOB", "SemrushBot-SWA");
        Add(BotCategory.Training, "Shap", "ShapBot");
        Add(BotCategory.Training, "Spider", "Spider");
        Add(BotCategory.Training, "TerraCotta", "TerraCotta");
        Add(BotCategory.Training, "Thinkbot", "Thinkbot");
        Add(BotCategory.Training, "WARDBot", "WARDBot");
        Add(BotCategory.Training, "YaK", "YaK");
        Add(BotCategory.Training, "Zanista", "ZanistaBot");
        Add(BotCategory.Training, "wpbot", "wpbot");
        Add(BotCategory.Training, "Aranet", "Aranet-SearchBot");
        Add(BotCategory.Training, "Atlassian", "atlassian-bot");
        Add(BotCategory.Training, "AddSearch", "AddSearchBot");
        Add(BotCategory.Training, "aiHit", "aiHitBot");
        Add(BotCategory.Training, "BigSur", "bigsur.ai");
        Add(BotCategory.Training, "netEstate", "netEstate Imprint Crawler");

        return map;
    }

    /// <summary>
    /// Internal record carrying the curated metadata for a single bot
    /// before it's converted to an <see cref="AiBotEntry"/> at load time.
    /// </summary>
    private sealed record BotMeta(BotCategory Category, string Operator);
}
