namespace Orkka.Tests.Features.Agents;

using System.Text.RegularExpressions;
using FluentAssertions;
using Orkka.Modules.Agents.Features.AgentChat;

/// <summary>
/// A signed link to a design asset is baked into an &lt;img&gt; on the Design page and into
/// chat history, so its lifetime is really a page's lifetime — not a security window. At
/// fifteen minutes, every image on a page left open became Azure's raw XML error, which reads
/// to the user as "the images were never generated" while the assets sat safe in blob storage
/// (2026-09-08). The window is a single shared constant for the same reason: it was the six
/// endpoints each spelling out their own `TimeSpan.FromMinutes(15)` that let it stay wrong.
/// </summary>
public sealed partial class DesignAssetLinkLifetimeTests
{
    [Fact]
    public void A_signed_link_outlives_a_working_session()
    {
        DesignAssetGenerator.DisplayUrlExpiry.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromHours(8),
            "the link has to survive a tab someone leaves open, which a short signature never does");
    }

    [Fact]
    public void No_design_asset_endpoint_mints_a_window_of_its_own()
    {
        var offenders = new List<string>();

        foreach (var file in DesignAssetSources())
        {
            var source = File.ReadAllText(file);
            foreach (Match call in SignedLinkCall().Matches(source))
            {
                if (!call.Value.Contains("DisplayUrlExpiry", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {Collapse(call.Value)}");
            }
        }

        offenders.Should().BeEmpty(
            "every signed link to an asset shares one lifetime — see DesignAssetGenerator.DisplayUrlExpiry");
    }

    /// <summary>A GenerateDownloadUrlAsync call, arguments included, across line breaks.</summary>
    [GeneratedRegex(@"GenerateDownloadUrlAsync\([^;]*?\)", RegexOptions.Singleline)]
    private static partial Regex SignedLinkCall();

    private static string Collapse(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    private static IEnumerable<string> DesignAssetSources()
    {
        var agents = Path.Combine(RepoRoot, "src", "Modules", "Orkka.Modules.Agents", "Features");

        return
        [
            .. Directory.EnumerateFiles(Path.Combine(agents, "DesignAssets"), "*.cs"),
            Path.Combine(agents, "AgentChat", "ListMessagesEndpoint.cs"),
            Path.Combine(agents, "AgentChat", "DesignAssetGenerator.cs"),
        ];
    }

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orkka.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"Orkka.slnx not found above {AppContext.BaseDirectory}");
    }
}
