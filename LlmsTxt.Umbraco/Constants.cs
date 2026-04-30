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
    }

    public static class HttpHeaders
    {
        public const string MarkdownContentType = "text/markdown; charset=utf-8";
        public const string XMarkdownTokens = "X-Markdown-Tokens";
        public const string CacheControl = "Cache-Control";
        public const string Vary = "Vary";
        public const string ETag = "ETag";
        public const string IfNoneMatch = "If-None-Match";
    }

    public static class Cache
    {
        // Mirrors LlmsTxt.Umbraco.Caching.LlmsCacheKeys — duplicated here for grep-ability
        // alongside other package-prefixed names. The Caching helper is canonical for
        // composition; these constants are for cross-folder string literals.
        public const string Prefix = "llms:";
        public const string PagePrefix = "llms:page:";
        public const string LlmsTxtPrefix = "llms:llmstxt:";
    }
}
