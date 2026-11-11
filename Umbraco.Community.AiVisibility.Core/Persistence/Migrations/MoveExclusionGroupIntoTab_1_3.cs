using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Exceptions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Umbraco.Community.AiVisibility.Persistence.Migrations;

/// <summary>
/// Moves the "LLM exports" group of the <c>llmsTxtSettingsComposition</c>
/// composition into an "AI Visibility" tab, so on tabbed doctypes the
/// "Exclude from LLM exports" checkbox no longer lands in Umbraco's automatic
/// "Generic" tab.
/// </summary>
/// <remarks>
/// <para>
/// Serves both fresh installs (<see cref="CreateAiVisibilitySettingsDoctype"/>
/// creates the loose group, this step nests it) and upgraded installs, so the
/// shipped creation step stays unchanged.
/// </para>
/// <para>
/// The composition is only touched while it still has the exact shape the
/// package created: one group <c>llmsTxt</c> holding the single property
/// <c>excludeFromLlmExports</c>, and no compositions of its own. Anything else
/// means the adopter (or uSync) owns it, so the step logs and skips. Honours
/// <c>AiVisibility:Migrations:SkipSettingsDoctype</c>.
/// </para>
/// <para>
/// The step never fails boot over a container clash: when another content
/// type already has a container aliased like the new tab (or nested group)
/// with a different container type, the step logs a Warning and skips before
/// calling <c>Save</c>. A failing <c>Save</c> would roll back the migration
/// scope (including the plan-state write), so the clash is detected up front.
/// </para>
/// </remarks>
public sealed class MoveExclusionGroupIntoTab_1_3 : AsyncMigrationBase
{
    internal const string TabAlias = "aiVisibility";
    internal const string TabName = "AI Visibility";
    internal const int TabSortOrder = 100;
    internal const string GroupLocalAlias = "llmsTxt";
    internal const string NestedGroupAlias = TabAlias + "/" + GroupLocalAlias;

    private readonly IContentTypeService _contentTypeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MoveExclusionGroupIntoTab_1_3> _logger;

    public MoveExclusionGroupIntoTab_1_3(
        IMigrationContext context,
        IContentTypeService contentTypeService,
        IConfiguration configuration,
        ILogger<MoveExclusionGroupIntoTab_1_3> logger)
        : base(context)
    {
        _contentTypeService = contentTypeService;
        _configuration = configuration;
        _logger = logger;
    }

    protected override Task MigrateAsync()
    {
        if (CreateAiVisibilitySettingsDoctype.ShouldSkipSettingsDoctype(_configuration))
        {
            _logger.LogInformation(
                "AiVisibility: SkipSettingsDoctype=true; not moving the {Alias} exclusion group into the {Tab} tab",
                CreateAiVisibilitySettingsDoctype.CompositionAlias,
                TabName);
            return Task.CompletedTask;
        }

        var composition = _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias);
        if (composition is null)
        {
            _logger.LogInformation(
                "AiVisibility: composition {Alias} not found; nothing to move into the {Tab} tab",
                CreateAiVisibilitySettingsDoctype.CompositionAlias,
                TabName);
            return Task.CompletedTask;
        }

        var mismatch = GetShapeMismatch(composition);
        if (mismatch is not null)
        {
            _logger.LogInformation(
                "AiVisibility: composition {Alias} left unchanged ({Reason}); the exclusion group was not moved into the {Tab} tab",
                CreateAiVisibilitySettingsDoctype.CompositionAlias,
                mismatch,
                TabName);
            return Task.CompletedTask;
        }

        var others = _contentTypeService.GetAll()
            .Where(c => c.Id != composition.Id
                && !string.Equals(c.Alias, composition.Alias, StringComparison.OrdinalIgnoreCase));
        var conflict = FindContainerConflict(others);
        if (conflict is not null)
        {
            _logger.LogWarning(
                "AiVisibility: composition {Alias} left unchanged; the exclusion group was not moved into the {Tab} tab because {Conflict}. Rename or retype that container, or move the group manually",
                CreateAiVisibilitySettingsDoctype.CompositionAlias,
                TabName,
                conflict);
            return Task.CompletedTask;
        }

        if (!MoveGroupIntoTab(composition))
        {
            _logger.LogWarning(
                "AiVisibility: composition {Alias} left unchanged; could not add the {TabAlias} tab",
                CreateAiVisibilitySettingsDoctype.CompositionAlias,
                TabAlias);
            return Task.CompletedTask;
        }

