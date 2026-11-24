namespace Umbraco.Community.AiVisibility.Extraction;

/// <summary>
/// Fixed target for the in-memory transport: nothing is dialled.
/// </summary>
internal sealed class InMemoryLoopbackUrlResolver : ILoopbackUrlResolver
{
    public static readonly InMemoryLoopbackUrlResolver Instance = new();

    private static readonly LoopbackTarget Target = new(new Uri("http://localhost/"), CertBypassEligible: false);

    public LoopbackTarget Resolve() => Target;
}
