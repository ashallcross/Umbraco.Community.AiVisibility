using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Umbraco.Community.AiVisibility.Persistence.Migrations;

/// <summary>
/// Story 3.1 — creates the <c>aiVisibilitySettings</c> doctype + the
/// <c>llmsTxtSettingsComposition</c> element type imperatively via
/// <see cref="IContentTypeService"/>. Idempotent: re-runs are no-ops.
/// Story 6.0c (2026-05-06) — pre-1.0 in-place rename of the doctype alias
/// (project-context.md immutability rule applies once shipped to NuGet;
/// pre-1.0 alpha = treat as if not yet shipped).
/// </summary>
/// <remarks>
/// Pattern source: AgentRun's <c>AddAgentRunSectionToAdminGroup</c> uses
/// the same <see cref="AsyncMigrationBase"/> + service-injection shape.
/// </remarks>
public sealed class CreateAiVisibilitySettingsDoctype : AsyncMigrationBase
{
    internal const string SettingsDoctypeAlias = "aiVisibilitySettings";
    internal const string CompositionAlias = "llmsTxtSettingsComposition";
    internal const string ExcludeBoolAlias = "excludeFromLlmExports";

    private readonly IContentTypeService _contentTypeService;
    private readonly IDataTypeService _dataTypeService;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly ILogger<CreateAiVisibilitySettingsDoctype> _logger;

    public CreateAiVisibilitySettingsDoctype(
        IMigrationContext context,
        IContentTypeService contentTypeService,
        IDataTypeService dataTypeService,
        IShortStringHelper shortStringHelper,
        ILogger<CreateAiVisibilitySettingsDoctype> logger)
        : base(context)
    {
        _contentTypeService = contentTypeService;
        _dataTypeService = dataTypeService;
        _shortStringHelper = shortStringHelper;
        _logger = logger;
    }

    protected override async Task MigrateAsync()
    {
        var textbox = _dataTypeService.GetDataType(global::Umbraco.Cms.Core.Constants.DataTypes.Textbox)
            ?? throw new InvalidOperationException("Built-in Textbox data type not found");
        var textarea = _dataTypeService.GetDataType(global::Umbraco.Cms.Core.Constants.DataTypes.Textarea)
            ?? throw new InvalidOperationException("Built-in Textarea data type not found");
        var trueFalse = _dataTypeService.GetDataType(global::Umbraco.Cms.Core.Constants.DataTypes.Boolean)
            ?? throw new InvalidOperationException("Built-in Boolean data type not found");

        EnsureComposition(trueFalse);
        EnsureSettingsDoctype(textbox, textarea);
        await Task.CompletedTask;
    }

    private void EnsureComposition(IDataType trueFalse)
    {
        var existing = _contentTypeService.Get(CompositionAlias);
        if (existing is not null)
        {
            // An existing composition (manual or uSync-imported) is left alone
            // — but if it lacks the property we depend on, the per-page bool
            // reads in MarkdownController/LlmsTxt(Full)Controller will silently
            // return false on every page. Surface that as a Warning so the
            // operator can see the schema-drift signal in logs without us
            // mutating someone else's content type.
            var hasExpectedProperty = existing.PropertyTypes
                .Any(p => string.Equals(p.Alias, ExcludeBoolAlias, StringComparison.OrdinalIgnoreCase));
            if (!hasExpectedProperty)
            {
                _logger.LogWarning(
                    "Composition {Alias} exists but does not declare expected property {Property}; per-page exclude toggle will be silently ignored on every page",
                    CompositionAlias,
                    ExcludeBoolAlias);
            }
            else
            {
                _logger.LogDebug("Composition {Alias} already exists; skipping", CompositionAlias);
            }
            return;
        }

        var composition = new ContentType(_shortStringHelper, parentId: -1)
        {
            Alias = CompositionAlias,
            Name = "AI Visibility Exclusion (composition)",
            Description = "Apply to any of your own doctypes to expose a per-page \"Exclude from LLM exports\" toggle.",
            Icon = "icon-eye-blocked color-red",
            IsElement = true,
            AllowedAsRoot = false,
        };

        composition.AddPropertyGroup("llmsTxt", "LLM exports");
        composition.AddPropertyType(new PropertyType(_shortStringHelper, trueFalse)
        {
            Alias = ExcludeBoolAlias,
            Name = "Exclude from LLM exports",
            Description = "When true, this page is omitted from /llms.txt, /llms-full.txt, and returns 404 for /{path}.md.",
            SortOrder = 10,
        }, "llmsTxt", "LLM exports");

        _contentTypeService.Save(composition);
        _logger.LogInformation("Composition {Alias} created", CompositionAlias);
    }

    private void EnsureSettingsDoctype(IDataType textbox, IDataType textarea)
    {
        var existing = _contentTypeService.Get(SettingsDoctypeAlias);
        if (existing is not null)
        {
            // Surface schema drift on an existing (manual / uSync-imported)
            // doctype: if any expected property is missing, the resolver will
            // silently fall back to appsettings on that field. Warn so the
            // operator can see the drift in logs.
            var expected = new[] { "siteName", "siteSummary", "excludedDoctypeAliases" };
            var missing = expected
                .Where(alias => !existing.PropertyTypes.Any(p => string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (missing.Length > 0)
            {
                _logger.LogWarning(
                    "Doctype {Alias} exists but is missing expected properties {Missing}; resolver will silently fall back to appsettings on these fields",
                    SettingsDoctypeAlias,
                    string.Join(", ", missing));
            }
            else
            {
                _logger.LogDebug("Doctype {Alias} already exists; skipping", SettingsDoctypeAlias);
            }
            return;
        }

        var settings = new ContentType(_shortStringHelper, parentId: -1)
        {
            Alias = SettingsDoctypeAlias,
            Name = "AI Visibility Settings",
            Description = "Settings for the Umbraco.Community.AiVisibility package — site name, summary, and per-doctype exclusion list. One node per Umbraco install, applied to every site (allowed at root). Adopters needing per-site overrides register a custom ISettingsResolver — see docs/getting-started.md.",
            Icon = "icon-settings color-purple",
            AllowedAsRoot = true,
        };

        settings.AddPropertyGroup("settings", "Settings");
        settings.AddPropertyGroup("exclusion", "Exclusion");

        settings.AddPropertyType(new PropertyType(_shortStringHelper, textbox)
        {
            Alias = "siteName",
            Name = "Site name",
            Description = "Override the package's H1 / site name on /llms.txt. Empty falls back to the matched root content node's name.",
            SortOrder = 10,
        }, "settings", "Settings");

        settings.AddPropertyType(new PropertyType(_shortStringHelper, textarea)
        {
            Alias = "siteSummary",
            Name = "Site summary",
            Description = "One-paragraph site summary emitted as the blockquote under the /llms.txt H1. 500-char soft cap (the resolver truncates at read time).",
            SortOrder = 20,
        }, "settings", "Settings");

        settings.AddPropertyType(new PropertyType(_shortStringHelper, textarea)
        {
            Alias = "excludedDoctypeAliases",
            Name = "Excluded doctype aliases",
            Description = "One doctype alias per line (or comma/semicolon separated). Pages whose ContentType.Alias matches any line are omitted from /llms.txt, /llms-full.txt, and .md (404). Cumulative with appsettings AiVisibility:ExcludedDoctypeAliases.",
            SortOrder = 30,
        }, "exclusion", "Exclusion");

        _contentTypeService.Save(settings);
        _logger.LogInformation("Doctype {Alias} created", SettingsDoctypeAlias);
    }
}
