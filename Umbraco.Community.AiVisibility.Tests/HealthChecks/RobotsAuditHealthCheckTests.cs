using LlmsTxt.Umbraco.HealthChecks;
using Umbraco.Cms.Core.HealthChecks;

namespace LlmsTxt.Umbraco.Tests.HealthChecks;

/// <summary>
/// Story 4.2 — pinpoints the per-result <see cref="HealthCheckStatus"/>
/// conversion via the internal <c>BuildStatuses</c> entry point. Avoids
/// the IDomainService / IHttpContextAccessor wiring by exercising the
/// pure conversion shape.
/// </summary>
[TestFixture]
public class RobotsAuditHealthCheckTests
{
    private static AiBotEntry GptBotEntry => new(
        Token: "GPTBot",
        Category: BotCategory.Training,
        IsDeprecated: false,
        Operator: "OpenAI",
        DeprecationReplacement: null);

    private static AiBotEntry AnthropicAiEntry => new(
        Token: "anthropic-ai",
        Category: BotCategory.Training,
        IsDeprecated: true,
        Operator: "Anthropic",
        DeprecationReplacement: "ClaudeBot");

    private static AiBotEntry BytespiderEntry => new(
        Token: "Bytespider",
        Category: BotCategory.Training,
        IsDeprecated: false,
        Operator: "ByteDance",
        DeprecationReplacement: null,
        Notes: "Documented to ignore robots.txt; blocking in robots.txt may not be effective.");

    private static RobotsAuditFinding F(AiBotEntry bot, string matched, string suggested) =>
        new(bot, matched, suggested, bot.IsDeprecated);

