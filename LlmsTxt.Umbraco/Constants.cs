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
    }
}
