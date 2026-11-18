namespace Orkka.SharedKernel.Ai;

using System.Text.Json;

/// <summary>
/// Pulls a JSON object out of raw model output.
///
/// "Return ONLY valid JSON" is a request, not a guarantee. Real replies arrive fenced, with a
/// leading "Here's the plan:", with trailing commentary, or truncated when the response hits the
/// output cap. Stripping fences alone — which is all this used to do — turns every one of those
/// into an opaque parse failure.
///
/// Lives in SharedKernel because every module that asks a model for JSON needs exactly this;
/// it was Tasks-internal until brand-kit derivation became the second caller, and a second
/// naive copy would have reintroduced the failure mode above.
/// </summary>
public static class ModelJson
{
    private static readonly JsonSerializerOptions LenientOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The outermost JSON object in <paramref name="text"/>, or null when there isn't one.
    /// Brace-matched rather than regex'd, and string-aware so a brace inside a description
    /// doesn't end the object early.
    /// </summary>
    public static string? ExtractObject(string? text) => Extract(text, '{', '}');

    /// <summary>
    /// The outermost JSON array in <paramref name="text"/>, or null when there isn't one.
    /// Same contract as <see cref="ExtractObject"/> for callers whose prompt asks for an array.
    /// </summary>
    public static string? ExtractArray(string? text) => Extract(text, '[', ']');

    private static string? Extract(string? text, char open, char close)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var start = text.IndexOf(open);
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        // Unbalanced: the reply was cut off mid-value, which is what an output-cap hit looks
        // like. Reported as such rather than as a generic parse error.
        return null;
    }

    /// <summary>
    /// Deserializes model output, or throws <see cref="ModelOutputException"/> with the raw text
    /// attached so the failure is diagnosable from logs instead of guessed at.
    /// </summary>
    public static T Deserialize<T>(string? text, string what)
    {
        var json = ExtractObject(text);

        if (json is null)
        {
            throw new ModelOutputException(
                $"The model's {what} was not valid JSON — it was empty, or cut off before the " +
                "object closed (usually the output limit).", text);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, LenientOptions)
                ?? throw new ModelOutputException($"The model's {what} deserialized to null.", text);
        }
        catch (JsonException ex)
        {
            throw new ModelOutputException(
                $"The model's {what} was not valid JSON: {ex.Message}", text);
        }
    }
}

/// <summary>
/// The model replied with something unusable. Distinct from a transport or budget failure: the
/// caller should say the plan came back malformed, not that the request was rejected.
/// </summary>
public sealed class ModelOutputException(string message, string? rawOutput)
    : Exception(message)
{
    /// <summary>Attached for logs. Never returned to the caller — it can run to thousands of
    /// characters and is only useful to whoever is debugging the prompt.</summary>
    public string? RawOutput { get; } = rawOutput;
}
