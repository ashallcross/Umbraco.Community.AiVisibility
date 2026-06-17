namespace Umbraco.Community.AiVisibility.Persistence;

/// <summary>
/// Story 5.1 — extension point that classifies an HTTP <c>User-Agent</c>
/// header into one of the <see cref="UserAgentClass"/> buckets surfaced on
/// the package's three notifications and the <c>aiVisibilityRequestLog</c>
/// <c>userAgentClass</c> column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime: Singleton.</b> The default implementation
/// (<see cref="DefaultUserAgentClassifier"/>) projects Story 4.2's
/// <c>AiBotList</c> entries into a length-sorted token list at first
/// construction; classification then runs a linear substring scan
/// (longest-token-first across AI tokens, then curated crawlers, then
/// curated browser tells) — complexity is roughly O((N_ai + N_crawler +
/// N_browser) × L_ua), bounded by the curated map sizes. Adopters
/// override via <c>services.AddSingleton&lt;IUserAgentClassifier, MyImpl&gt;()</c>.
/// </para>
/// <para>
/// <b>Sync method.</b> Classification is pure string matching — no I/O, no
/// awaitable work. Async overhead is unjustified.
/// </para>
/// </remarks>
public interface IUserAgentClassifier
{
    /// <summary>
    /// Classify the supplied User-Agent string. Empty / null / whitespace
    /// returns <see cref="UserAgentClass.Unknown"/>.
    /// </summary>
    UserAgentClass Classify(string? userAgent);
}
