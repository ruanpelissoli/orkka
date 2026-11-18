namespace Orkka.Modules.Agents.PublicApi;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;

public sealed class DesignGenerationApi(AgentsDbContext db) : IDesignGenerationApi
{
    public async Task<BrandKitSummary?> GetBrandKitAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default)
    {
        var kit = await db.BrandKits
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.OrgId == orgId && k.ProjectId == projectId, ct);

        return kit is null ? null : ToSummary(kit);
    }

    private static readonly JsonSerializerOptions PaletteJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static BrandKitSummary ToSummary(BrandKit kit) => new(
        JsonSerializer.Deserialize<BrandPalette>(kit.PaletteJson, PaletteJsonOptions)!,
        kit.DisplayFont, kit.BodyFont, kit.Tone, kit.Source);

    /// <summary>
    /// The project's decided visual identity, injected into any Designer prompt. Shared with
    /// agent chat so an asset requested six weeks later is still on-palette — previously the
    /// only guidance was "read the project's colour scheme first", which asks the model to
    /// rediscover a decision that was already made.
    /// </summary>
    public static string BrandKitBlock(BrandKitSummary? kit) => kit is null ? "" : $"""


        THIS PROJECT'S BRAND (ALREADY DECIDED — USE IT, DO NOT INVENT):
        - primary        {kit.Palette.Primary}
        - primary hover  {kit.Palette.PrimaryHover}
        - secondary      {kit.Palette.Secondary}
        - accent         {kit.Palette.Accent}
        - background     {kit.Palette.Background}
        - surface        {kit.Palette.Surface}
        - text           {kit.Palette.Text}
        - text muted     {kit.Palette.TextMuted}
        - border         {kit.Palette.Border}
        {(kit.DisplayFont is null ? "" : $"- display font   {kit.DisplayFont}\n        ")}{(kit.BodyFont is null ? "" : $"- body font      {kit.BodyFont}\n        ")}{(kit.Tone is null ? "" : $"- tone           {kit.Tone}\n        ")}
        Every asset you produce uses these colours. Neutrals and shading may be derived from
        them; a new hue may not. If the user explicitly asks for something off-palette, follow
        the user — they own the brand, this block is only what it is today.
        """;
}