        try
        {
#pragma warning disable CS0618 // Synchronous Save keeps validation inline, same as the creation step.
            _contentTypeService.Save(composition);
#pragma warning restore CS0618
        }
        catch (InvalidCompositionException ex)
        {
            _logger.LogWarning(
                ex,
                "AiVisibility: could not move the exclusion group of composition {Alias} into the {Tab} tab (conflicting property aliases: {PropertyAliases}; conflicting container aliases: {ContainerAliases}). The change was rolled back and this step will retry on the next boot",
                CreateAiVisibilitySettingsDoctype.CompositionAlias,
                TabName,
                string.Join(", ", ex.PropertyTypeAliases ?? Array.Empty<string>()),
                string.Join(", ", ex.PropertyGroupAliases ?? Array.Empty<string>()));
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "AiVisibility: moved the exclusion group of composition {Alias} into the {Tab} tab ({NestedAlias})",
            CreateAiVisibilitySettingsDoctype.CompositionAlias,
            TabName,
            NestedGroupAlias);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns <c>null</c> when the composition still has the package-created
    /// shape, otherwise a short reason why it is left alone.
    /// </summary>
    internal static string? GetShapeMismatch(IContentTypeComposition composition)
    {
        if (composition.ContentTypeComposition.Any())
        {
            return "it composes other content types";
        }

        var groups = composition.PropertyGroups.ToArray();
        if (IsAlreadyNested(groups))
        {
            return "the exclusion group is already nested";
        }

        if (groups.Length != 1)
        {
            return $"expected 1 property group, found {groups.Length}";
        }

        var group = groups[0];
        if (!string.Equals(group.Alias, GroupLocalAlias, StringComparison.OrdinalIgnoreCase))
        {
            return $"unexpected property group alias '{group.Alias}'";
        }

        if (group.Type != PropertyGroupType.Group)
        {
            return $"property group '{group.Alias}' is a {group.Type}, not a group";
        }

        var properties = composition.PropertyTypes.ToArray();
        if (properties.Length != 1)
        {
            return $"expected 1 property, found {properties.Length}";
        }

        var property = properties[0];
        if (!string.Equals(property.Alias, CreateAiVisibilitySettingsDoctype.ExcludeBoolAlias, StringComparison.OrdinalIgnoreCase))
        {
            return $"unexpected property alias '{property.Alias}'";
        }

        if (group.PropertyTypes is null || !group.PropertyTypes.Contains(property))
        {
            return $"property '{property.Alias}' is not in group '{group.Alias}'";
        }

        return null;
    }

    /// <summary>
    /// Returns <c>null</c> when no other content type has a container that would
    /// clash with the tab or nested group, otherwise a description of the clash.
    /// </summary>
    internal static string? FindContainerConflict(IEnumerable<IContentTypeComposition> others)
    {
        var clashes = new List<string>();
        foreach (var other in others)
        {
            foreach (var group in other.PropertyGroups)
            {
                if (string.Equals(group.Alias, TabAlias, StringComparison.OrdinalIgnoreCase)
                    && group.Type != PropertyGroupType.Tab)
                {
                    clashes.Add($"'{other.Alias}' has container '{group.Alias}' of type {group.Type}");
                }
                else if (string.Equals(group.Alias, NestedGroupAlias, StringComparison.OrdinalIgnoreCase)
                    && group.Type != PropertyGroupType.Group)
                {
                    clashes.Add($"'{other.Alias}' has container '{group.Alias}' of type {group.Type}");
                }
            }
        }

        return clashes.Count == 0 ? null : string.Join("; ", clashes);
    }

    /// <summary>
    /// Adds the "AI Visibility" tab and re-aliases the existing group under it.
    /// The group instance (and its id and property) is kept. Returns
    /// <c>false</c> without changing anything when the tab cannot be added.
    /// </summary>
    internal static bool MoveGroupIntoTab(IContentTypeComposition composition)
    {
        var group = composition.PropertyGroups
            .Single(g => string.Equals(g.Alias, GroupLocalAlias, StringComparison.OrdinalIgnoreCase));

        if (!composition.AddPropertyGroup(TabAlias, TabName))
        {
            return false;
        }

        var tab = composition.PropertyGroups
            .Single(g => string.Equals(g.Alias, TabAlias, StringComparison.OrdinalIgnoreCase));
        tab.Type = PropertyGroupType.Tab;
        tab.SortOrder = TabSortOrder;

        group.Alias = NestedGroupAlias;
        return true;
    }

    private static bool IsAlreadyNested(PropertyGroup[] groups)
    {
        if (groups.Length == 1)
        {
            return string.Equals(groups[0].GetLocalAlias(), GroupLocalAlias, StringComparison.OrdinalIgnoreCase)
                && string.Equals(groups[0].GetParentAlias(), TabAlias, StringComparison.OrdinalIgnoreCase);
        }

        if (groups.Length == 2)
        {
            return groups.Any(g => string.Equals(g.Alias, TabAlias, StringComparison.OrdinalIgnoreCase) && g.Type == PropertyGroupType.Tab)
                && groups.Any(g => string.Equals(g.Alias, NestedGroupAlias, StringComparison.OrdinalIgnoreCase) && g.Type == PropertyGroupType.Group);
        }

        return false;
    }
}
