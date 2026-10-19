using HealthMonitor.Infrastructure.AiQueries;

namespace HealthMonitor.UnitTests.AiQueries;

/// <summary>Verifies the two reviewed installation identities without provider discovery, keys or HTTP.</summary>
public sealed class StructuredQueryProviderProfileTests
{
    [Theory]
    [InlineData("Groq", "Groq", "openai/gpt-oss-20b", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("groq", "Groq", "openai/gpt-oss-20b", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("OpenAI", "OpenAI", "gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1/chat/completions")]
    [InlineData("OPENAI", "OpenAI", "gpt-5.4-mini-2026-03-17", "https://api.openai.com/v1/chat/completions")]
    public void SupportedName_ResolvesExactReviewedTuple(string input, string name, string model, string endpoint)
    {
        var profile = StructuredQueryProviderProfile.FromName(input);
        Assert.Equal(name, profile.Name); Assert.Equal(model, profile.Model); Assert.Equal(endpoint, profile.Endpoint.AbsoluteUri);
        Assert.Equal("https", profile.Endpoint.Scheme); Assert.Equal(443, profile.Endpoint.Port);
        Assert.Empty(profile.Endpoint.UserInfo); Assert.Empty(profile.Endpoint.Query); Assert.Empty(profile.Endpoint.Fragment);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData(" Groq")] [InlineData("OpenAI ")]
    [InlineData("Groq\n")] [InlineData("Anthropic")] [InlineData("Ollama")]
    [InlineData("https://private-config-canary.invalid")] [InlineData("Groq/private-config-canary")]
    public void UnsupportedName_RejectsWithoutEchoingInput(string? name)
    {
        var error = Assert.Throws<ArgumentException>(() => StructuredQueryProviderProfile.FromName(name));
        Assert.DoesNotContain("private-config-canary", error.ToString());
    }

    [Fact]
    public void Profile_CannotBeConstructedOrMutatedToRedirectCredentials()
    {
        Assert.Empty(typeof(StructuredQueryProviderProfile).GetConstructors());
        foreach (var property in typeof(StructuredQueryProviderProfile).GetProperties()) Assert.Null(property.SetMethod);
        Assert.NotEqual(StructuredQueryProviderProfile.FromName("Groq").Endpoint.Host,
            StructuredQueryProviderProfile.FromName("OpenAI").Endpoint.Host);
    }
}