using LlmsTxt.Umbraco.HealthChecks;

namespace Umbraco.Community.AiVisibility.Persistence;

/// <summary>
/// Story 5.1 — default <see cref="IUserAgentClassifier"/> implementation.
/// Projects Story 4.2's <see cref="AiBotList"/> entries to a token →
/// <see cref="UserAgentClass"/> dictionary at first construction; lookups
/// are O(N) substring scans (longest-token-first) followed by O(1)
/// dictionary check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Match priority</b> (longest-substring-first to avoid <c>GPTBot</c>
/// matching inside <c>chatgpt-user</c>):
/// </para>
/// <list type="number">
/// <item>AI tokens from <see cref="AiBotList"/> — sorted by token length
/// descending. <c>IsDeprecated == true</c> overrides
/// <see cref="UserAgentClass.AiDeprecated"/> regardless of underlying
/// category.</item>
/// <item>Curated non-AI crawler substrings (<c>Googlebot</c>,
/// <c>bingbot</c>, etc.) → <see cref="UserAgentClass.CrawlerOther"/>.</item>
/// <item>Browser substrings (<c>Mozilla</c>, <c>AppleWebKit</c>) →
/// <see cref="UserAgentClass.HumanBrowser"/>.</item>
/// <item>Otherwise → <see cref="UserAgentClass.Unknown"/>.</item>
/// </list>
/// <para>
/// Substring matching is case-insensitive
/// (<see cref="StringComparison.OrdinalIgnoreCase"/>).
/// </para>
/// <para>
/// <b>Singleton lifetime, stateless after construction.</b> The token
/// projections are computed once in the constructor and held in
/// <c>readonly</c> fields; thread-safe by immutability.
/// </para>
/// </remarks>
public sealed class DefaultUserAgentClassifier : IUserAgentClassifier
{
    /// <summary>
    /// Curated browser-tell substrings. Order is insignificant — match
    /// is "any contains" → <see cref="UserAgentClass.HumanBrowser"/>.
    /// Kept short on purpose: real browsers all carry one of these.
    /// </summary>
    private static readonly string[] BrowserSubstrings = new[]
    {
        "Mozilla",
        "AppleWebKit",
        "Chrome",
        "Firefox",
        "Safari",
        "Edge/",
        "Edg/",
        "Opera",
        "OPR/",
    };

    /// <summary>
    /// Curated non-AI crawler substrings. Match → <see cref="UserAgentClass.CrawlerOther"/>.
    /// Not exhaustive — adopters needing finer detail override
    /// <see cref="IUserAgentClassifier"/> entirely.
    /// </summary>
    private static readonly string[] CrawlerSubstrings = new[]
    {
        "Googlebot",
        "bingbot",
        "DuckDuckBot",
        "Baiduspider",
        "YandexBot",
        "Slurp",
        "Sogou",
        "facebookexternalhit",
        "ia_archiver",
        "Exabot",
        "AhrefsBot",
        "SemrushBot",
        "MJ12bot",
        "DotBot",
        "PetalBot",
        "Applebot",
    };

    private readonly IReadOnlyList<TokenProjection> _aiTokensByLengthDesc;

    public DefaultUserAgentClassifier(AiBotList aiBotList)
    {
        if (aiBotList is null)
        {
            throw new ArgumentNullException(nameof(aiBotList));
        }

        // Project AiBotList entries to a (Token, UserAgentClass) tuple list,
        // sorted by Token length descending. Longest-token-first matching
        // prevents shorter substrings (e.g. "GPTBot") from masking longer
        // qualifying matches that contain them ("ChatGPT Agent" contains
        // "Chat" but the longer "ChatGPT" entry must win).
        //
        // Entries whose curated category is BotCategory.Unknown are SKIPPED
        // from the AI loop per Task 1.3 — they fall through to the curated
        // crawler/browser substring lists. This is the only way upstream
        // tokens that AiBotList ingests but our curated map hasn't classed
        // can land in CrawlerOther/HumanBrowser/Unknown rather than being
        // bucketed as a default AI category.
        _aiTokensByLengthDesc = aiBotList.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Token))
            .Select(e => (Entry: e, Class: MapBotCategory(e)))
            .Where(p => p.Class is not null)
            .Select(p => new TokenProjection(p.Entry.Token, p.Class!.Value))
            .OrderByDescending(p => p.Token.Length)
            .ToList();
    }

    public UserAgentClass Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return UserAgentClass.Unknown;
        }

        // 1. AI tokens (longest first).
        foreach (var projection in _aiTokensByLengthDesc)
        {
            if (userAgent.Contains(projection.Token, StringComparison.OrdinalIgnoreCase))
            {
                return projection.Class;
            }
        }

        // 2. Curated non-AI crawlers.
        foreach (var crawler in CrawlerSubstrings)
        {
            if (userAgent.Contains(crawler, StringComparison.OrdinalIgnoreCase))
            {
                return UserAgentClass.CrawlerOther;
            }
        }

        // 3. Browser tells.
        foreach (var browser in BrowserSubstrings)
        {
            if (userAgent.Contains(browser, StringComparison.OrdinalIgnoreCase))
            {
                return UserAgentClass.HumanBrowser;
            }
        }

        return UserAgentClass.Unknown;
    }

    /// <summary>
    /// Map Story 4.2's <see cref="BotCategory"/> + deprecation flag to a
    /// Story 5.1 <see cref="UserAgentClass"/>. Returns <c>null</c> for
    /// <see cref="BotCategory.Unknown"/> so the caller skips that entry
    /// from the AI loop and falls through to the curated browser/crawler
    /// substring lists per Task 1.3 contract. Deprecated overrides category.
    /// </summary>
    private static UserAgentClass? MapBotCategory(AiBotEntry entry)
    {
        if (entry.IsDeprecated)
        {
            return UserAgentClass.AiDeprecated;
        }

        return entry.Category switch
        {
            BotCategory.Training => UserAgentClass.AiTraining,
            BotCategory.SearchRetrieval => UserAgentClass.AiSearchRetrieval,
            BotCategory.UserTriggered => UserAgentClass.AiUserTriggered,
            BotCategory.OptOut => UserAgentClass.AiTraining,
            // Unknown category → null → skip from AI loop, fall through to
            // curated crawler/browser lists. Per Task 1.3 + Task 1.4 pin.
            _ => null,
        };
    }

    private readonly record struct TokenProjection(string Token, UserAgentClass Class);
}
