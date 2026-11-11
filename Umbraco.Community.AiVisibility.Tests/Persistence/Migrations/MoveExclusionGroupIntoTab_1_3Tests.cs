using Umbraco.Community.AiVisibility.Persistence.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Exceptions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Umbraco.Community.AiVisibility.Tests.Persistence.Migrations;

// The migration under test calls the obsolete IContentTypeService.Save; assertions on it are intentional.
#pragma warning disable CS0618

[TestFixture]
public class MoveExclusionGroupIntoTab_1_3Tests
{
    // A real short-string helper so ContentType/PropertyType/PropertyGroup
    // aliases behave as they do at runtime (a substitute leaves them unusable).
    private static readonly IShortStringHelper ShortStringHelper =
        new DefaultShortStringHelper(new DefaultShortStringHelperConfig());

    private IContentTypeService _contentTypeService = null!;
    private IMigrationContext _context = null!;
    private IConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _contentTypeService = Substitute.For<IContentTypeService>();
        _context = Substitute.For<IMigrationContext>();
        _configuration = BuildConfiguration(skipSettingsDoctype: null);
        _contentTypeService.GetAll().Returns(Array.Empty<IContentType>());
    }

    [Test]
    public async Task MoveIntoTab_PackageShape_NestsGroupUnderTab()
    {
        var composition = BuildPackageShapedComposition();
        var group = composition.PropertyGroups[MoveExclusionGroupIntoTab_1_3.GroupLocalAlias]!;
        group.Id = 42;
        var property = composition.PropertyTypes.Single();
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);

        await MakeMigration().RunAsync();

        _contentTypeService.Received(1).Save(composition, Arg.Any<int>());

        Assert.That(composition.PropertyGroups, Has.Count.EqualTo(2));

        var tab = composition.PropertyGroups["aiVisibility"]!;
        Assert.That(tab.Name, Is.EqualTo("AI Visibility"));
        Assert.That(tab.Type, Is.EqualTo(PropertyGroupType.Tab));
        Assert.That(tab.SortOrder, Is.EqualTo(MoveExclusionGroupIntoTab_1_3.TabSortOrder));

        Assert.That(composition.PropertyGroups.Contains("llmsTxt"), Is.False);
        var nested = composition.PropertyGroups["aiVisibility/llmsTxt"]!;
        Assert.That(nested, Is.SameAs(group), "the existing group is re-aliased, not recreated");
        Assert.That(nested.Id, Is.EqualTo(42), "group id is kept so the property stays attached");
        Assert.That(nested.Name, Is.EqualTo("LLM exports"));
        Assert.That(nested.Type, Is.EqualTo(PropertyGroupType.Group));
        Assert.That(nested.GetParentAlias(), Is.EqualTo("aiVisibility"));
        Assert.That(nested.GetLocalAlias(), Is.EqualTo("llmsTxt"));
        Assert.That(nested.PropertyTypes!.Single(), Is.SameAs(property), "property type (and its values) is kept");
        Assert.That(composition.PropertyTypes.Single().Alias, Is.EqualTo("excludeFromLlmExports"));
    }

    [Test]
    public async Task MigrateAsync_SkipFlagTrue_DoesNotTouchContentTypes()
    {
        _configuration = BuildConfiguration(skipSettingsDoctype: true);

        await MakeMigration().RunAsync();

        Assert.That(_contentTypeService.ReceivedCalls(), Is.Empty,
            "skip flag must short-circuit before any content type lookup or save");
    }

    [Test]
    public async Task MigrateAsync_CompositionMissing_DoesNotSave()
    {
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns((IContentType?)null);

        await MakeMigration().RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
    }

    [TestCase("extra-property-in-group")]
    [TestCase("extra-property-in-other-group")]
    [TestCase("extra-group")]
    [TestCase("group-is-tab")]
    [TestCase("group-renamed")]
    [TestCase("property-renamed")]
    public async Task MigrateAsync_CustomisedComposition_DoesNotSave(string variant)
    {
        var composition = BuildPackageShapedComposition();
        switch (variant)
        {
            case "extra-property-in-group":
                composition.AddPropertyType(NewProperty("seoNoIndex"), "llmsTxt", "LLM exports");
                break;
            case "extra-property-in-other-group":
                composition.AddPropertyType(NewProperty("seoNoIndex"), "seo", "SEO");
                break;
            case "extra-group":
                composition.AddPropertyGroup("seo", "SEO");
                break;
            case "group-is-tab":
                composition.PropertyGroups["llmsTxt"]!.Type = PropertyGroupType.Tab;
                break;
            case "group-renamed":
                composition.PropertyGroups["llmsTxt"]!.Alias = "aiExports";
                break;
            case "property-renamed":
                composition.PropertyTypes.Single().Alias = "hideFromAi";
                break;
        }

        Assert.That(MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(composition), Is.Not.Null);

        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);
        var groupAliasesBefore = composition.PropertyGroups.Select(g => g.Alias).ToArray();

        await MakeMigration().RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
        Assert.That(composition.PropertyGroups.Select(g => g.Alias), Is.EqualTo(groupAliasesBefore),
            "a customised composition is never mutated");
    }

    [Test]
    public async Task MigrateAsync_AlreadyNested_DoesNotSave()
    {
        // Re-run: a composition that this step has already moved.
        var composition = BuildPackageShapedComposition();
        Assert.That(MoveExclusionGroupIntoTab_1_3.MoveGroupIntoTab(composition), Is.True);
        Assert.That(MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(composition), Does.Contain("already nested"));
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);

        await MakeMigration().RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
    }

    [Test]
    public async Task MigrateAsync_NestedOnly_DoesNotSave()
    {
        // e.g. a uSync import of only the nested group (tab container dropped).
        var composition = BuildPackageShapedComposition();
        composition.PropertyGroups["llmsTxt"]!.UpdateParentAlias("aiVisibility");
        Assert.That(MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(composition), Does.Contain("already nested"));
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);

        await MakeMigration().RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
        Assert.That(composition.PropertyGroups.Single().Alias, Is.EqualTo("aiVisibility/llmsTxt"));
    }

    [Test]
    public async Task MigrateAsync_NestedUnderOtherParent_DoesNotSave()
    {
        var composition = BuildPackageShapedComposition();
        composition.PropertyGroups["llmsTxt"]!.UpdateParentAlias("content");
        var mismatch = MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(composition);
        Assert.That(mismatch, Does.Contain("unexpected property group alias"));
        Assert.That(mismatch, Does.Not.Contain("already nested"));
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);

        await MakeMigration().RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
        Assert.That(composition.PropertyGroups.Single().Alias, Is.EqualTo("content/llmsTxt"));
    }

    [Test]
    public async Task MigrateAsync_CompositionComposesOthers_DoesNotSave()
    {
        var composition = BuildPackageShapedComposition();
        var parent = new ContentType(ShortStringHelper, parentId: -1) { Alias = "seoComposition", Name = "SEO" };
        composition.AddContentType(parent);
        Assert.That(MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(composition), Is.Not.Null);
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);

        await MakeMigration().RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
    }

    [TestCase("aiVisibility", PropertyGroupType.Group)]
    [TestCase("AIVISIBILITY", PropertyGroupType.Group)]
    [TestCase("aiVisibility/llmsTxt", PropertyGroupType.Tab)]
    public async Task MigrateAsync_OtherTypeHasClashingContainer_SkipsWithoutMutationOrSave(string containerAlias, PropertyGroupType type)
    {
        var composition = BuildPackageShapedComposition();
        composition.Id = 1001;
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);
        var other = BuildContentTypeWithContainer("contentPage", containerAlias, type);
        other.Id = 1002;
        _contentTypeService.GetAll().Returns(new IContentType[] { composition, other });
        var groupAliasesBefore = composition.PropertyGroups.Select(g => g.Alias).ToArray();

        await MakeMigration().RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
        Assert.That(composition.PropertyGroups.Select(g => g.Alias), Is.EqualTo(groupAliasesBefore),
            "the composition is not mutated when a clash is detected");
    }

    [Test]
    public async Task MigrateAsync_OtherTypeHasMatchingContainers_StillMoves()
    {
        // A doctype that already has an "aiVisibility" tab is compatible.
        var composition = BuildPackageShapedComposition();
        composition.Id = 1001;
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);
        var other = BuildContentTypeWithContainer("contentPage", "aiVisibility", PropertyGroupType.Tab);
        other.Id = 1002;
        _contentTypeService.GetAll().Returns(new IContentType[] { composition, other });

        await MakeMigration().RunAsync();

        _contentTypeService.Received(1).Save(composition, Arg.Any<int>());
    }

    [Test]
    public void FindContainerConflict_NamesContentTypeAndContainer()
    {
        var other = BuildContentTypeWithContainer("contentPage", "aiVisibility", PropertyGroupType.Group);

        var conflict = MoveExclusionGroupIntoTab_1_3.FindContainerConflict(new[] { other });

        Assert.That(conflict, Does.Contain("contentPage").And.Contain("aiVisibility"));
        Assert.That(MoveExclusionGroupIntoTab_1_3.FindContainerConflict(Array.Empty<IContentTypeComposition>()), Is.Null);
    }

    [Test]
    public void MigrateAsync_SaveThrowsInvalidComposition_DoesNotFailBoot()
    {
        // Backstop for clashes the pre-check does not cover (e.g. property aliases).
        var composition = BuildPackageShapedComposition();
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(composition);
        _contentTypeService
            .When(s => s.Save(Arg.Any<IContentType>(), Arg.Any<int>()))
            .Do(_ => throw new InvalidCompositionException("contentPage", CreateAiVisibilitySettingsDoctype.CompositionAlias, new[] { "excludeFromLlmExports" }, Array.Empty<string>()));

        Assert.DoesNotThrowAsync(async () => await MakeMigration().RunAsync());
        _contentTypeService.Received(1).Save(composition, Arg.Any<int>());
    }

    [Test]
    public void MoveGroupIntoTab_NormalisesAliasCasing()
    {
        var composition = BuildPackageShapedComposition();
        composition.PropertyGroups["llmsTxt"]!.Alias = "LLMSTXT";

        Assert.That(MoveExclusionGroupIntoTab_1_3.MoveGroupIntoTab(composition), Is.True);

        Assert.That(composition.PropertyGroups.Select(g => g.Alias),
            Is.EquivalentTo(new[] { "aiVisibility", "aiVisibility/llmsTxt" }));
    }

    [Test]
    public void GetShapeMismatch_PackageShape_ReturnsNull()
    {
        Assert.That(MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(BuildPackageShapedComposition()), Is.Null);
    }

    [Test]
    public void GetShapeMismatch_AliasCaseDiffers_ReturnsNull()
    {
        var composition = BuildPackageShapedComposition();
        composition.PropertyGroups["llmsTxt"]!.Alias = "LLMSTXT";
        composition.PropertyTypes.Single().Alias = "ExcludeFromLlmExports";

        Assert.That(MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(composition), Is.Null);
    }

    // Mirrors the shape CreateAiVisibilitySettingsDoctype builds.
    private static ContentType BuildPackageShapedComposition()
    {
        var composition = new ContentType(ShortStringHelper, parentId: -1)
        {
            Alias = CreateAiVisibilitySettingsDoctype.CompositionAlias,
            Name = "AI Visibility Exclusion (composition)",
            IsElement = true,
        };
        composition.AddPropertyGroup("llmsTxt", "LLM exports");
        composition.AddPropertyType(NewProperty(CreateAiVisibilitySettingsDoctype.ExcludeBoolAlias), "llmsTxt", "LLM exports");
        return composition;
    }

    private static ContentType BuildContentTypeWithContainer(string alias, string containerAlias, PropertyGroupType type)
    {
        var contentType = new ContentType(ShortStringHelper, parentId: -1) { Alias = alias, Name = alias };
        contentType.PropertyGroups.Add(new PropertyGroup(new PropertyTypeCollection(true))
        {
            Alias = containerAlias,
            Name = containerAlias,
            Type = type,
        });
        return contentType;
    }

    private static PropertyType NewProperty(string alias)
        => new(ShortStringHelper, "Umbraco.TrueFalse", ValueStorageType.Integer, alias)
        {
            Name = alias,
            SortOrder = 10,
        };

    private static IConfiguration BuildConfiguration(bool? skipSettingsDoctype)
    {
        var entries = new Dictionary<string, string?>();
        if (skipSettingsDoctype is { } value)
        {
            entries["AiVisibility:Migrations:SkipSettingsDoctype"] = value ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    private MoveExclusionGroupIntoTab_1_3 MakeMigration()
        => new(
            _context,
            _contentTypeService,
            _configuration,
            NullLogger<MoveExclusionGroupIntoTab_1_3>.Instance);
}
