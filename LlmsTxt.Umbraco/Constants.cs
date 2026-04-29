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
    }
}
