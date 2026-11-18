namespace Orkka.IntegrationTests.Helpers;

using System.Text.Json;
using Orkka.SharedKernel.Utilities;

public static class TestJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new UlidJsonConverter() }
    };
}
