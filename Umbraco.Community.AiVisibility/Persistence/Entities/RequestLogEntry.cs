using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace Umbraco.Community.AiVisibility.Persistence.Entities;

/// <summary>
/// Story 5.1 — NPoco entity backing the <c>llmsTxtRequestLog</c> table.
/// One row per successfully served Markdown / <c>/llms.txt</c> /
/// <c>/llms-full.txt</c> response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema-via-annotations:</b> the <see cref="AddRequestLogTable_1_0"/>
/// migration's <c>Create.Table&lt;RequestLogEntry&gt;().Do()</c> reads
/// the annotations on this class to issue the DDL. Annotation drift between
/// this class and the migration's expectation re-creates the table on next
/// migrate. <b>Migrations are immutable once shipped (AR7)</b>; schema
/// changes go in a NEW migration class.
/// </para>
/// <para>
/// <b>PII discipline (NFR11):</b> the columns capture path / content key /
/// culture / UA classification / referrer host ONLY. Cookies, tokens,
/// session IDs, query strings, full referrer paths are NEVER persisted.
/// </para>
/// </remarks>
[TableName("llmsTxtRequestLog")]
[PrimaryKey("id", AutoIncrement = true)]
[ExplicitColumns]
public sealed class RequestLogEntry
{
    [Column("id")]
    [PrimaryKeyColumn(AutoIncrement = true, IdentitySeed = 1)]
    public int Id { get; set; }

    [Column("createdUtc")]
    [Index(IndexTypes.NonClustered, Name = "IX_llmsTxtRequestLog_createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [Column("path")]
    [Length(2048)]
    public string Path { get; set; } = string.Empty;

    [Column("contentKey")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Index(IndexTypes.NonClustered, Name = "IX_llmsTxtRequestLog_contentKey")]
    public Guid? ContentKey { get; set; }

    /// <summary>
    /// Effective culture for the served response (e.g. <c>"en-GB"</c>).
    /// Non-null per AC4: when <see cref="MarkdownPageRequestedNotification"/>
    /// or its sibling notifications carry <c>null</c> culture (rare —
    /// hostname resolver produced no culture), the handler defaults to
    /// <see cref="string.Empty"/> rather than <c>NULL</c>. This keeps
    /// analytics queries simple (no <c>WHERE culture IS NULL</c> branch
    /// needed) and matches the same shape as
    /// <see cref="UserAgentClass"/>'s "Unknown" sentinel.
    /// </summary>
    [Column("culture")]
    [Length(16)]
    public string Culture { get; set; } = string.Empty;

    /// <summary>
    /// String name of the <see cref="UserAgentClass"/> enum member (e.g.
    /// <c>"HumanBrowser"</c>, <c>"AiTraining"</c>). Persisted as VARCHAR not
    /// INT so the integer ordering of the enum can evolve without
    /// invalidating stored rows.
    /// </summary>
    [Column("userAgentClass")]
    [Length(64)]
    public string UserAgentClass { get; set; } = nameof(Umbraco.Community.AiVisibility.Persistence.UserAgentClass.Unknown);

    [Column("referrerHost")]
    [Length(256)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? ReferrerHost { get; set; }
}
