using System.Net;

namespace Umbraco.Community.AiVisibility.Routing;

/// <summary>
/// Strips the <c>.md</c> or <c>/index.html.md</c> suffix from a captured route path
/// and URL-decodes the result, producing the canonical content path Umbraco resolves.
///
/// Per Story 1.1 AC8 / Task 8 decision: <c>/foo.md</c>, <c>/foo/.md</c>, and
/// <c>/foo/index.html.md</c> all collapse to the same canonical path
/// (<c>/foo</c> or <c>/foo/</c> depending on the trailing-slash form). No 301 — adopters
/// pointing to any of the three accepted suffixes get the same Markdown body.
/// </summary>
internal static class MarkdownPathNormaliser
{
    /// <summary>
    /// Normalises a captured <c>{**path:nonfile}</c> route value to a leading-slash,
    /// suffix-stripped, URL-decoded canonical path. Throws <see cref="ArgumentException"/>
    /// when the input is missing/empty, contains a control character, attempts path
    /// traversal (<c>..</c>, backslash, encoded variants), or lacks a recognised
    /// <c>.md</c> suffix — the route constraint should have prevented those cases
    /// from reaching this helper.
    /// </summary>
    public static string NormaliseToCanonical(string capturedPath)
    {
        if (string.IsNullOrEmpty(capturedPath))
        {
            throw new ArgumentException("Captured path must not be empty.", nameof(capturedPath));
        }

        // Step 0a: reject control chars and NUL bytes at the wire level.
        foreach (var c in capturedPath)
        {
            if (c < 0x20 || c == 0x7F)
            {
                throw new ArgumentException(
                    "Captured path contains a control character.",
                    nameof(capturedPath));
            }
        }

        // Step 0b: reject backslash and double-dot before decoding so encoded variants
        // (`%2E%2E`, `%5C`) can't smuggle traversal through.
        if (ContainsTraversal(capturedPath))
        {
            throw new ArgumentException(
                $"Path '{capturedPath}' contains traversal segments.",
                nameof(capturedPath));
        }

        // Step 1: ensure leading slash (route values may strip it depending on pattern)
        var withSlash = capturedPath.StartsWith('/')
            ? capturedPath
            : "/" + capturedPath;

        // Step 2: locate the .md suffix (case-insensitive); reject if absent
        if (!withSlash.EndsWith(Constants.Routes.MarkdownSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Path '{capturedPath}' does not end in '.md'.",
                nameof(capturedPath));
        }

        // Step 3: strip the suffix(es). Order matters — longer match first.
        string canonical;
        if (EndsWithIgnoreCase(withSlash, Constants.Routes.IndexHtmlMdSuffix))
        {
            // /docs/index.html.md → /docs/ (per the llms.txt spec convention)
            canonical = withSlash[..^Constants.Routes.IndexHtmlMdSuffix.Length] + "/";
        }
        else if (withSlash.EndsWith("/.md", StringComparison.OrdinalIgnoreCase))
        {
            // /docs/.md → /docs/  (AC8 — typographical-artefact form, normalise without 301)
            // Reject /.md (just the suffix with no body) — collapsing to "/" would silently
            // serve the homepage for what is plainly malformed input.
            if (withSlash.Length == "/.md".Length)
            {
                throw new ArgumentException(
                    "Path '/.md' has no content segment to normalise.",
                    nameof(capturedPath));
            }
            canonical = withSlash[..^"/.md".Length] + "/";
        }
        else
        {
            // /home.md → /home  (the canonical form)
            // Reject the bare ".md" / "/.md" — same reasoning as above.
            if (withSlash.Length == Constants.Routes.MarkdownSuffix.Length + 1)
            {
                throw new ArgumentException(
                    "Path has no content segment to normalise.",
                    nameof(capturedPath));
            }
            canonical = withSlash[..^Constants.Routes.MarkdownSuffix.Length];
        }

        // Step 4: URL-decode (handles UTF-8 percent-encoded paths like /caf%C3%A9)
        var decoded = WebUtility.UrlDecode(canonical);

        // Step 5: re-check traversal after decoding so `%2E%2E%2F` can't slip through.
        if (ContainsTraversal(decoded))
        {
            throw new ArgumentException(
                $"Path '{capturedPath}' contains traversal segments after decoding.",
                nameof(capturedPath));
        }

        return decoded;
    }

    private static bool ContainsTraversal(string path)
    {
        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return true;
        }
        // Match `/..` or `..` as an isolated segment (start, between slashes, or end).
        var segments = path.Split('/', StringSplitOptions.None);
        foreach (var seg in segments)
        {
            if (seg == "..")
            {
                return true;
            }
        }
        return false;
    }

    private static bool EndsWithIgnoreCase(string s, string suffix)
        => s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