    [Test]
    public void BuildStatuses_RobotsTxtMissing_InfoSeverity()
    {
        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.RobotsTxtMissing,
            Findings: Array.Empty<RobotsAuditFinding>(),
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(1));
            Assert.That(statuses[0].ResultType, Is.EqualTo(StatusResultType.Info));
            Assert.That(statuses[0].Message, Does.Contain("no /robots.txt"));
        });
    }

    [Test]
    public void BuildStatuses_NoAiBlocks_SuccessSeverity()
    {
        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.NoAiBlocks,
            Findings: Array.Empty<RobotsAuditFinding>(),
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(1));
            Assert.That(statuses[0].ResultType, Is.EqualTo(StatusResultType.Success));
            Assert.That(statuses[0].Message, Does.Contain("no AI crawler blocks"));
        });
    }

    [Test]
    public void BuildStatuses_FetchFailed_WarningSeverityCarriesError()
    {
        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.FetchFailed,
            Findings: Array.Empty<RobotsAuditFinding>(),
            CapturedAtUtc: DateTime.UtcNow,
            ErrorMessage: "connection refused");

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(1));
            Assert.That(statuses[0].ResultType, Is.EqualTo(StatusResultType.Warning));
            Assert.That(statuses[0].Description, Does.Contain("connection refused"));
        });
    }

    [Test]
    public void BuildStatuses_BlocksDetected_WarningSeverityWithMessageAndDescription()
    {
        // The Bellissima Health Check view renders Message via unsafeHTML and
        // does NOT render Description — so the per-bot breakdown lives in the
        // Message field as inline HTML. Description retains the Markdown form
        // for non-Backoffice consumers (e.g. email notifications).
        var finding = F(
            GptBotEntry,
            matched: "User-agent: GPTBot\nDisallow: /",
            suggested: "# Remove the following lines to allow GPTBot:\nUser-agent: GPTBot\nDisallow: /");

        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.BlocksDetected,
            Findings: new[] { finding },
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(1));
            Assert.That(statuses[0].ResultType, Is.EqualTo(StatusResultType.Warning));

            // Message carries the Backoffice-visible HTML breakdown
            Assert.That(statuses[0].Message, Does.Contain("GPTBot"));
            Assert.That(statuses[0].Message, Does.Contain("OpenAI"));
            Assert.That(statuses[0].Message, Does.Contain("Remove the following"));
            Assert.That(statuses[0].Message, Does.Contain("<details"),
                "details/summary disclosure for the suggested-removal block");

            // Description retains the Markdown form for non-Backoffice consumers
            Assert.That(statuses[0].Description, Does.Contain("GPTBot"));
            Assert.That(statuses[0].Description, Does.Contain("```"),
                "Markdown fenced code block in Description for email/notification renderers");
        });
    }

    [Test]
    public void BuildStatuses_DeprecatedToken_AnnotatesWithReplacement()
    {
        var finding = F(
            AnthropicAiEntry,
            matched: "User-agent: anthropic-ai\nDisallow: /",
            suggested: "anything");

        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.BlocksDetected,
            Findings: new[] { finding },
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.Multiple(() =>
        {
            // The deprecation annotation appears in BOTH the HTML Message and
            // the Markdown Description so adopters see it whether they're in
            // the Backoffice or reading email notifications.
            Assert.That(statuses[0].Message, Does.Contain("deprecated"));
            Assert.That(statuses[0].Message, Does.Contain("ClaudeBot"));
            Assert.That(statuses[0].Description, Does.Contain("deprecated"));
            Assert.That(statuses[0].Description, Does.Contain("ClaudeBot"));
        });
    }

    [Test]
    public void BuildStatuses_BytespiderInBlocks_AppendsCaveat()
    {
        var finding = F(
            BytespiderEntry,
            matched: "User-agent: Bytespider\nDisallow: /",
            suggested: "anything");

        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.BlocksDetected,
            Findings: new[] { finding },
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(2),
                "one Warning per category + one Info caveat");
            Assert.That(statuses.Last().ResultType, Is.EqualTo(StatusResultType.Info));
            Assert.That(statuses.Last().Message, Does.Contain("ignore robots.txt"),
                "caveat copy is in Message for Backoffice visibility (Bellissima only renders Message)");
            Assert.That(statuses.Last().Description, Does.Contain("ignore robots.txt"),
                "caveat copy also in Description for non-Backoffice consumers");
            Assert.That(statuses.Last().Message, Does.Contain("Bytespider"),
                "caveat lists the matched non-compliant tokens");
        });
    }

    [Test]
    public void BuildStatuses_UnknownCategory_InfoSeverity()
    {
        // Spec § Other Dev Notes: Unknown findings surface as "unclassified"
        // with Info severity (not Warning) — they're a curated-map gap, not
        // a deliberate block worth alerting on.
        var unknown = new AiBotEntry(
            Token: "BrandNewBot",
            Category: BotCategory.Unknown,
            IsDeprecated: false,
            Operator: null,
            DeprecationReplacement: null);
        var finding = F(unknown, "User-agent: BrandNewBot\nDisallow: /", "x");

        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.BlocksDetected,
            Findings: new[] { finding },
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);
        Assert.Multiple(() =>
        {
            Assert.That(statuses, Has.Count.EqualTo(1));
            Assert.That(statuses[0].ResultType, Is.EqualTo(StatusResultType.Info));
            Assert.That(statuses[0].Message, Does.Contain("unclassified"));
        });
    }

    [Test]
    public void BuildStatuses_HostileFields_AreAllHtmlEncoded()
    {
        // Defensive: every adopter / data-driven string interpolated into
        // Message HTML MUST be HtmlEncoded — Operator, Token, DeprecationReplacement,
        // SuggestedRemoval, and Hostname all flow through unsafeHTML on the
        // Bellissima Health Check view.
        const string xss = "<script>alert('xss')</script>";
        var hostile = new AiBotEntry(
            Token: $"Token{xss}",
            Category: BotCategory.Training,
            IsDeprecated: true,
            Operator: $"Op{xss}",
            DeprecationReplacement: $"Modern{xss}");
        var finding = new RobotsAuditFinding(
            Bot: hostile,
            MatchedDirective: "User-agent: EvilBot\nDisallow: /",
            SuggestedRemoval: $"Suggest{xss}",
            IsDeprecated: true);

        var result = new RobotsAuditResult(
            Hostname: $"host{xss}",
            Outcome: RobotsAuditOutcome.BlocksDetected,
            Findings: new[] { finding },
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.Multiple(() =>
        {
            // The literal "<script>" string MUST never survive unencoded into
            // Message — it would execute under the Bellissima unsafeHTML render.
            Assert.That(statuses[0].Message, Does.Not.Contain(xss),
                "raw <script> must never appear in Message HTML");
            // The HTML-encoded form is rendered verbatim — pin one occurrence
            // per encoded field to confirm the encoder is reached for each.
            Assert.That(statuses[0].Message, Does.Contain("&lt;script&gt;"),
                "encoded form is what the Bellissima view renders");
            Assert.That(statuses[0].Message, Does.Contain($"Token&lt;script&gt;"),
                "Token field is encoded");
            Assert.That(statuses[0].Message, Does.Contain($"Op&lt;script&gt;"),
                "Operator field is encoded");
            Assert.That(statuses[0].Message, Does.Contain($"Modern&lt;script&gt;"),
                "DeprecationReplacement field is encoded");
            Assert.That(statuses[0].Message, Does.Contain($"Suggest&lt;script&gt;"),
                "SuggestedRemoval field is encoded");
            Assert.That(statuses[0].Message, Does.Contain($"host&lt;script&gt;"),
                "Hostname field is encoded");
        });
    }

    [Test]
    public void BuildStatuses_BlocksAcrossCategories_OneStatusPerCategory()
    {
        var trainingFinding = F(
            GptBotEntry,
            matched: "User-agent: GPTBot\nDisallow: /",
            suggested: "x");
        var searchFinding = F(
            new AiBotEntry(
                Token: "OAI-SearchBot",
                Category: BotCategory.SearchRetrieval,
                IsDeprecated: false,
                Operator: "OpenAI",
                DeprecationReplacement: null),
            matched: "User-agent: OAI-SearchBot\nDisallow: /",
            suggested: "y");

        var result = new RobotsAuditResult(
            Hostname: "example.com",
            Outcome: RobotsAuditOutcome.BlocksDetected,
            Findings: new[] { trainingFinding, searchFinding },
            CapturedAtUtc: DateTime.UtcNow);

        var statuses = RobotsAuditHealthCheck.BuildStatuses(result);

        Assert.That(statuses, Has.Count.EqualTo(2),
            "categories grouped — one HealthCheckStatus per category");
        Assert.That(statuses.All(s => s.ResultType == StatusResultType.Warning), Is.True);
    }
}
