using Microsoft.AspNetCore.Mvc;

namespace LlmsTxt.Umbraco.TestSite.Spikes.DistributedJob;

// SPIKE 0.B — read-only inspector for the spike's execution-log table.
// `GET /spikes/distributed-job/rows` returns every row inserted by
// `SpikeDistributedJob` so the AC4 two-instance run can be verified
// without opening a SQL client.
[ApiController]
[Route("spikes/distributed-job")]
public sealed class SpikeJobInspectorController : ControllerBase
{
    private readonly SpikeJobLogStore _store;

    public SpikeJobInspectorController(SpikeJobLogStore store)
    {
        _store = store;
    }

    [HttpGet("rows")]
    public IActionResult Rows()
    {
        IReadOnlyList<SpikeJobLogEntry> rows = _store.ReadAll();
        SpikeJobInspectorResponse response = new(
            Count: rows.Count,
            CurrentInstanceId: $"{Environment.MachineName}/{Environment.ProcessId}",
            Rows: rows
                .Select(r => new SpikeJobInspectorRow(
                    r.Id,
                    r.CycleSequence,
                    r.ExecutedAt.ToUniversalTime().ToString("O"),
                    r.InstanceId))
                .ToArray());
        return Ok(response);
    }
}

public sealed record SpikeJobInspectorResponse(
    int Count,
    string CurrentInstanceId,
    IReadOnlyList<SpikeJobInspectorRow> Rows);

public sealed record SpikeJobInspectorRow(
    int Id,
    long CycleSequence,
    string ExecutedAt,
    string InstanceId);
