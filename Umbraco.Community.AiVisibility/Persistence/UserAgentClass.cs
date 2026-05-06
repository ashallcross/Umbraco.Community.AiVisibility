namespace Umbraco.Community.AiVisibility.Persistence;

/// <summary>
/// Story 5.1 — coarse classification of an HTTP <c>User-Agent</c> header,
/// emitted on every <see cref="Umbraco.Community.AiVisibility.Notifications.MarkdownPageRequestedNotification"/>,
/// <see cref="Umbraco.Community.AiVisibility.Notifications.LlmsTxtRequestedNotification"/>, and
/// <see cref="Umbraco.Community.AiVisibility.Notifications.LlmsFullTxtRequestedNotification"/>
/// and persisted as the <c>userAgentClass</c> column on the
/// <c>llmsTxtRequestLog</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Persisted as the enum member's <b>name</b> (string) — never as the integer
/// value. The integer ordering MUST stay stable once shipped (renumbering
/// would invalidate stored data via deserialiser drift); always append new
/// members at the end with a fresh integer.
/// </para>
/// <para>
/// AI-bucket categorisation (<see cref="AiTraining"/>, <see cref="AiSearchRetrieval"/>,
/// <see cref="AiUserTriggered"/>, <see cref="AiDeprecated"/>) is sourced from
/// Story 4.2's <c>AiBotList</c> Singleton (the embedded list ingested at
/// build time from <c>ai-robots-txt/ai.robots.txt</c>); the package does NOT
/// duplicate the AI-token list here. <see cref="HumanBrowser"/> and
/// <see cref="CrawlerOther"/> patterns live as a hand-curated substring set
/// in <see cref="DefaultUserAgentClassifier"/> (no upstream feed for
/// browsers).
/// </para>
/// </remarks>
public enum UserAgentClass
{
    /// <summary>
    /// UA was empty / null, or matched no curated pattern. Always the integer
    /// default (0) so an unset enum value surfaces as <see cref="Unknown"/>.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// AI training crawler — e.g. <c>GPTBot</c>, <c>ClaudeBot</c>,
    /// <c>cohere-training-data-crawler</c>, <c>Bytespider</c>. Sourced from
    /// <c>AiBotList</c> entries with <c>BotCategory.Training</c>. Opt-out
    /// tokens (<c>BotCategory.OptOut</c>, e.g. <c>Google-Extended</c>) also
    /// surface here — the canonical "no AI training on my content" intent.
    /// </summary>
    AiTraining = 1,

    /// <summary>
    /// AI search / retrieval crawler — e.g. <c>OAI-SearchBot</c>,
    /// <c>PerplexityBot</c>, <c>Claude-SearchBot</c>. Sourced from
    /// <c>AiBotList</c> entries with <c>BotCategory.SearchRetrieval</c>.
    /// </summary>
    AiSearchRetrieval = 2,

    /// <summary>
    /// One-shot fetch initiated by an end-user query against an LLM —
    /// e.g. <c>ChatGPT-User</c>, <c>Claude-User</c>, <c>Perplexity-User</c>.
    /// Sourced from <c>AiBotList</c> entries with <c>BotCategory.UserTriggered</c>.
    /// </summary>
    AiUserTriggered = 3,

    /// <summary>
    /// Deprecated AI crawler token — e.g. <c>anthropic-ai</c>, <c>Claude-Web</c>.
    /// Overrides the underlying <c>BotCategory</c> classification when an
    /// entry's <c>IsDeprecated == true</c>; the operator's modern replacement
    /// token is preferred.
    /// </summary>
    AiDeprecated = 4,

    /// <summary>
    /// Human browser — Mozilla / AppleWebKit / Chrome / Firefox / Safari /
    /// Edge UA shape.
    /// </summary>
    HumanBrowser = 5,

    /// <summary>
    /// Non-AI crawler — Googlebot / Bingbot / DuckDuckBot / Baiduspider etc.
    /// Sourced from a hand-curated substring set; not exhaustive.
    /// </summary>
    CrawlerOther = 6,
}
