namespace Umbraco.Community.AiVisibility.Robots;

/// <summary>
/// Story 4.2 — one matched-and-blocked AI crawler in a
/// <see cref="RobotsAuditResult"/>. Carries enough context for the
/// Backoffice Health Check view to render a copy-pasteable suggested
/// removal block.
/// </summary>
/// <param name="Bot">The matched <see cref="AiBotEntry"/> from
/// <see cref="AiBotList"/>.</param>
/// <param name="MatchedDirective">The literal robots.txt block (User-agent +
/// Disallow lines) that triggered the finding, formatted as the adopter
/// would see it in their <c>robots.txt</c>. Used in the "you currently
/// have this block" half of the Backoffice copy-paste UX.</param>
/// <param name="SuggestedRemoval">A short, copy-pasteable suggestion the
/// adopter can apply to remove or scope the block. Empty when removal is
/// the only suggestion (the finding's <see cref="MatchedDirective"/> is
/// what's already in the file; the suggestion is "remove these lines").</param>
/// <param name="IsDeprecated">Mirrors <c>Bot.IsDeprecated</c> at the finding
/// level — the spec contract (Task 3.2) puts the flag on the finding so
/// renderers can read it without dotting through <see cref="Bot"/>. Always
/// equal to <c>Bot.IsDeprecated</c>; the duplication is intentional.</param>
public sealed record RobotsAuditFinding(
    AiBotEntry Bot,
    string MatchedDirective,
    string SuggestedRemoval,
    bool IsDeprecated);
