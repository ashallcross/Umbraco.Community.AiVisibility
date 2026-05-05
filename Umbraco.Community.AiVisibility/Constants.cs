namespace LlmsTxt.Umbraco;

public static class Constants
{
    public const string ApiName = "llmstxtumbraco";

    public static class Routes
    {
        public const string MarkdownSuffix = ".md";
        public const string IndexHtmlMdSuffix = "/index.html.md";
        public const string MarkdownRouteName = "Llms.MarkdownRoute";
        public const string MarkdownRoutePattern = "{**path}";

        // Story 2.1 — /llms.txt manifest route. Mounted as a discrete endpoint
        // (NOT via the .md catch-all) so the route ordering is explicit and
        // unambiguous. Path is case-insensitive at the constraint level.
        public const string LlmsTxtPath = "/llms.txt";
        public const string LlmsTxtRouteName = "Llms.LlmsTxtRoute";
        public const string LlmsTxtRoutePattern = "llms.txt";

        // Story 2.2 — /llms-full.txt manifest route. Same registration shape as
        // /llms.txt: discrete endpoint, registered before the .md catch-all,
        // case-insensitive path matching.
        public const string LlmsFullPath = "/llms-full.txt";
        public const string LlmsFullRouteName = "Llms.LlmsFullTxtRoute";
        public const string LlmsFullRoutePattern = "llms-full.txt";
    }

    public static class HttpHeaders
    {
        public const string MarkdownContentType = "text/markdown; charset=utf-8";
        public const string XMarkdownTokens = "X-Markdown-Tokens";
        public const string CacheControl = "Cache-Control";
        public const string Vary = "Vary";
        public const string ETag = "ETag";
        public const string IfNoneMatch = "If-None-Match";

        // Story 4.1
        public const string Link = "Link";
        public const string ContentSignal = "Content-Signal";
    }

    public static class Cache
    {
        // Mirrors LlmsTxt.Umbraco.Caching.LlmsCacheKeys — duplicated here for grep-ability
        // alongside other package-prefixed names. The Caching helper is canonical for
        // composition; these constants are for cross-folder string literals.
        public const string Prefix = "llms:";
        public const string PagePrefix = "llms:page:";
        public const string LlmsTxtPrefix = "llms:llmstxt:";
        public const string LlmsFullPrefix = "llms:llmsfull:";

        // Story 3.1 — resolver settings cache namespace. Mirrored from
        // LlmsTxt.Umbraco.Caching.LlmsCacheKeys.SettingsPrefix for grep-ability.
        public const string SettingsPrefix = "llms:settings:";

        // Story 4.2 — robots audit cache namespace. Mirrored from
        // LlmsTxt.Umbraco.Caching.LlmsCacheKeys.RobotsPrefix for grep-ability.
        // The robots-audit cache lives under a different invalidation regime
        // than per-page / manifest caches — see LlmsCacheKeys.Robots for the
        // full rationale.
        public const string RobotsPrefix = "llms:robots:";
    }

    public static class HealthChecks
    {
        /// <summary>
        /// Story 4.2 — stable identifier for <c>RobotsAuditHealthCheck</c>'s
        /// <c>[HealthCheck(...)]</c> attribute. Umbraco persists Health Check
        /// IDs in adopter logs and Backoffice run records; <b>NEVER</b>
        /// regenerate this value. The accompanying
        /// <see cref="RobotsAuditGuidParseSentinel"/> guarantees a typo here
        /// fails at type-init rather than silently shipping a different ID.
        /// </summary>
        public const string RobotsAuditGuid = "1f8a2c4d-7b3e-4f9d-9c0a-2e6b5d8f1a42";

        /// <summary>
        /// Health Check group label shared between the
        /// <c>[HealthCheck(...)]</c> attribute and any documentation /
        /// notification filters that key on the group string.
        /// </summary>
        public const string GroupName = "LLMs";

        /// <summary>
        /// Type-init guard: parses <see cref="RobotsAuditGuid"/> at first
        /// touch of <see cref="HealthChecks"/>. A typo (e.g. swapped
        /// character) raises <c>FormatException</c> immediately rather than
        /// silently changing the persisted Health Check ID.
        /// </summary>
        private static readonly Guid RobotsAuditGuidParseSentinel = Guid.Parse(RobotsAuditGuid);
    }
}
