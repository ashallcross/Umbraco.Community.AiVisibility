using ReverseMarkdown;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// HTML → Markdown wrapper around <see cref="Converter"/> with the package's
/// extraction-friendly configuration baked in. Singleton-safe — ReverseMarkdown's
/// <see cref="Converter"/> is thread-safe per its docs.
/// </summary>
internal sealed class MarkdownConverter
{
    private readonly Converter _converter;

    public MarkdownConverter()
    {
        _converter = new Converter(BuildConfig());
    }

    public string Convert(string html) => _converter.Convert(html);

    private static Config BuildConfig()
    {
        var config = new Config
        {
            // Keep adopter custom tags as raw HTML — avoids data loss on unrecognised
            // elements; ReverseMarkdown's recommended default for content extraction.
            UnknownTags = Config.UnknownTagsOption.Bypass,
            RemoveComments = true,
            GithubFlavored = true,
            SmartHrefHandling = true,
        };

        // WhitelistUriSchemes is a read-only HashSet — security boundary against
        // javascript:, data:, file: hrefs. Populate by mutation.
        config.WhitelistUriSchemes.Clear();
        config.WhitelistUriSchemes.Add("http");
        config.WhitelistUriSchemes.Add("https");
        config.WhitelistUriSchemes.Add("mailto");
        config.WhitelistUriSchemes.Add("tel");

        return config;
    }
}
