namespace LlmsTxt.Umbraco.HealthChecks;

/// <summary>
/// Story 4.2 — AI-crawler taxonomy used by <see cref="DefaultRobotsAuditor"/>
/// to group findings in the Backoffice Health Check view. Surfaces the four
/// canonical buckets recognised by the wider GEO ecosystem
/// (Cloudflare Content-Signals, Anthropic / OpenAI / Google docs, Mintlify
/// guidance) plus an explicit <see cref="Unknown"/> bucket for tokens that
/// ship in <c>ai-robots-txt/ai.robots.txt</c> without a confident category
/// signal.
/// </summary>
public enum BotCategory
{
    /// <summary>
    /// Categorisation could not be determined from the curated map and the
    /// upstream feed didn't carry a metadata header for the token.
    /// Surfaces in the Health Check under "Unclassified bots" so the dev sees
    /// it and can patch the curated map in a follow-up PR.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Crawls content for use as model-training data
    /// (e.g. <c>GPTBot</c>, <c>ClaudeBot</c>, <c>cohere-training-data-crawler</c>,
    /// <c>Bytespider</c>). Blocking these typically reflects a "no AI training
    /// on my content" intent.
    /// </summary>
    Training = 1,

    /// <summary>
    /// Crawls to populate AI-mediated search / retrieval surfaces
    /// (e.g. <c>OAI-SearchBot</c>, <c>PerplexityBot</c>, <c>Claude-SearchBot</c>).
    /// Blocking these excludes the site from grounded-answer features in the
    /// corresponding LLM products — usually a different intent than blocking
    /// training crawlers.
    /// </summary>
    SearchRetrieval = 2,

    /// <summary>
    /// One-shot fetch initiated by an end-user query against an LLM
    /// (e.g. <c>ChatGPT-User</c>, <c>Claude-User</c>, <c>Perplexity-User</c>,
    /// <c>MistralAI-User</c>, <c>ChatGPT Agent</c>, <c>Operator</c>).
    /// Blocking these breaks user-driven "summarise this URL for me"
    /// interactions.
    /// </summary>
    UserTriggered = 3,

    /// <summary>
    /// Opt-out tokens that don't fetch directly but signal preferences to a
    /// crawler family (e.g. <c>Google-Extended</c> tells Google to exclude
    /// the site from Bard / Vertex AI training while still allowing
    /// <c>Googlebot</c>'s search index). Blocking these is the canonical
    /// "yes to search, no to AI training" knob.
    /// </summary>
    OptOut = 4,
}
