using ReverseMarkdown;
using ReverseMarkdown.Readers;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// HTML → Markdown wrapper around <see cref="Converter"/> with the package's
/// extraction-friendly configuration baked in. Shared as a singleton: readers are
/// registered only in the constructor, and concurrent <see cref="Convert"/> calls
/// were checked on ReverseMarkdown 6.3.0.
/// </summary>
internal sealed class MarkdownConverter
{
    private readonly Converter _converter;

    public MarkdownConverter()
    {
        _converter = new Converter(BuildConfig());

        // ReverseMarkdown 6.x converts <figure> into a single image whose alt text is
        // replaced by the <figcaption> text: the real alt text is lost, any second
        // image is dropped, and a link in the caption is nested inside the image
        // markup. Bypassing the <figure> wrapper keeps each image with its own alt
        // text and the caption as text after the image (as in 5.x, though 5.x kept
        // the caption on the next line of the same paragraph; it is now a separate
        // paragraph).
        _converter.RegisterReader("figure", new BypassReader());
    }

    public string Convert(string html) => _converter.Convert(html);

    private static Config BuildConfig()
    {
        // ReverseMarkdown 6.x moved most options into nested groups (Tags,
        // Formatting, Links). GithubFlavored is NOT obsolete and still drives GFM
        // output. Do not switch to Config.Flavor = MarkdownFlavor.GitHub: on 6.3.0
        // it makes Convert return the input HTML unchanged.
        var config = new Config
        {
            GithubFlavored = true,
        };

        // Bypass drops unrecognised (e.g. adopter custom) tags but keeps their inner
        // content, so text inside them is not lost. It does not keep the raw HTML
        // (that would be PassThrough).
        config.Tags.Unknown = Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;

        // WhitelistedSchemes is a read-only HashSet — security boundary against
        // javascript:, data:, file: hrefs. Populate by mutation.
        config.Links.WhitelistedSchemes.Clear();
        config.Links.WhitelistedSchemes.Add("http");
        config.Links.WhitelistedSchemes.Add("https");
        config.Links.WhitelistedSchemes.Add("mailto");
        config.Links.WhitelistedSchemes.Add("tel");

        return config;
    }
}
