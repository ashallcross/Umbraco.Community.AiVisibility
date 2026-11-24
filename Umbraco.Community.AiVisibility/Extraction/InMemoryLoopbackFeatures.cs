using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Collects status and headers, and runs <c>OnStarting</c>/<c>OnCompleted</c> like a server.
/// </summary>
internal sealed class InMemoryResponseFeature : IHttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _onStarting = new();
    private readonly List<(Func<object, Task> Callback, object State)> _onCompleted = new();

    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    public string? ReasonPhrase { get; set; }

    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

    public Stream Body { get; set; } = Stream.Null;

    public bool HasStarted { get; private set; }

    public void OnStarting(Func<object, Task> callback, object state)
    {
        if (HasStarted)
        {
            throw new InvalidOperationException("The response has already started.");
        }

        _onStarting.Add((callback, state));
    }

    public void OnCompleted(Func<object, Task> callback, object state)
        => _onCompleted.Add((callback, state));

    public async Task FireOnStartingAsync()
    {
        if (HasStarted)
        {
            return;
        }

        for (var i = _onStarting.Count - 1; i >= 0; i--)
        {
            await _onStarting[i].Callback(_onStarting[i].State).ConfigureAwait(false);
        }

        HasStarted = true;
    }

    public async Task FireOnCompletedAsync()
    {
        for (var i = _onCompleted.Count - 1; i >= 0; i--)
        {
            try
            {
                await _onCompleted[i].Callback(_onCompleted[i].State).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Like a server: keep going so the request scope is still disposed.
            }
        }
    }
}

/// <summary>
/// <see cref="RequestAborted"/> fires when the caller cancels or the page calls <c>Abort()</c>.
/// </summary>
internal sealed class InMemoryRequestLifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
{
    private readonly CancellationTokenSource _aborted;

    public InMemoryRequestLifetimeFeature(CancellationToken callerToken)
    {
        _aborted = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        RequestAborted = _aborted.Token;
    }

    public CancellationToken RequestAborted { get; set; }

    public void Abort() => _aborted.Cancel();

    public void Dispose() => _aborted.Dispose();
}
