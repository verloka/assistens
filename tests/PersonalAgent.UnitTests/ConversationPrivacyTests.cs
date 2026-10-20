using PersonalAgent.Core.Domain;

namespace PersonalAgent.UnitTests;

public sealed class ConversationPrivacyTests
{
    [Theory]
    [InlineData("api_key=hidden-value")]
    [InlineData("Authorization: Bearer hidden-value")]
    [InlineData("{\"client_secret\": \"hidden-value\"}")]
    [InlineData("password='hidden-value with spaces'")]
    [InlineData("sk-proj-hidden-value12345678")]
    [InlineData("123456:hidden-value01234567890123456789")]
    [InlineData("https://owner:hidden-value@example.test/path")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nhidden-value\n-----END PRIVATE KEY-----")]
    public void ConversationFiltersRecognizableCredentialsWithoutDiscardingIssue(string input)
    {
        var filtered = ConversationPrivacy.Filter("TEST-104 " + input);
        Assert.StartsWith("TEST-104 ", filtered);
        Assert.DoesNotContain("hidden-value", filtered);
        Assert.Equal(filtered, ConversationPrivacy.Filter(filtered));
    }

    [Fact]
    public void ConversationSourceFilterRetainsIssueAndRemovesCredentialParts()
    {
        var source = Assert.Single(ConversationPrivacy.FilterSources([new("TEST-104", new("https://owner:hidden-value@example.test/browse/TEST-104?access_token=hidden-value&issue=104"))]));
        Assert.Equal("example.test", source.Url.Host); Assert.Equal("/browse/TEST-104", source.Url.AbsolutePath);
        Assert.Empty(source.Url.UserInfo); Assert.DoesNotContain("hidden-value", source.Url.AbsoluteUri);
        Assert.Contains("issue=104", source.Url.Query); Assert.Equal("TEST-104", source.Title);
    }
}
