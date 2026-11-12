using Umbraco.Community.AiVisibility.Persistence.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;

namespace Umbraco.Community.AiVisibility.Tests.Persistence.Migrations;

[TestFixture]
public class CreateAiVisibilitySettingsDoctypeTests
{
    private IContentTypeService _contentTypeService = null!;
    private IDataTypeService _dataTypeService = null!;
    private IShortStringHelper _shortStringHelper = null!;
    private IMigrationContext _context = null!;
    private IConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _contentTypeService = Substitute.For<IContentTypeService>();
        _dataTypeService = Substitute.For<IDataTypeService>();
        _shortStringHelper = Substitute.For<IShortStringHelper>();
        _context = Substitute.For<IMigrationContext>();
        _configuration = BuildConfiguration(skipSettingsDoctype: null);

        // Built-in data type stubs — return non-null IDataType for each.
        // Configure each int-overload call individually; Arg.Any<int>() routes
        // NSubstitute to the wrong overload-matcher in 17.3.2.
        var stubDt = StubDataType();
        _dataTypeService.GetDataType(global::Umbraco.Cms.Core.Constants.DataTypes.Textbox).Returns(stubDt);
        _dataTypeService.GetDataType(global::Umbraco.Cms.Core.Constants.DataTypes.Textarea).Returns(stubDt);
        _dataTypeService.GetDataType(global::Umbraco.Cms.Core.Constants.DataTypes.Boolean).Returns(stubDt);
    }

    [Test]
    public async Task MigrateAsync_DoctypesDoNotExist_CreatesSettingsAndComposition()
    {
        // Story 3.1 AC1 — fresh install creates both the Settings doctype
        // AND the per-page exclusion composition.
        // (Asserting on Name not Alias — ContentType.Alias setter routes
        // through IShortStringHelper.CleanStringForSafeAlias which is an
        // extension method we can't configure on the interface stub.)
        _contentTypeService.Get(Arg.Any<string>()).Returns((IContentType?)null);

        var migration = MakeMigration();
        await migration.RunAsync();

        _contentTypeService.Received().Save(
            Arg.Is<IContentType>(c => c.Name == "AI Visibility Exclusion (composition)"),
            Arg.Any<int>());
        _contentTypeService.Received().Save(
            Arg.Is<IContentType>(c => c.Name == "AI Visibility Settings"),
            Arg.Any<int>());
    }

    [Test]
    public async Task MigrateAsync_DoctypesAlreadyExist_NoOp()
    {
        // Idempotent re-run — second boot must not create duplicates.
        var existingComposition = Substitute.For<IContentType>();
        existingComposition.Alias.Returns(CreateAiVisibilitySettingsDoctype.CompositionAlias);
        var existingSettings = Substitute.For<IContentType>();
        existingSettings.Alias.Returns(CreateAiVisibilitySettingsDoctype.SettingsDoctypeAlias);

        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.CompositionAlias).Returns(existingComposition);
        _contentTypeService.Get(CreateAiVisibilitySettingsDoctype.SettingsDoctypeAlias).Returns(existingSettings);

        var migration = MakeMigration();
        await migration.RunAsync();

        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
    }

    [Test]
    public async Task MigrateAsync_SkipFlagTrue_DoesNotTouchContentTypes()
    {
        // uSync coexistence: with SkipSettingsDoctype=true the step returns
        // before any lookup, so neither a missing nor an existing (uSync-
        // imported) doctype/composition is read or written. The rest of the
        // plan (request-log table) is unaffected by this step.
        _configuration = BuildConfiguration(skipSettingsDoctype: true);
        _contentTypeService.ClearReceivedCalls();
        _dataTypeService.ClearReceivedCalls();

        var migration = MakeMigration();
        await migration.RunAsync();

        _contentTypeService.DidNotReceive().Get(Arg.Any<string>());
        _contentTypeService.DidNotReceive().Save(Arg.Any<IContentType>(), Arg.Any<int>());
        Assert.That(_dataTypeService.ReceivedCalls(), Is.Empty,
            "skip flag must short-circuit before any data type lookup");
        Assert.That(_contentTypeService.ReceivedCalls(), Is.Empty,
            "skip flag must short-circuit before any content type lookup or save");
    }

    [Test]
    public async Task MigrateAsync_SkipFlagFalse_CreatesSettingsAndComposition()
    {
        // Explicit false behaves exactly like a missing key.
        _configuration = BuildConfiguration(skipSettingsDoctype: false);
        _contentTypeService.Get(Arg.Any<string>()).Returns((IContentType?)null);

        var migration = MakeMigration();
        await migration.RunAsync();

        _contentTypeService.Received().Save(
            Arg.Is<IContentType>(c => c.Name == "AI Visibility Exclusion (composition)"),
            Arg.Any<int>());
        _contentTypeService.Received().Save(
            Arg.Is<IContentType>(c => c.Name == "AI Visibility Settings"),
            Arg.Any<int>());
    }

    [Test]
    public async Task MigrateAsync_FreshInstall_CompositionHasShapeTheTabMoveAccepts()
    {
        // Pins that the composition this step creates is exactly what
        // MoveExclusionGroupIntoTab_1_3 moves on a fresh install. A real
        // short-string helper is needed so aliases survive.
        _shortStringHelper = new DefaultShortStringHelper(new DefaultShortStringHelperConfig());
        _contentTypeService.Get(Arg.Any<string>()).Returns((IContentType?)null);
        IContentType? savedComposition = null;
#pragma warning disable CS0618 // the step under test calls the obsolete Save
        _contentTypeService.Save(
            Arg.Do<IContentType>(c =>
            {
                if (c.Alias == CreateAiVisibilitySettingsDoctype.CompositionAlias)
                {
                    savedComposition = c;
                }
            }),
            Arg.Any<int>());
#pragma warning restore CS0618

        await MakeMigration().RunAsync();

        Assert.That(savedComposition, Is.Not.Null);
        Assert.That(MoveExclusionGroupIntoTab_1_3.GetShapeMismatch(savedComposition!), Is.Null);
    }

    [Test]
    public void ShouldSkipSettingsDoctype_True_ReturnsTrue()
    {
        var configuration = BuildConfiguration(skipSettingsDoctype: true);

        Assert.That(CreateAiVisibilitySettingsDoctype.ShouldSkipSettingsDoctype(configuration), Is.True);
    }

    [Test]
    public void ShouldSkipSettingsDoctype_Missing_ReturnsFalse()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.That(CreateAiVisibilitySettingsDoctype.ShouldSkipSettingsDoctype(configuration), Is.False);
    }

    private static IConfiguration BuildConfiguration(bool? skipSettingsDoctype)
    {
        var entries = new Dictionary<string, string?>();
        if (skipSettingsDoctype is { } value)
        {
            entries["AiVisibility:Migrations:SkipSettingsDoctype"] = value ? "true" : "false";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    private CreateAiVisibilitySettingsDoctype MakeMigration()
        => new(
            _context,
            _contentTypeService,
            _dataTypeService,
            _shortStringHelper,
            _configuration,
            NullLogger<CreateAiVisibilitySettingsDoctype>.Instance);

    private static IDataType StubDataType()
    {
        var dt = Substitute.For<IDataType>();
        dt.Id.Returns(-88);
        dt.Key.Returns(Guid.NewGuid());
        dt.DatabaseType.Returns(ValueStorageType.Nvarchar);
        dt.EditorAlias.Returns("Umbraco.TextBox");
        return dt;
    }
}
