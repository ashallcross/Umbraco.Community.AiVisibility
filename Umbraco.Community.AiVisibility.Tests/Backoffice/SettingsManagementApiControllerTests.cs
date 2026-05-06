using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Backoffice;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Cms.Core.Web;

namespace Umbraco.Community.AiVisibility.Tests.Backoffice;

[TestFixture]
public class SettingsManagementApiControllerTests
{
    private static readonly Guid SettingsNodeKey = Guid.Parse("aaaa1111-aaaa-1111-aaaa-aaaa11111111");
    private static readonly Guid HomeNodeKey = Guid.Parse("bbbb2222-bbbb-2222-bbbb-bbbb22222222");
    private static readonly Guid AboutNodeKey = Guid.Parse("cccc3333-cccc-3333-cccc-cccc33333333");

    // ────────────────────────────────────────────────────────────────────────
    // GET / — AC1
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Get_NoSettingsNode_ReturnsAppsettingsSnapshot_WithNullSettingsNodeKey()
    {
        // AC1 — when no aiVisibilitySettings root content node exists, the controller
        // returns the appsettings overlay (whatever the resolver produces) and
        // SettingsNodeKey is null so the dashboard knows the doctype hasn't
        // been initialised yet.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(new ResolvedLlmsSettings(
            SiteName: "Appsettings Site",
            SiteSummary: "Appsettings Summary",
            ExcludedDoctypeAliases: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            BaseSettings: new AiVisibilitySettings { SiteName = "Appsettings Site", SiteSummary = "Appsettings Summary" }));
        fixture.WithRoots(/* none — no Settings node */);

        var result = await fixture.Controller.GetAsync(CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "expected 200 OkObjectResult");
        var view = ok!.Value as SettingsViewModel;
        Assert.That(view, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(view!.SiteName, Is.EqualTo("Appsettings Site"));
            Assert.That(view.SiteSummary, Is.EqualTo("Appsettings Summary"));
            Assert.That(view.SummaryMaxChars, Is.EqualTo(500));
            Assert.That(view.SettingsNodeKey, Is.Null, "no aiVisibilitySettings node → SettingsNodeKey null");
            Assert.That(view.ExcludedDoctypeAliases, Is.Empty);
        });
    }

    [Test]
    public async Task Get_WithSettingsNode_ReturnsResolvedRecord_AndKey()
    {
        // AC1 — happy path: resolver overlays doctype values onto appsettings,
        // and the Settings node's key is surfaced for the dashboard's deep-link
        // affordance.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(new ResolvedLlmsSettings(
            SiteName: "Doctype Site",
            SiteSummary: "Doctype Summary",
            ExcludedDoctypeAliases: new HashSet<string>(new[] { "redirectPage", "errorPage" }, StringComparer.OrdinalIgnoreCase),
            BaseSettings: new AiVisibilitySettings()));
        fixture.WithSettingsRootNode();

        var result = await fixture.Controller.GetAsync(CancellationToken.None);

        var view = ((OkObjectResult)result).Value as SettingsViewModel;
        Assert.That(view, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(view!.SiteName, Is.EqualTo("Doctype Site"));
            Assert.That(view.SiteSummary, Is.EqualTo("Doctype Summary"));
            Assert.That(view.SettingsNodeKey, Is.EqualTo(SettingsNodeKey));
            Assert.That(view.ExcludedDoctypeAliases, Is.EquivalentTo(new[] { "errorPage", "redirectPage" }),
                "aliases sorted ordinal-ignore-case for stable round-trip");
        });
    }

    [Test]
    public async Task Get_ResolverThrows_FallsBackToAppsettings_LogsWarning()
    {
        // AC1 — resolver-throw graceful degradation. The controller catches +
        // logs Warning + returns a "safe defaults" view model rather than 500.
        var fixture = new ControllerFixture();
        fixture.Resolver
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolvedLlmsSettings>>(_ => throw new InvalidOperationException("resolver fault"));

        var result = await fixture.Controller.GetAsync(CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>(), "throw must NOT 500");
        var view = ((OkObjectResult)result).Value as SettingsViewModel;
        Assert.Multiple(() =>
        {
            Assert.That(view!.SiteName, Is.Null);
            Assert.That(view.SiteSummary, Is.Null);
            Assert.That(view.ExcludedDoctypeAliases, Is.Empty);
            Assert.That(view.SummaryMaxChars, Is.EqualTo(500));
        });
    }

    [Test]
    public async Task Get_HonoursCancellationToken()
    {
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ResolveAsync receives the cancelled token; the resolver substitute
        // doesn't honour cancellation by itself, but the controller passes the
        // token through. We assert the token was forwarded.
        await fixture.Controller.GetAsync(cts.Token);

        await fixture.Resolver.Received(1)
            .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), cts.Token);
    }

    // ────────────────────────────────────────────────────────────────────────
    // PUT / — AC2 + AC3
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Put_ValidPayload_UpdatesExistingSettingsNode_SavesAndPublishes()
    {
        // AC2 — when a Settings node exists, PUT updates its property values
        // and publishes. Save+Publish triggers Umbraco's normal cache-refresher
        // pipeline → Story 3.1's handler clears llms:settings: implicitly.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());
        var existingContent = fixture.WithExistingSettingsContent();
        fixture.WithSettingsRootNode();
        fixture.ContentService.Publish(Arg.Any<IContent>(), Arg.Any<string[]>())
            .Returns(SuccessfulPublishResult(existingContent));

        var request = new LlmsSettingsUpdateRequest(
            SiteName: "New Name",
            SiteSummary: "New Summary",
            ExcludedDoctypeAliases: new[] { "blogPost", "errorPage" });

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());

        existingContent.Received(1).SetValue("siteName", "New Name");
        existingContent.Received(1).SetValue("siteSummary", "New Summary");
        existingContent.Received(1).SetValue("excludedDoctypeAliases", "blogPost\nerrorPage");

        fixture.ContentService.Received(1).Save(existingContent);
        fixture.ContentService.Received(1).Publish(existingContent, Arg.Is<string[]>(arr => arr.Length == 1 && arr[0] == "*"));
    }

    [Test]
    public async Task Put_NoSettingsNode_CreatesAndPublishes()
    {
        // AC2 — first-write convenience: when no Settings node exists yet but
        // the doctype IS installed, PUT creates a new Settings node at root.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());
        fixture.WithRoots(/* none initially */);
        var contentType = Substitute.For<IContentType>();
        contentType.Alias.Returns("aiVisibilitySettings");
        fixture.ContentTypeService.Get("aiVisibilitySettings").Returns(contentType);
        var newContent = Substitute.For<IContent>();
        newContent.Key.Returns(SettingsNodeKey);
        fixture.ContentService.Create("AI Visibility Settings", parentId: -1, "aiVisibilitySettings")
            .Returns(newContent);
        fixture.ContentService.Publish(Arg.Any<IContent>(), Arg.Any<string[]>())
            .Returns(SuccessfulPublishResult(newContent));

        var request = new LlmsSettingsUpdateRequest(
            SiteName: "Bootstrap",
            SiteSummary: null,
            ExcludedDoctypeAliases: Array.Empty<string>());

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        fixture.ContentService.Received(1).Create("AI Visibility Settings", -1, "aiVisibilitySettings");
        fixture.ContentService.Received(1).Save(newContent);
        fixture.ContentService.Received(1).Publish(newContent, Arg.Any<string[]>());
    }

    [Test]
    public async Task Put_NoDoctypeInstalled_Returns400_DoesNotSave()
    {
        // AC2 — when the doctype isn't installed (e.g. uSync skip flag set
        // and the schema hasn't been imported yet), the controller surfaces a
        // 400 instead of throwing; the editor's UX is "install the doctype first".
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());
        fixture.WithRoots(/* none */);
        fixture.ContentTypeService.Get("aiVisibilitySettings").Returns((IContentType?)null);

        var request = new LlmsSettingsUpdateRequest("x", null, Array.Empty<string>());

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
        fixture.ContentService.DidNotReceive().Publish(Arg.Any<IContent>(), Arg.Any<string[]>());
    }

    [Test]
    public async Task Put_SummaryOver500Chars_Returns400_DoesNotSave()
    {
        // AC3 — server-side defence: 600-char summary returns 400, no writes.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());

        var request = new LlmsSettingsUpdateRequest(
            SiteName: "ok",
            SiteSummary: new string('x', 501),
            ExcludedDoctypeAliases: Array.Empty<string>());

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
    }

    [Test]
    public async Task Put_EmptyAliasInList_Returns400_DoesNotSave()
    {
        // AC3 — whitespace-only alias entries are rejected.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());

        var request = new LlmsSettingsUpdateRequest(
            SiteName: null,
            SiteSummary: null,
            ExcludedDoctypeAliases: new[] { "blogPost", "   " });

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
    }

    [Test]
    public async Task Put_DuplicateAliasInList_Returns400_DoesNotSave()
    {
        // AC3 — case-insensitive duplicates are rejected.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());

        var request = new LlmsSettingsUpdateRequest(
            SiteName: null,
            SiteSummary: null,
            ExcludedDoctypeAliases: new[] { "BlogPost", "blogpost" });

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
    }

    [Test]
    public async Task Put_NullRequestBody_Returns400()
    {
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());

        var result = await fixture.Controller.PutAsync(null, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Put_PublishReturnsFailure_Returns400_LogsWarning()
    {
        // Failure & Edge Cases — IContentService.Publish returning failure
        // (e.g. user permission denial on the Settings node) surfaces as 400
        // with a friendly message, not 500.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());
        var content = fixture.WithExistingSettingsContent();
        fixture.WithSettingsRootNode();
        fixture.ContentService.Publish(Arg.Any<IContent>(), Arg.Any<string[]>())
            .Returns(FailedPublishResult(content));

        var request = new LlmsSettingsUpdateRequest("ok", null, Array.Empty<string>());

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Put_ValidPayload_TrimsAliasesAndPreservesNameSummaryWhitespace()
    {
        // AC3 — the controller trims whitespace from each alias entry before
        // persisting (and dedupes via OrdinalIgnoreCase on the trimmed form).
        // siteName / siteSummary are persisted verbatim — adopters may
        // legitimately want leading/trailing whitespace (e.g. trailing newline
        // in summary). Pins all three contracts in one fixture so a future
        // refactor can't quietly drift any of them.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());
        var content = fixture.WithExistingSettingsContent();
        fixture.WithSettingsRootNode();
        fixture.ContentService.Publish(Arg.Any<IContent>(), Arg.Any<string[]>())
            .Returns(SuccessfulPublishResult(content));

        var request = new LlmsSettingsUpdateRequest(
            SiteName: "  Spaced  ",
            SiteSummary: "  Summary  ",
            ExcludedDoctypeAliases: new[] { "  blogPost  ", "errorPage" });

        await fixture.Controller.PutAsync(request, CancellationToken.None);

        // Aliases trimmed (and joined with \n for the resolver's parser).
        content.Received(1).SetValue("excludedDoctypeAliases", "blogPost\nerrorPage");
        // siteName / siteSummary preserved with their whitespace intact.
        content.Received(1).SetValue("siteName", "  Spaced  ");
        content.Received(1).SetValue("siteSummary", "  Summary  ");
    }

    [Test]
    public async Task Put_AliasContainingSeparatorChar_Returns400_DoesNotSave()
    {
        // AC3 / Failure & Edge Cases — the resolver splits the persisted
        // textarea on \n, \r, comma, semicolon. An unescaped separator inside
        // an alias entry would round-trip as multiple aliases (or zero, when
        // the entry IS the separator). Reject at the validation boundary.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());

        var request = new LlmsSettingsUpdateRequest(
            SiteName: null,
            SiteSummary: null,
            ExcludedDoctypeAliases: new[] { "blog,Post" });

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
    }

    [Test]
    public async Task Put_SiteNameOver255Chars_Returns400_DoesNotSave()
    {
        // Bound siteName length symmetrically with siteSummary so a hostile
        // or buggy client cannot inflate the persisted Settings node.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());

        var request = new LlmsSettingsUpdateRequest(
            SiteName: new string('x', 256),
            SiteSummary: null,
            ExcludedDoctypeAliases: Array.Empty<string>());

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
    }

    [Test]
    public async Task Put_TooManyAliases_Returns400_DoesNotSave()
    {
        // Bound the alias array length so the resolver's split-on-every-call
        // work is bounded (1024 entries).
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());

        var aliases = Enumerable.Range(0, 1025).Select(i => $"alias{i}").ToArray();
        var request = new LlmsSettingsUpdateRequest(null, null, aliases);

        var result = await fixture.Controller.PutAsync(request, CancellationToken.None);

        AssertProblemDetailsWithStatus(result, StatusCodes.Status400BadRequest);
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
    }

    [Test]
    public void Put_HonoursCancellationToken_ThrowsOperationCanceled()
    {
        // PUT respects request cancellation BEFORE Save runs. Pre-cancelled
        // token surfaces as OperationCanceledException — ASP.NET Core maps
        // that to a 499 / aborted-request shape upstream of the controller.
        var fixture = new ControllerFixture();
        fixture.WithResolverReturning(EmptyResolved());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new LlmsSettingsUpdateRequest("ok", null, Array.Empty<string>());

        Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Controller.PutAsync(request, cts.Token));
        fixture.ContentService.DidNotReceive().Save(Arg.Any<IContent>());
        fixture.ContentService.DidNotReceive().Publish(Arg.Any<IContent>(), Arg.Any<string[]>());
    }

    // ────────────────────────────────────────────────────────────────────────
    // GET /doctypes — AC4
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetDoctypes_FiltersElementTypes_AndSettingsDoctype_AndSortsByName()
    {
        var fixture = new ControllerFixture();
        var blog = StubContentType("blogPost", "Blog Post", isElement: false, "icon-document");
        var home = StubContentType("homePage", "Home Page", isElement: false, "icon-home");
        var settingsCt = StubContentType("aiVisibilitySettings", "AI Visibility Settings", isElement: false, null);
        var composition = StubContentType("llmsTxtSettingsComposition", "AI Visibility Exclusion", isElement: true, null);
        fixture.ContentTypeService.GetAll().Returns(new[] { settingsCt, blog, composition, home });

        var result = fixture.Controller.GetDoctypes(CancellationToken.None);

        var list = ((OkObjectResult)result).Value as IReadOnlyList<LlmsDoctypeViewModel>;
        Assert.That(list, Is.Not.Null);
        Assert.That(list!.Select(d => d.Alias), Is.EqualTo(new[] { "blogPost", "homePage" }),
            "settings doctype + element compositions filtered out, remainder sorted by Name");
    }

    [Test]
    public void GetDoctypes_GreenfieldSite_ReturnsEmptyList()
    {
        var fixture = new ControllerFixture();
        fixture.ContentTypeService.GetAll().Returns(Array.Empty<IContentType>());

        var result = fixture.Controller.GetDoctypes(CancellationToken.None);

        var list = ((OkObjectResult)result).Value as IReadOnlyList<LlmsDoctypeViewModel>;
        Assert.That(list, Is.Empty);
    }

    [Test]
    public void GetDoctypes_NullFromService_ReturnsEmptyList()
    {
        // Defensive: IContentTypeService.GetAll() returning null (NSubstitute
        // default for a method that returns IEnumerable<T>) shouldn't NPE.
        var fixture = new ControllerFixture();
        fixture.ContentTypeService.GetAll().Returns((IEnumerable<IContentType>?)null);

        var result = fixture.Controller.GetDoctypes(CancellationToken.None);

        var list = ((OkObjectResult)result).Value as IReadOnlyList<LlmsDoctypeViewModel>;
        Assert.That(list, Is.Empty);
    }

    // ────────────────────────────────────────────────────────────────────────
    // GET /excluded-pages — AC5
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetExcludedPages_FiltersByExcludeFlag_AndPaginates()
    {
        var fixture = new ControllerFixture();
        var excludedHome = BuildPublishedPage(HomeNodeKey, "Home", "homePage", excludeFromLlmExports: true);
        var notExcludedAbout = BuildPublishedPage(AboutNodeKey, "About", "aboutPage", excludeFromLlmExports: false);
        fixture.WithRoots(excludedHome, notExcludedAbout);
        fixture.PublishedUrlProvider.GetUrl(excludedHome, Arg.Any<UrlMode>(), Arg.Any<string?>(), Arg.Any<Uri?>()).Returns("/home");

        var result = fixture.Controller.GetExcludedPages(skip: 0, take: 100, CancellationToken.None);

        var page = ((OkObjectResult)result).Value as LlmsExcludedPagesPageViewModel;
        Assert.That(page, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(page!.Total, Is.EqualTo(1));
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].Key, Is.EqualTo(HomeNodeKey));
            Assert.That(page.Items[0].ContentTypeAlias, Is.EqualTo("homePage"));
        });
    }

    [Test]
    public void GetExcludedPages_LegacyStringValue_TreatedAsNotExcluded()
    {
        // Story 3.1 Failure & Edge Cases — string-typed legacy data falls
        // through as not-excluded (the flag is properly bool in v17 schema,
        // but a legacy migration may have left strings).
        var fixture = new ControllerFixture();
        var page = Substitute.For<IPublishedContent>();
        page.Key.Returns(HomeNodeKey);
        page.Name.Returns("Home");
        var ct = Substitute.For<IPublishedContentType>();
        ct.Alias.Returns("homePage");
        page.ContentType.Returns(ct);
        var prop = Substitute.For<IPublishedProperty>();
        prop.GetValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns("true"); // STRING, not bool
        page.GetProperty("excludeFromLlmExports").Returns(prop);
        fixture.WithRoots(page);

        var result = fixture.Controller.GetExcludedPages(skip: 0, take: 100, CancellationToken.None);

        var pageVm = ((OkObjectResult)result).Value as LlmsExcludedPagesPageViewModel;
        Assert.That(pageVm!.Total, Is.EqualTo(0),
            "string-typed value on legacy doctype must NOT be treated as excluded (defensive cast)");
    }

    [Test]
    public void GetExcludedPages_TakeClampedTo200()
    {
        var fixture = new ControllerFixture();
        fixture.WithRoots(/* none */);

        var result = fixture.Controller.GetExcludedPages(skip: 0, take: 9999, CancellationToken.None);

        var page = ((OkObjectResult)result).Value as LlmsExcludedPagesPageViewModel;
        Assert.That(page!.Take, Is.EqualTo(200), "take clamped to upper bound 200");
    }

    [Test]
    public void GetExcludedPages_TakeClampedToOne()
    {
        var fixture = new ControllerFixture();
        fixture.WithRoots(/* none */);

        var result = fixture.Controller.GetExcludedPages(skip: 0, take: 0, CancellationToken.None);

        var page = ((OkObjectResult)result).Value as LlmsExcludedPagesPageViewModel;
        Assert.That(page!.Take, Is.EqualTo(1), "take clamped to lower bound 1");
    }

    [Test]
    public void GetExcludedPages_NegativeSkip_ClampedToZero()
    {
        var fixture = new ControllerFixture();
        fixture.WithRoots(/* none */);

        var result = fixture.Controller.GetExcludedPages(skip: -5, take: 50, CancellationToken.None);

        var page = ((OkObjectResult)result).Value as LlmsExcludedPagesPageViewModel;
        Assert.That(page!.Skip, Is.EqualTo(0), "negative skip clamped to 0");
    }

    [Test]
    public void GetExcludedPages_NoUmbracoContext_ReturnsEmpty()
    {
        var fixture = new ControllerFixture();
        fixture.UmbracoContextAccessor
            .TryGetUmbracoContext(out Arg.Any<IUmbracoContext?>())
            .Returns(call =>
            {
                call[0] = null;
                return false;
            });

        var result = fixture.Controller.GetExcludedPages(skip: 0, take: 100, CancellationToken.None);

        var page = ((OkObjectResult)result).Value as LlmsExcludedPagesPageViewModel;
        Assert.That(page!.Total, Is.EqualTo(0));
        Assert.That(page.Items, Is.Empty);
    }

    [Test]
    public void GetExcludedPages_NoNavigationRootKeys_ReturnsEmpty()
    {
        var fixture = new ControllerFixture();
        fixture.Navigation
            .TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>())
            .Returns(call =>
            {
                call[0] = Array.Empty<Guid>();
                return false;
            });

        var result = fixture.Controller.GetExcludedPages(skip: 0, take: 100, CancellationToken.None);

        var page = ((OkObjectResult)result).Value as LlmsExcludedPagesPageViewModel;
        Assert.That(page!.Total, Is.EqualTo(0));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Authorization metadata — AC1 / AC9
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void Controller_HasSectionAccessSettingsAuthorizePolicy()
    {
        // AC1 + AC9 — the controller is gated behind SectionAccessSettings.
        // ManagementApiControllerBase + ApiController also carry [Authorize]
        // attributes, so we look only at OUR class's own declarations.
        // Relaxed from "exactly one" to "at least one with the right policy"
        // so a future framework upgrade adding a parallel attribute (e.g. via
        // source generators) doesn't fail this test for benign reasons.
        var attrs = typeof(SettingsManagementApiController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .ToArray();

        Assert.That(attrs, Is.Not.Empty, "controller must declare at least one [Authorize] attribute");
        Assert.That(attrs.Select(a => a.Policy),
            Has.Some.EqualTo("SectionAccessSettings"),
            "at least one [Authorize] must use the SectionAccessSettings policy (AC9)");
    }

    [Test]
    public void Get_WithoutBearerToken_Returns401()
    {
        // AC1 + Spike 0.B locked decision #11 — pin the contract that cookie-
        // only fetches return HTTP 401 (NOT 200, NOT 403). The runtime path
        // was end-to-end verified at manual-gate Step 5 against a real
        // Backoffice TestSite. At unit-test scope the guarantee is that the
        // controller carries the SectionAccessSettings policy — Umbraco's
        // Management API auth pipeline maps that policy's challenge semantics
        // to an HTTP 401 when no bearer token is presented.
        var attrs = typeof(SettingsManagementApiController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();

        Assert.That(attrs.Select(a => a.Policy),
            Has.Some.EqualTo(global::Umbraco.Cms.Web.Common.Authorization.AuthorizationPolicies.SectionAccessSettings),
            "policy must trigger Umbraco's bearer-token challenge → HTTP 401 on missing token");
    }

    [Test]
    public void Get_WithoutSettingsAccess_Returns403()
    {
        // AC9 — pin the contract that an authenticated user without
        // Settings-section access receives HTTP 403 (NOT 401, NOT 200) when
        // hitting the Settings Management API. The runtime path was end-to-
        // end verified at manual-gate Step 9. At unit-test scope the
        // guarantee is the same SectionAccessSettings policy attribute —
        // Umbraco's auth pipeline maps a policy-fail-after-auth-success to
        // HTTP 403 (forbid semantics), distinct from the 401 challenge path.
        var attrs = typeof(SettingsManagementApiController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();

        Assert.That(attrs.Select(a => a.Policy),
            Has.Some.EqualTo(global::Umbraco.Cms.Web.Common.Authorization.AuthorizationPolicies.SectionAccessSettings),
            "policy must surface as HTTP 403 when user is authenticated but lacks Settings-section access");
    }

    [Test]
    public void Controller_HasVersionedApiBackOfficeRouteWithAivisibilitySettingsTemplate()
    {
        // Story 6.0c (post-rename) — route prefix MUST be "aivisibility/settings".
        // The Spike 0.B locked decision #5 originally pinned "llmstxt/settings";
        // Story 6.0c flipped both production + this assertion in lockstep.
        // VersionedApiBackOfficeRoute may also be inherited; assert the OWN-declared
        // attribute matches the expected template.
        var routeAttr = typeof(SettingsManagementApiController)
            .GetCustomAttributes(typeof(global::Umbraco.Cms.Api.Management.Routing.VersionedApiBackOfficeRouteAttribute), inherit: false)
            .Cast<global::Umbraco.Cms.Api.Management.Routing.VersionedApiBackOfficeRouteAttribute>()
            .Single();
        // The framework expands the template into a full route prefix that
        // ends in our suffix; assert end-with so the test stays stable across
        // framework changes to the surrounding prefix.
        Assert.That(routeAttr.Template, Does.EndWith("aivisibility/settings"),
            "VersionedApiBackOfficeRoute suffix MUST be 'aivisibility/settings' (Story 6.0c renamed from 'llmstxt/settings')");
    }

    [Test]
    public void Controller_HasMapToApiAttribute_PointingAtConstantsApiName()
    {
        var attr = typeof(SettingsManagementApiController)
            .GetCustomAttributes(typeof(global::Umbraco.Cms.Api.Common.Attributes.MapToApiAttribute), inherit: true)
            .Cast<global::Umbraco.Cms.Api.Common.Attributes.MapToApiAttribute>()
            .Single();
        var apiName = typeof(global::Umbraco.Cms.Api.Common.Attributes.MapToApiAttribute)
            .GetProperty("ApiName")?.GetValue(attr) as string;
        Assert.That(apiName, Is.EqualTo(global::Umbraco.Community.AiVisibility.Constants.ApiName),
            "MapToApi must point at Constants.ApiName so the action lands in the existing Swagger doc");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static ResolvedLlmsSettings EmptyResolved()
        => new(
            SiteName: null,
            SiteSummary: null,
            ExcludedDoctypeAliases: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            BaseSettings: new AiVisibilitySettings());

    private static IContentType StubContentType(string alias, string name, bool isElement, string? icon)
    {
        var ct = Substitute.For<IContentType>();
        ct.Alias.Returns(alias);
        ct.Name.Returns(name);
        ct.IsElement.Returns(isElement);
        ct.Icon.Returns(icon);
        return ct;
    }

    private static IPublishedContent BuildPublishedPage(
        Guid key,
        string name,
        string contentTypeAlias,
        bool excludeFromLlmExports)
    {
        var page = Substitute.For<IPublishedContent>();
        page.Key.Returns(key);
        page.Name.Returns(name);
        var ct = Substitute.For<IPublishedContentType>();
        ct.Alias.Returns(contentTypeAlias);
        page.ContentType.Returns(ct);

        if (excludeFromLlmExports)
        {
            var prop = Substitute.For<IPublishedProperty>();
            prop.GetValue(Arg.Any<string?>(), Arg.Any<string?>()).Returns(true);
            page.GetProperty("excludeFromLlmExports").Returns(prop);
        }
        else
        {
            page.GetProperty(Arg.Any<string>()).Returns((IPublishedProperty?)null);
        }

        return page;
    }

    private static PublishResult SuccessfulPublishResult(IContent content)
        => new(PublishResultType.SuccessPublish, eventMessages: new EventMessages(), content);

    private static PublishResult FailedPublishResult(IContent content)
        => new(PublishResultType.FailedPublishCancelledByEvent, eventMessages: new EventMessages(), content);

    private static void AssertProblemDetailsWithStatus(IActionResult result, int expectedStatus)
    {
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var obj = (ObjectResult)result;
        Assert.That(obj.StatusCode, Is.EqualTo(expectedStatus));
        Assert.That(obj.Value, Is.InstanceOf<ProblemDetails>());
    }

    /// <summary>
    /// Stitches together the controller dependency graph as NSubstitute fakes
    /// (mirrors <c>MarkdownControllerTests.MakeController</c> shape — a single
    /// fixture object per test that exposes the substitutes for assertions).
    /// </summary>
    private sealed class ControllerFixture
    {
        public ISettingsResolver Resolver { get; } = Substitute.For<ISettingsResolver>();
        public IContentService ContentService { get; } = Substitute.For<IContentService>();
        public IContentTypeService ContentTypeService { get; } = Substitute.For<IContentTypeService>();
        public IUmbracoContextAccessor UmbracoContextAccessor { get; } = Substitute.For<IUmbracoContextAccessor>();
        public IDocumentNavigationQueryService Navigation { get; } = Substitute.For<IDocumentNavigationQueryService>();
        public IPublishedUrlProvider PublishedUrlProvider { get; } = Substitute.For<IPublishedUrlProvider>();
        public IPublishedContentCache PublishedCache { get; } = Substitute.For<IPublishedContentCache>();
        public IUmbracoContext UmbracoContext { get; } = Substitute.For<IUmbracoContext>();

        public SettingsManagementApiController Controller { get; }

        public ControllerFixture()
        {
            UmbracoContext.Content.Returns(PublishedCache);
            UmbracoContextAccessor
                .TryGetUmbracoContext(out Arg.Any<IUmbracoContext?>())
                .Returns(call =>
                {
                    call[0] = UmbracoContext;
                    return true;
                });
            Navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>())
                .Returns(call =>
                {
                    call[0] = Array.Empty<Guid>();
                    return true;
                });
            Navigation.TryGetDescendantsKeys(Arg.Any<Guid>(), out Arg.Any<IEnumerable<Guid>>())
                .Returns(call =>
                {
                    call[1] = Array.Empty<Guid>();
                    return true;
                });
            ContentTypeService.GetAll().Returns(Array.Empty<IContentType>());

            Controller = new SettingsManagementApiController(
                Resolver,
                ContentService,
                ContentTypeService,
                UmbracoContextAccessor,
                Navigation,
                PublishedUrlProvider,
                NullLogger<SettingsManagementApiController>.Instance);
            Controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            };
            Controller.ControllerContext.HttpContext.Request.Scheme = "https";
            Controller.ControllerContext.HttpContext.Request.Host = new HostString("example.test");
        }

        public void WithResolverReturning(ResolvedLlmsSettings resolved)
        {
            Resolver
                .ResolveAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(resolved));
        }

        /// <summary>
        /// Sets the published-cache root list. CALLED MORE THAN ONCE PER TEST
        /// REPLACES THE PRIOR LIST — the second call's roots win, the first's
        /// are dropped. Use a single <c>WithRoots(...)</c> per test (or compose
        /// the desired roots into one call) so test ordering doesn't matter.
        /// </summary>
        public void WithRoots(params IPublishedContent[] roots)
        {
            var keys = roots.Select(r => r.Key).ToArray();
            Navigation.TryGetRootKeys(out Arg.Any<IEnumerable<Guid>>())
                .Returns(call =>
                {
                    call[0] = keys;
                    return true;
                });
            foreach (var root in roots)
            {
                PublishedCache.GetById(root.Key).Returns(root);
            }
        }

        public void WithSettingsRootNode()
        {
            var settingsNode = Substitute.For<IPublishedContent>();
            settingsNode.Key.Returns(SettingsNodeKey);
            settingsNode.Name.Returns("AI Visibility Settings");
            var ct = Substitute.For<IPublishedContentType>();
            ct.Alias.Returns("aiVisibilitySettings");
            settingsNode.ContentType.Returns(ct);
            settingsNode.GetProperty(Arg.Any<string>()).Returns((IPublishedProperty?)null);
            WithRoots(settingsNode);
        }

        public IContent WithExistingSettingsContent()
        {
            var content = Substitute.For<IContent>();
            content.Key.Returns(SettingsNodeKey);
            ContentService.GetById(SettingsNodeKey).Returns(content);
            return content;
        }
    }
}
