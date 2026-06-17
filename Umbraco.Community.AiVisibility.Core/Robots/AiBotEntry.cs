namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — one entry in the AI-crawler list ingested at build time from
/// <c>ai-robots-txt/ai.robots.txt</c> (or the committed fallback at
/// <c>HealthChecks/AiBotList.fallback.txt</c>). Carries the canonical
/// User-agent token (case-preserved as upstream emits it), the curated
/// category, the deprecated annotation, and an optional operator note that
/// surfaces in the Health Check Description.
/// </summary>
/// <remarks>
/// Token comparison at lookup time is case-insensitive
/// (<see cref="System.StringComparer.OrdinalIgnoreCase"/>) — robots.txt
/// User-agent matching is case-insensitive per RFC 9309.
/// </remarks>
/// <param name="Token">The User-agent token verbatim from the source feed.</param>
/// <param name="Category">Curated bucket — see <see cref="BotCategory"/>.</param>
/// <param name="IsDeprecated">When <c>true</c>, the Health Check annotates
/// the finding with a "deprecated — see {modern token}" note. Sourced from
/// <see cref="AiBotList.DeprecatedTokens"/>.</param>
/// <param name="Operator">Optional operator name (e.g. "OpenAI", "Anthropic") —
/// nullable because not every entry has one. Used in the Health Check
/// Description.</param>
/// <param name="DeprecationReplacement">When <see cref="IsDeprecated"/> is
/// <c>true</c>, the modern token to recommend (e.g. <c>anthropic-ai</c> →
/// <c>ClaudeBot</c>). <c>null</c> when no replacement is applicable.</param>
/// <param name="Notes">Free-form note surfaced in the Health Check Description
/// for entries that need a per-token caveat (e.g. "documented to ignore
/// robots.txt"). <c>null</c> for the common case.</param>
public sealed record AiBotEntry(
    string Token,
    BotCategory Category,
    bool IsDeprecated,
    string? Operator,
    string? DeprecationReplacement,
    string? Notes = null);
