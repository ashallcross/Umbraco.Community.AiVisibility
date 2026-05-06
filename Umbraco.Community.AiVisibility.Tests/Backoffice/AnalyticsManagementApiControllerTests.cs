using System.Reflection;
using Asp.Versioning;
using LlmsTxt.Umbraco;
using Umbraco.Community.AiVisibility.Configuration;
using Umbraco.Community.AiVisibility.Backoffice;
using Umbraco.Community.AiVisibility.Persistence;
using Umbraco.Community.AiVisibility.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NPoco;
using NSubstitute;
using Umbraco.Cms.Api.Common.Attributes;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace Umbraco.Community.AiVisibility.Tests.Backoffice;

/// <summary>
/// Story 5.2 — Management API controller tests for the AI Traffic dashboard.
/// Mirrors Story 3.2 <c>LlmsSettingsManagementApiControllerTests</c> shape:
/// auth-attribute reflection (3) + GetRequests happy/validation/filtering
/// (12) + GetClassifications (3) + GetSummary (2) + GetRetention (1) +
/// auth surface (2) + cancellation (1) = 24 tests targeting AC1–AC4 + AC11.
/// </summary>
[TestFixture]
public class LlmsAnalyticsManagementApiControllerTests
{
    private static readonly DateTime FixedNowUtc = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);

    private static IOptionsMonitor<AiVisibilitySettings> SettingsMonitor(AiVisibilitySettings? value = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<AiVisibilitySettings>>();
        monitor.CurrentValue.Returns(value ?? new AiVisibilitySettings());
        return monitor;
    }

    /// <summary>
    /// Minimal stub TimeProvider returning a fixed UTC instant. Avoids the
    /// Microsoft.Extensions.TimeProvider.Testing NuGet (not in
    /// Directory.Packages.props); same dependency-minimisation shape Story 5.1
    /// LogRetentionJobTests uses.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static FixedTimeProvider FixedClock() =>
        new(new DateTimeOffset(FixedNowUtc, TimeSpan.Zero));

    private static (AnalyticsManagementApiController controller, IAnalyticsReader reader, FixedTimeProvider clock) NewController(
        AiVisibilitySettings? settings = null,
        IAnalyticsReader? reader = null)
    {
        var readerSub = reader ?? Substitute.For<IAnalyticsReader>();
        var clock = FixedClock();
        var controller = new AnalyticsManagementApiController(
            NullLogger<AnalyticsManagementApiController>.Instance,
            SettingsMonitor(settings),
            readerSub,
            clock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return (controller, readerSub, clock);
    }

    private static Page<RequestLogEntry> BuildPage(
        IReadOnlyList<RequestLogEntry> items,
        long totalItems,
        int pageSize,
        int currentPage = 1)
    {
        var totalPages = totalItems == 0 ? 0 : (long)Math.Ceiling((double)totalItems / pageSize);
        return new Page<RequestLogEntry>
        {
            CurrentPage = currentPage,
            ItemsPerPage = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            Items = items.ToList(),
        };
    }

    private static RequestLogEntry BuildRow(long id, DateTime createdUtc, string userAgentClass, string path = "/home")
        => new()
        {
            Id = (int)id,
            CreatedUtc = createdUtc,
            Path = path,
            ContentKey = null,
            Culture = "en-GB",
            UserAgentClass = userAgentClass,
            ReferrerHost = null,
        };

    // ─────────── 1. Auth-attribute reflection (3 tests — AC1, AC11) ───────────

    [Test]
    public void Controller_HasSectionAccessSettingsAuthorizePolicy()
    {
        // Story 3.2 SDN #2 precedent — inherit:false to assert OWN-declared.
        var attrs = typeof(AnalyticsManagementApiController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(attrs.Length, Is.EqualTo(1),
                "exactly one [Authorize] OWN-declared on the controller");
            Assert.That(attrs[0].Policy, Is.EqualTo(AuthorizationPolicies.SectionAccessSettings),
                "Policy must be SectionAccessSettings (architecture.md:376; Story 5.2 § Architectural drift entry 1)");
        });
    }

    [Test]
    public void Controller_HasMapToApiAttributeMatchingApiName()
    {
        var attr = typeof(AnalyticsManagementApiController)
            .GetCustomAttribute<MapToApiAttribute>(inherit: false);

        Assert.Multiple(() =>
        {
            Assert.That(attr, Is.Not.Null, "MapToApi attribute must be present");
            Assert.That(attr!.ApiName, Is.EqualTo(Constants.ApiName),
                "MapToApi.ApiName must equal Constants.ApiName so the operations land in the existing Swagger doc");
        });
    }

    [Test]
    public void Controller_HasVersionedApiBackOfficeRouteEndingWithLlmstxtAnalytics()
    {
        // Story 3.2 SDN #3 precedent — Does.EndWith because the framework
        // wraps the prefix at runtime (/umbraco/management/api/v1/...).
        var attr = typeof(AnalyticsManagementApiController)
            .GetCustomAttribute<VersionedApiBackOfficeRouteAttribute>(inherit: false);
        Assert.That(attr, Is.Not.Null);

        var template = attr!.Template;
        Assert.That(template, Does.EndWith("llmstxt/analytics"));

        var version = typeof(AnalyticsManagementApiController)
            .GetCustomAttribute<ApiVersionAttribute>(inherit: false);
        Assert.That(version, Is.Not.Null, "ApiVersion attribute must be present");
        Assert.That(version!.Versions.Single().ToString(), Is.EqualTo("1.0"));
    }

    // ─────────── 2. GetRequests happy path (4 tests — AC2) ───────────

    [Test]
    public void GetRequests_HappyPath_ReturnsPagedResults_OrderedDescByCreatedUtc()
    {
        var rows = new[]
        {
            BuildRow(3, FixedNowUtc.AddHours(-1), nameof(UserAgentClass.AiTraining)),
            BuildRow(2, FixedNowUtc.AddHours(-2), nameof(UserAgentClass.HumanBrowser)),
            BuildRow(1, FixedNowUtc.AddHours(-3), nameof(UserAgentClass.CrawlerOther)),
        };
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<int>(),
            Arg.Any<int>())
            .Returns(BuildPage(rows, 3, 50));

        var (controller, _, _) = NewController(reader: reader);

        var result = controller.GetRequests(null, null, null, null, null, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var body = ok!.Value as LlmsAnalyticsRequestPageViewModel;
        Assert.That(body, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(body!.Items, Has.Count.EqualTo(3));
            Assert.That(body.Items[0].Id, Is.EqualTo(3));
            Assert.That(body.Items[2].Id, Is.EqualTo(1));
            Assert.That(body.Total, Is.EqualTo(3));
            Assert.That(body.Page, Is.EqualTo(1));
            Assert.That(body.PageSize, Is.EqualTo(50));
            Assert.That(body.TotalCappedAt, Is.Null, "default MaxResultRows=10000; total=3 must NOT trigger cap");
        });
    }

    [Test]
    public void GetRequests_DefaultRange_LastSevenDays()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(Array.Empty<RequestLogEntry>(), 0, 50));

        var (controller, capturedReader, _) = NewController(reader: reader);
        controller.GetRequests(null, null, null, null, null, CancellationToken.None);

        capturedReader.Received(1).ReadRequestsPage(
            Arg.Is<DateTime>(d => d == FixedNowUtc.AddDays(-7)),
            Arg.Is<DateTime>(d => d == FixedNowUtc),
            Arg.Any<IReadOnlyList<string>>(),
            1,
            50);
    }

    [Test]
    public void GetRequests_RangeWithinBounds_NoClamping()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(Array.Empty<RequestLogEntry>(), 0, 50));

        var (controller, _, _) = NewController(reader: reader);
        var from = FixedNowUtc.AddDays(-30).ToString("O");
        var to = FixedNowUtc.ToString("O");
        controller.GetRequests(from, to, null, null, null, CancellationToken.None);

        Assert.That(
            controller.Response.Headers.ContainsKey("X-Llms-Range-Clamped"),
            Is.False,
            "30-day range stays under default 365 — no clamp header");
    }

    [Test]
    public void GetRequests_RangeExceedsMaxRangeDays_ClampsAndSetsHeader()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(Array.Empty<RequestLogEntry>(), 0, 50));

        var (controller, capturedReader, _) = NewController(reader: reader);
        var from = FixedNowUtc.AddDays(-400).ToString("O");
        var to = FixedNowUtc.ToString("O");
        var result = controller.GetRequests(from, to, null, null, null, CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsRequestPageViewModel;
        Assert.That(body, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(body!.RangeFrom, Is.EqualTo(FixedNowUtc.AddDays(-365)),
                "from clamped to to - 365 days");
            Assert.That(controller.Response.Headers["X-Llms-Range-Clamped"].ToString(),
                Is.EqualTo("true"));
        });

        capturedReader.Received(1).ReadRequestsPage(
            FixedNowUtc.AddDays(-365),
            FixedNowUtc,
            Arg.Any<IReadOnlyList<string>>(),
            1,
            50);
    }

    // ─────────── 3. GetRequests validation (5 tests — AC2) ───────────

    [Test]
    public void GetRequests_FromAfterTo_Returns400()
    {
        var (controller, _, _) = NewController();
        var from = FixedNowUtc.ToString("O");
        var to = FixedNowUtc.AddDays(-7).ToString("O");
        var result = controller.GetRequests(from, to, null, null, null, CancellationToken.None);

        var problem = (result as ObjectResult)?.Value as ProblemDetails;
        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(400));
            Assert.That(problem!.Detail, Does.Contain("to must be greater than from"));
        });
    }

    [Test]
    public void GetRequests_FromMissingTimezone_Returns400()
    {
        var (controller, _, _) = NewController();
        // No Z, no offset — bare ISO without timezone designator.
        var from = "2026-04-27T00:00:00";
        var to = FixedNowUtc.ToString("O");
        var result = controller.GetRequests(from, to, null, null, null, CancellationToken.None);

        var problem = (result as ObjectResult)?.Value as ProblemDetails;
        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(400));
            Assert.That(problem!.Title, Does.Contain("ISO-8601 UTC"));
        });
    }

    [Test]
    public void GetRequests_InvalidClass_Returns400_AndListsValidNames()
    {
        var (controller, _, _) = NewController();
        var result = controller.GetRequests(null, null, new[] { "NotAClass" }, null, null, CancellationToken.None);

        var problem = (result as ObjectResult)?.Value as ProblemDetails;
        Assert.Multiple(() =>
        {
            Assert.That((result as ObjectResult)?.StatusCode, Is.EqualTo(400));
            Assert.That(problem!.Detail, Does.Contain("'NotAClass'"));
            Assert.That(problem.Detail, Does.Contain("AiTraining"));
            Assert.That(problem.Detail, Does.Contain("HumanBrowser"));
            Assert.That(problem.Detail, Does.Contain("Unknown"));
        });
    }

    [Test]
    public void GetRequests_PageSizeAboveMax_ClampsToMax()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(Array.Empty<RequestLogEntry>(), 0, 200));

        var settings = new AiVisibilitySettings { Analytics = new AnalyticsSettings { MaxPageSize = 200 } };
        var (controller, capturedReader, _) = NewController(settings, reader);
        var result = controller.GetRequests(null, null, null, 1, 10000, CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsRequestPageViewModel;
        Assert.That(body!.PageSize, Is.EqualTo(200));
        capturedReader.Received(1).ReadRequestsPage(
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Any<IReadOnlyList<string>>(),
            1,
            200);
    }

    [Test]
    public void GetRequests_PageBelowOne_ClampsToOne()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(Array.Empty<RequestLogEntry>(), 0, 50));

        var (controller, capturedReader, _) = NewController(reader: reader);
        controller.GetRequests(null, null, null, 0, null, CancellationToken.None);
        controller.GetRequests(null, null, null, -5, null, CancellationToken.None);

        capturedReader.Received(2).ReadRequestsPage(
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Any<IReadOnlyList<string>>(),
            1,
            Arg.Any<int>());
    }

    // ─────────── 4. GetRequests filtering (3 tests — AC2) ───────────

    [Test]
    public void GetRequests_SingleClassFilter_PassesThroughToReader()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(Array.Empty<RequestLogEntry>(), 0, 50));

        var (controller, capturedReader, _) = NewController(reader: reader);
        controller.GetRequests(null, null, new[] { "aitraining" }, null, null, CancellationToken.None);

        capturedReader.Received(1).ReadRequestsPage(
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Is<IReadOnlyList<string>>(c => c.Count == 1 && c[0] == "AiTraining"),
            1,
            50);
    }

    [Test]
    public void GetRequests_MultipleClassFilter_PassesUnionToReader_Deduped()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(Array.Empty<RequestLogEntry>(), 0, 50));

        var (controller, capturedReader, _) = NewController(reader: reader);
        // Mixed case + duplicate — controller normalises to canonical PascalCase + dedupes.
        controller.GetRequests(null, null, new[] { "AiTraining", "aisearchretrieval", "AITRAINING" }, null, null, CancellationToken.None);

        capturedReader.Received(1).ReadRequestsPage(
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Is<IReadOnlyList<string>>(c =>
                c.Count == 2
                && c.Contains("AiTraining")
                && c.Contains("AiSearchRetrieval")),
            1,
            50);
    }

    [Test]
    public void GetRequests_TotalAboveMaxResultRows_ReturnsTotalCappedAt()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        // total 11000 > MaxResultRows default 10000 ⇒ totalCappedAt populated.
        var rows = Enumerable.Range(1, 50)
            .Select(i => BuildRow(i, FixedNowUtc.AddSeconds(-i), nameof(UserAgentClass.AiTraining)))
            .ToList();
        reader.ReadRequestsPage(default, default, default!, default, default)
            .ReturnsForAnyArgs(BuildPage(rows, 11000, 50));

        var (controller, _, _) = NewController(reader: reader);
        var result = controller.GetRequests(null, null, null, null, null, CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsRequestPageViewModel;
        Assert.Multiple(() =>
        {
            Assert.That(body!.Total, Is.EqualTo(11000), "total reflects true row count, not the cap");
            Assert.That(body.TotalCappedAt, Is.EqualTo(10000), "totalCappedAt populated when total > MaxResultRows");
        });
    }

    // ─────────── 5. GetClassifications (3 tests — AC3) ───────────

    [Test]
    public void GetClassifications_HappyPath_ReturnsCountsDescending()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadClassifications(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new List<AnalyticsClassificationRow>
            {
                new() { UserAgentClass = nameof(UserAgentClass.AiTraining), Count = 4 },
                new() { UserAgentClass = nameof(UserAgentClass.HumanBrowser), Count = 2 },
                new() { UserAgentClass = nameof(UserAgentClass.Unknown), Count = 1 },
            });

        var (controller, _, _) = NewController(reader: reader);
        var result = controller.GetClassifications(null, null, CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsClassificationViewModel[];
        Assert.That(body, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(body!.Length, Is.EqualTo(3));
            Assert.That(body[0].Class, Is.EqualTo("AiTraining"));
            Assert.That(body[0].Count, Is.EqualTo(4));
            Assert.That(body[2].Class, Is.EqualTo("Unknown"));
            Assert.That(body[2].Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void GetClassifications_NoRowsInRange_ReturnsEmptyArray()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadClassifications(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(Array.Empty<AnalyticsClassificationRow>());

        var (controller, _, _) = NewController(reader: reader);
        var result = controller.GetClassifications(null, null, CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsClassificationViewModel[];
        Assert.That(body, Is.Not.Null);
        Assert.That(body!, Is.Empty,
            "epic Failure case 4 — empty array hides all chips so editors never see zero-count chips");
    }

    [Test]
    public void GetClassifications_ReaderThrows_Returns503ProblemDetails()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadClassifications(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(_ => throw new InvalidOperationException("simulated DB failure"));

        var (controller, _, _) = NewController(reader: reader);
        var result = controller.GetClassifications(null, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            var obj = result as ObjectResult;
            Assert.That(obj?.StatusCode, Is.EqualTo(503));
            Assert.That(obj?.Value, Is.InstanceOf<ProblemDetails>());
        });
    }

    // ─────────── 6. GetSummary (2 tests — AC4) ───────────

    [Test]
    public void GetSummary_HappyPath_ReturnsAggregates()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadSummary(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new AnalyticsSummaryRow
            {
                TotalRequests = 47,
                FirstSeenUtc = FixedNowUtc.AddHours(-12),
                LastSeenUtc = FixedNowUtc.AddMinutes(-5),
            });

        var (controller, _, _) = NewController(reader: reader);
        var result = controller.GetSummary(null, null, CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsSummaryViewModel;
        Assert.Multiple(() =>
        {
            Assert.That(body!.TotalRequests, Is.EqualTo(47));
            Assert.That(body.FirstSeenUtc, Is.EqualTo(FixedNowUtc.AddHours(-12)));
            Assert.That(body.LastSeenUtc, Is.EqualTo(FixedNowUtc.AddMinutes(-5)));
        });
    }

    [Test]
    public void GetSummary_EmptyRange_ReturnsZeroAndNullTimestamps()
    {
        var reader = Substitute.For<IAnalyticsReader>();
        reader.ReadSummary(Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(new AnalyticsSummaryRow
            {
                TotalRequests = 0,
                FirstSeenUtc = null,
                LastSeenUtc = null,
            });

        var (controller, _, _) = NewController(reader: reader);
        var result = controller.GetSummary(null, null, CancellationToken.None);

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsSummaryViewModel;
        Assert.Multiple(() =>
        {
            Assert.That(body!.TotalRequests, Is.EqualTo(0));
            Assert.That(body.FirstSeenUtc, Is.Null);
            Assert.That(body.LastSeenUtc, Is.Null);
        });
    }

    // ─────────── 7. GetRetention (1 test — AC9) ───────────

    [Test]
    public void GetRetention_ReadsFromOptionsMonitor()
    {
        var settings = new AiVisibilitySettings
        {
            LogRetention = new LogRetentionSettings { DurationDays = 30 },
        };
        var (controller, _, _) = NewController(settings);
        var result = controller.GetRetention();

        var body = (result as OkObjectResult)?.Value as LlmsAnalyticsRetentionViewModel;
        Assert.That(body!.DurationDays, Is.EqualTo(30));
    }

    // ─────────── 8. Auth surface (2 tests — AC1, AC11) ───────────
    // The actual 401/403 enforcement is at the ASP.NET Core auth pipeline
    // (the [Authorize] attribute) — not testable in unit-isolation since
    // the pipeline runs OUTSIDE the controller.  The two tests below pin
    // the attribute reflection (already covered by the auth-attribute
    // reflection cluster above) AND assert the controller is sealed
    // (Story 3.2 + 5.1 precedent — sealed controllers prevent adopter
    // subclass-mutation of the auth surface).

    [Test]
    public void Controller_IsSealed_PreventsAdopterAuthSurfaceSubclassing()
    {
        Assert.That(typeof(AnalyticsManagementApiController).IsSealed, Is.True,
            "AC1 + AC11 — sealed prevents adopter subclasses bypassing the [Authorize(SectionAccessSettings)] gate via covariant return / hidden methods");
    }

    [Test]
    public void Controller_AllPublicActions_DeclareGetMethodAttribute()
    {
        // AC1 — every action is GET (read-only contract). Surface check;
        // changing this in a future story (e.g. POST /export) requires a
        // deliberate test update + spec amendment.
        var actionMethods = typeof(AnalyticsManagementApiController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.ReturnType == typeof(IActionResult))
            .ToArray();

        Assert.That(actionMethods, Is.Not.Empty);
        foreach (var m in actionMethods)
        {
            var get = m.GetCustomAttribute<HttpGetAttribute>(inherit: false);
            Assert.That(get, Is.Not.Null,
                $"Action '{m.Name}' must declare [HttpGet] — controller is read-only");
        }
    }

    // ─────────── 9. Cancellation (1 test — DoD line 5) ───────────

    [Test]
    public void GetRequests_CancellationTokenAlreadyCancelled_ThrowsOperationCanceledException()
    {
        var (controller, _, _) = NewController();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            controller.GetRequests(null, null, null, null, null, cts.Token));
    }

    // ─────────── 10. TryParseUtc helper (1 bonus pin — internal contract) ───────────
    // Pinning the timezone-strict parser separately so a future maintainer
    // can't relax `hasOffset` without the test surfacing the change.

    [Test]
    public void TryParseUtc_VariousInputs_AcceptsOnlyExplicitTimezoneOffsets()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AnalyticsManagementApiController.TryParseUtc("2026-05-04T00:00:00Z", out var z), Is.True);
            Assert.That(z.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(AnalyticsManagementApiController.TryParseUtc("2026-05-04T00:00:00+00:00", out _), Is.True);
            Assert.That(AnalyticsManagementApiController.TryParseUtc("2026-05-04T03:00:00+03:00", out var offset), Is.True);
            Assert.That(offset.Kind, Is.EqualTo(DateTimeKind.Utc), "offset-bearing input adjusted to UTC");
            Assert.That(AnalyticsManagementApiController.TryParseUtc("2026-05-04T00:00:00", out _), Is.False,
                "bare ISO without timezone designator rejected");
            Assert.That(AnalyticsManagementApiController.TryParseUtc("not-a-date", out _), Is.False);
            Assert.That(AnalyticsManagementApiController.TryParseUtc("", out _), Is.False);
        });
    }
}
