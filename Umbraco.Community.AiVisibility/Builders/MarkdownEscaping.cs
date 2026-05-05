using System.Text;

namespace LlmsTxt.Umbraco.Builders;

/// <summary>
/// Shared Markdown helpers used by both <see cref="DefaultLlmsTxtBuilder"/> (Story
/// 2.1) and <see cref="DefaultLlmsFullBuilder"/> (Story 2.2). Lifted from
/// <see cref="DefaultLlmsTxtBuilder"/> so the <c>/llms-full.txt</c> builder doesn't
/// have to reach into the index builder's internals — pure refactor with no
/// behaviour change versus Story 2.1.
/// </summary>
internal static class MarkdownEscaping
{
    /// <summary>
    /// Escape Markdown link-text and heading-text special characters
    /// (<c>[</c>, <c>]</c>, <c>(</c>, <c>)</c>, <c>\</c>, <c>`</c>) per CommonMark
    /// § 6.6 inline-link grammar. Also replaces ASCII control characters
    /// (U+0000–U+001F and U+007F, which include <c>\n</c>, <c>\r</c>, <c>\t</c>) with
    /// a single space so a page Name carrying a stray newline doesn't split the
    /// surrounding ATX heading or break inline-link text. Used for:
    /// <list type="bullet">
    /// <item>Link text in <c>/llms.txt</c> bullet links (Story 2.1).</item>
    /// <item>The H1 title prefix on each <c>/llms-full.txt</c> page section
    /// (Story 2.2) — backticks inside an ATX heading would mark inline-code spans
    /// and break visible title rendering; an embedded newline would terminate the
    /// heading and orphan the rest of the title as a paragraph.</item>
    /// </list>
    /// </summary>
    internal static string EscapeMarkdownLinkText(string title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        var sb = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (c is '\\' or '[' or ']' or '(' or ')' or '`')
            {
                sb.Append('\\');
                sb.Append(c);
            }
            else if (c < 0x20 || c == 0x7F)
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strip a leading YAML frontmatter block (<c>---\n…\n---\n</c>) from extracted
    /// Markdown. Used by:
    /// <list type="bullet">
    /// <item><c>/llms.txt</c> body-fallback summaries (Story 2.1) — so summaries
    /// reflect content, not metadata.</item>
    /// <item><c>/llms-full.txt</c> page concatenation (Story 2.2) — so the
    /// manifest body doesn't carry N copies of frontmatter, one per page.</item>
    /// </list>
    /// Recognises only an opening <c>---</c> at the start of the string followed by
    /// a closing <c>\n---</c>. Bodies that start <c>---xyz</c> or
    /// <c>---&lt;non-newline&gt;</c> do NOT have frontmatter detected.
    /// </summary>
    internal static string StripFrontmatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal))
        {
            return markdown;
        }
        var closeIdx = markdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closeIdx < 0)
        {
            return markdown;
        }
        var afterClose = closeIdx + 4; // skip "\n---"
        // Skip the trailing newline if present.
        while (afterClose < markdown.Length && (markdown[afterClose] == '\n' || markdown[afterClose] == '\r'))
        {
            afterClose++;
        }
        return markdown[afterClose..];
    }
}
