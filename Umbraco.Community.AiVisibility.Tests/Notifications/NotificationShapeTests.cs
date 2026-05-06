using Umbraco.Community.AiVisibility.Notifications;
using Umbraco.Community.AiVisibility.Persistence;

namespace Umbraco.Community.AiVisibility.Tests.Notifications;

[TestFixture]
public class NotificationShapeTests
{
    // Pin: each notification is sealed POCO, all-args ctor, get-only
    // properties. Reflection-driven so a future "let's add a setter" or
    // "let's open the class" change surfaces as a test failure rather than
    // silently relaxing the framework contract.

    [Test]
    public void MarkdownPageRequestedNotification_PropertiesGetOnly_AndClassSealed()
    {
        var t = typeof(MarkdownPageRequestedNotification);
        Assert.Multiple(() =>
        {
            Assert.That(t.IsSealed, Is.True);
            foreach (var prop in t.GetProperties())
            {
                Assert.That(prop.CanRead, Is.True, $"{prop.Name} must be readable");
                Assert.That(prop.SetMethod, Is.Null,
                    $"{prop.Name} must NOT have a setter (notification properties are get-only, populated via ctor)");
            }
        });

        // Constructor takes the documented fields.
        var n = new MarkdownPageRequestedNotification(
            path: "/about",
            contentKey: Guid.NewGuid(),
            culture: "en-US",
            userAgentClassification: UserAgentClass.HumanBrowser,
            referrerHost: "google.com");
        Assert.Multiple(() =>
        {
            Assert.That(n.Path, Is.EqualTo("/about"));
            Assert.That(n.ContentKey, Is.Not.EqualTo(Guid.Empty));
            Assert.That(n.Culture, Is.EqualTo("en-US"));
            Assert.That(n.UserAgentClassification, Is.EqualTo(UserAgentClass.HumanBrowser));
            Assert.That(n.ReferrerHost, Is.EqualTo("google.com"));
        });
    }

    [Test]
    public void LlmsTxtRequestedNotification_PropertiesGetOnly_AndClassSealed()
    {
        var t = typeof(LlmsTxtRequestedNotification);
        Assert.Multiple(() =>
        {
            Assert.That(t.IsSealed, Is.True);
            foreach (var prop in t.GetProperties())
            {
                Assert.That(prop.SetMethod, Is.Null, $"{prop.Name} must be get-only (no setter)");
            }
        });

        var n = new LlmsTxtRequestedNotification(
            hostname: "example.com",
            culture: "en-US",
            userAgentClassification: UserAgentClass.AiTraining,
            referrerHost: null);
        Assert.That(n.Hostname, Is.EqualTo("example.com"));
        Assert.That(n.UserAgentClassification, Is.EqualTo(UserAgentClass.AiTraining));
    }

    [Test]
    public void LlmsFullTxtRequestedNotification_PropertiesGetOnly_AndClassSealed_AndBytesServedSet()
    {
        var t = typeof(LlmsFullTxtRequestedNotification);
        Assert.Multiple(() =>
        {
            Assert.That(t.IsSealed, Is.True);
            foreach (var prop in t.GetProperties())
            {
                Assert.That(prop.SetMethod, Is.Null, $"{prop.Name} must be get-only (no setter)");
            }
        });

        var n = new LlmsFullTxtRequestedNotification(
            hostname: "example.com",
            culture: "en-US",
            userAgentClassification: UserAgentClass.AiSearchRetrieval,
            referrerHost: null,
            bytesServed: 12_345);
        Assert.Multiple(() =>
        {
            Assert.That(n.BytesServed, Is.EqualTo(12_345));
            Assert.That(n.UserAgentClassification, Is.EqualTo(UserAgentClass.AiSearchRetrieval));
        });
    }
}
