namespace Orkka.Tests;

using System.Runtime.CompilerServices;

/// <summary>
/// Git only runs on the developer's machine, where a commit with no explicit identity is
/// authored by whatever their git config says. A test box may have no git identity at all
/// (CI) or someone's real one (a laptop), so the test process stands in for "the developer"
/// with a fixed one in a global git config of its own (<c>GIT_CONFIG_GLOBAL</c>). Being the
/// GLOBAL level, a repository's local identity — the one a runner workspace configures — and
/// an explicit identity on a command still win over it, exactly as on a real machine.
/// </summary>
internal static class TestGitIdentity
{
    public const string Name = "Orkka Test Developer";
    public const string Email = "developer@orkka-tests.invalid";

    [ModuleInitializer]
    internal static void Apply()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orkka-tests-gitconfig-{Environment.ProcessId}");
        File.WriteAllText(path, $"[user]\n\tname = {Name}\n\temail = {Email}\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", path);
    }
}
