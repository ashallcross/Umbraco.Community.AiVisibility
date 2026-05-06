using Umbraco.Community.AiVisibility.Persistence.Migrations;
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

    [SetUp]
    public void Setup()
    {
        _contentTypeService = Substitute.For<IContentTypeService>();
        _dataTypeService = Substitute.For<IDataTypeService>();
        _shortStringHelper = Substitute.For<IShortStringHelper>();
        _context = Substitute.For<IMigrationContext>();

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
            Arg.Is<IContentType>(c => c.Name == "LlmsTxt Exclusion (composition)"),
            Arg.Any<int>());
        _contentTypeService.Received().Save(
            Arg.Is<IContentType>(c => c.Name == "LlmsTxt Settings"),
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

    private CreateAiVisibilitySettingsDoctype MakeMigration()
        => new(
            _context,
            _contentTypeService,
            _dataTypeService,
            _shortStringHelper,
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
