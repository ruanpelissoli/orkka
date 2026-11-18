namespace Orkka.SharedKernel.Ai;

public sealed record AnthropicRequest
{
    public required string Model { get; init; }
    public int MaxTokens { get; init; } = 1024;
    public string? System { get; init; }
    public required List<AnthropicMessage> Messages { get; init; }

    /// <summary>Emits top-level cache_control (auto-places a cache breakpoint on the last
    /// cacheable block). Only worthwhile for multi-turn calls that re-send a growing prefix;
    /// prompts below the model's cacheable minimum (2-4K tokens) silently don't cache.</summary>
    public bool EnableCaching { get; init; }
}

public sealed record AnthropicMessage(string Role, List<AnthropicContent> Content)
{
    public AnthropicMessage(string role, string text)
        : this(role, [new AnthropicTextContent(text)]) { }
}

public abstract record AnthropicContent(string Type);

public sealed record AnthropicTextContent(string Text) : AnthropicContent("text");

public sealed record AnthropicImageContent(AnthropicImageSource Source) : AnthropicContent("image");

public sealed record AnthropicImageSource(string Type, string Url)
{
    public static AnthropicImageSource FromUrl(string url) => new("url", url);
}

public sealed record AnthropicResponse
{
    public required string Text { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheWriteTokens { get; init; }
}
