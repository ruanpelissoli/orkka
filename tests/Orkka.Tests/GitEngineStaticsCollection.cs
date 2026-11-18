namespace Orkka.Tests;

/// <summary>
/// Tests that drive the real git engine through <c>RunnerOperations</c> share process-wide
/// state: the runner's engine is a static the constructor installs. One collection
/// serializes them.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GitEngineStaticsCollection
{
    public const string Name = "git engine statics";
}
