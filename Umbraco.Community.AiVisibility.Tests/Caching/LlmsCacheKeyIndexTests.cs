using LlmsTxt.Umbraco.Caching;

namespace LlmsTxt.Umbraco.Tests.Caching;

[TestFixture]
public class LlmsCacheKeyIndexTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private LlmsCacheKeyIndex _index = null!;

    [SetUp]
    public void Setup() => _index = new LlmsCacheKeyIndex();

    [Test]
    public void Register_AddsKey()
    {
        _index.Register(NodeA, "k1");
        Assert.That(_index.GetKeysFor(NodeA), Is.EquivalentTo(new[] { "k1" }));
    }

    [Test]
    public void Register_SameKeyTwice_StoresOnce()
    {
        _index.Register(NodeA, "k1");
        _index.Register(NodeA, "k1");
        Assert.That(_index.GetKeysFor(NodeA).Count, Is.EqualTo(1));
    }

    [Test]
    public void Register_TwoKeysSameNode_StoresBoth()
    {
        _index.Register(NodeA, "k1");
        _index.Register(NodeA, "k2");
        Assert.That(_index.GetKeysFor(NodeA), Is.EquivalentTo(new[] { "k1", "k2" }));
    }

    [Test]
    public void Register_TwoNodes_TrackedIndependently()
    {
        _index.Register(NodeA, "k1");
        _index.Register(NodeB, "k2");
        Assert.That(_index.GetKeysFor(NodeA), Is.EquivalentTo(new[] { "k1" }));
        Assert.That(_index.GetKeysFor(NodeB), Is.EquivalentTo(new[] { "k2" }));
    }

    [Test]
    public void Register_NullOrEmptyKey_NoOp()
    {
        _index.Register(NodeA, string.Empty);
        Assert.That(_index.GetKeysFor(NodeA), Is.Empty);
    }

    [Test]
    public void GetKeysFor_UnknownNode_ReturnsEmpty()
    {
        Assert.That(_index.GetKeysFor(NodeA), Is.Empty);
    }

    [Test]
    public void Remove_KnownNode_RemovesEntry()
    {
        _index.Register(NodeA, "k1");
        _index.Remove(NodeA);
        Assert.That(_index.GetKeysFor(NodeA), Is.Empty);
    }

    [Test]
    public void Remove_UnknownNode_NoOp()
    {
        Assert.DoesNotThrow(() => _index.Remove(NodeA));
    }

    [Test]
    public void Reset_ClearsAll()
    {
        _index.Register(NodeA, "k1");
        _index.Register(NodeB, "k2");
        _index.Reset();
        Assert.That(_index.GetKeysFor(NodeA), Is.Empty);
        Assert.That(_index.GetKeysFor(NodeB), Is.Empty);
    }

    [Test]
    public void GetKeysFor_ReturnsSnapshot_NotLiveView()
    {
        _index.Register(NodeA, "k1");
        var snapshot = _index.GetKeysFor(NodeA);
        _index.Register(NodeA, "k2");
        // The snapshot taken before the second Register call should still have one entry.
        Assert.That(snapshot.Count, Is.EqualTo(1));
        // The current state has both, of course.
        Assert.That(_index.GetKeysFor(NodeA).Count, Is.EqualTo(2));
    }

    [Test, Repeat(20)]
    public void Concurrent_Register_And_GetKeysFor_NoExceptions_AllRegistered()
    {
        // 1000 concurrent inserts on the same node + interleaved reads — assert
        // no InvalidOperationException from concurrent HashSet mutation, and that
        // every key landed.
        Parallel.For(0, 1000, i =>
        {
            _index.Register(NodeA, $"k{i}");
            _ = _index.GetKeysFor(NodeA);
        });

        Assert.That(_index.GetKeysFor(NodeA).Count, Is.EqualTo(1000));
    }
}
