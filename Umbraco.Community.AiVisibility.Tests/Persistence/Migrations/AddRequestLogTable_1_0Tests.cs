using Umbraco.Community.AiVisibility.Persistence.Migrations;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Install;

namespace Umbraco.Community.AiVisibility.Tests.Persistence.Migrations;

[TestFixture]
public class AddRequestLogTable_1_0Tests
{
    // Story 5.1 — migration's behaviour (idempotency + DDL emission) is
    // exercised end-to-end by manual gate Steps 1 & 2:
    //   1. Fresh DB → table created with documented columns + indexes
    //   2. Second boot on same DB → idempotent short-circuit + LogDebug
    //
    // Unit testing that behaviour requires either (a) a real Umbraco
    // migration test harness with an in-memory DB or (b) NSubstitute
    // against DatabaseSchemaCreator — the latter fails because the type's
    // public ctor signature is not Castle DynamicProxy-friendly (5+ deps,
    // some non-virtual). Story 3.1's CreateLlmsSettingsDoctypeTests
    // works because IContentTypeService is an interface; DDL migrations
    // don't have an equivalent service-mediated seam.
    //
    // Spec Drift Note (Story 5.1) captures this — the idempotency gate
    // lives at the manual gate per project-context.md "ceilings, not
    // floors" guidance + Story 4.2 § Other Dev Notes precedent
    // (RobotsAuditRefreshJob Spike-0.B-shaped gates deferred to manual).
    //
    // We retain the ctor-shape pin which verifies the DI surface (a
    // ctor-signature change would break Umbraco's migration plan
    // resolver).

    [Test]
    public void Constructor_RequiresIMigrationContextAndSchemaFactoryAndLogger()
    {
        // Pin the public DI surface: any ctor-signature change becomes a
        // visible test failure rather than a runtime DI-resolution surprise
        // when Umbraco's migration plan executor activates the type.
        var ctorParams = typeof(AddRequestLogTable_1_0)
            .GetConstructors()
            .Single()
            .GetParameters();

        Assert.Multiple(() =>
        {
            Assert.That(ctorParams, Has.Length.EqualTo(3));
            Assert.That(ctorParams[0].ParameterType, Is.EqualTo(typeof(IMigrationContext)));
            Assert.That(ctorParams[1].ParameterType, Is.EqualTo(typeof(DatabaseSchemaCreatorFactory)));
            Assert.That(ctorParams[2].ParameterType.IsGenericType, Is.True,
                "third parameter should be ILogger<AddRequestLogTable_1_0>");
            Assert.That(
                ctorParams[2].ParameterType.GetGenericTypeDefinition().Name,
                Is.EqualTo("ILogger`1"));
        });
    }

    [Test]
    public void Type_IsSealedPublicAndInheritsAsyncMigrationBase()
    {
        var t = typeof(AddRequestLogTable_1_0);
        Assert.Multiple(() =>
        {
            Assert.That(t.IsSealed, Is.True, "Migration should be sealed");
            Assert.That(t.IsPublic, Is.True, "Migration must be public for TypeLoader auto-discovery");
            Assert.That(t.BaseType, Is.EqualTo(typeof(AsyncMigrationBase)));
        });
    }

    [Test]
    public void TableNameConstant_MatchesArchitectureMdNaming()
    {
        // architecture.md line 495 + spec AC4: camelCase package-prefixed
        // table name. Pin so a typo/rename surfaces immediately.
        Assert.That(AddRequestLogTable_1_0.TableName, Is.EqualTo("llmsTxtRequestLog"));
    }
}
