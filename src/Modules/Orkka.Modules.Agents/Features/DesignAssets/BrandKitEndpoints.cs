namespace Orkka.Modules.Agents.Features.DesignAssets;

using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Security;

public sealed record BrandKitPaletteDto(
    string Primary,
    string PrimaryHover,
    string Secondary,
    string Accent,
    string Background,
    string Surface,
    string Text,
    string TextMuted,
    string Border);

public sealed record BrandKitResponse(
    BrandKitPaletteDto Palette,
    string? DisplayFont,
    string? BodyFont,
    string? Tone,
    string Source);

public sealed record UpdateBrandKitRequest(
    BrandKitPaletteDto Palette,
    string? DisplayFont = null,
    string? BodyFont = null,
    string? Tone = null);

public sealed class UpdateBrandKitValidator : Validator<UpdateBrandKitRequest>
{
    // Hex is what the stylesheet, the SVGs, and the colour input all speak. Accepting
    // "rebeccapurple" here would mean every consumer has to resolve names too.
    private const string HexColor = "^#([0-9a-fA-F]{6})$";

    public UpdateBrandKitValidator()
    {
        RuleFor(x => x.Palette).NotNull().WithMessage("A palette is required.");

        When(x => x.Palette is not null, () =>
        {
            RuleFor(x => x.Palette.Primary).Matches(HexColor).WithMessage(Message("primary"));
            RuleFor(x => x.Palette.PrimaryHover).Matches(HexColor).WithMessage(Message("primary hover"));
            RuleFor(x => x.Palette.Secondary).Matches(HexColor).WithMessage(Message("secondary"));
            RuleFor(x => x.Palette.Accent).Matches(HexColor).WithMessage(Message("accent"));
            RuleFor(x => x.Palette.Background).Matches(HexColor).WithMessage(Message("background"));
            RuleFor(x => x.Palette.Surface).Matches(HexColor).WithMessage(Message("surface"));
            RuleFor(x => x.Palette.Text).Matches(HexColor).WithMessage(Message("text"));
            RuleFor(x => x.Palette.TextMuted).Matches(HexColor).WithMessage(Message("muted text"));
            RuleFor(x => x.Palette.Border).Matches(HexColor).WithMessage(Message("border"));
        });

        RuleFor(x => x.DisplayFont).MaximumLength(200);
        RuleFor(x => x.BodyFont).MaximumLength(200);
        RuleFor(x => x.Tone).MaximumLength(500);
    }

    private static string Message(string field) =>
        $"The {field} colour must be a 6-digit hex value like #2563eb.";
}

/// <summary>
/// The project's brand kit. Reads are open to every org member, like every other GET.
/// </summary>
public sealed class GetBrandKitEndpoint(
    ICurrentUser currentUser,
    IProjectResolver projectResolver,
    IDesignGenerationApi designGeneration)
    : EndpointWithoutRequest<BrandKitResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/brand");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var kit = await designGeneration.GetBrandKitAsync(currentUser.OrgId, projectId, ct);

        // 404 rather than an empty kit: a project briefed before brand kits existed genuinely
        // has none, and inventing a default here would be a palette nothing else agrees with.
        if (kit is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(ToResponse(kit), ct);
    }

    internal static BrandKitResponse ToResponse(BrandKitSummary kit) => new(
        new BrandKitPaletteDto(
            kit.Palette.Primary, kit.Palette.PrimaryHover, kit.Palette.Secondary,
            kit.Palette.Accent, kit.Palette.Background, kit.Palette.Surface,
            kit.Palette.Text, kit.Palette.TextMuted, kit.Palette.Border),
        kit.DisplayFont, kit.BodyFont, kit.Tone, kit.Source.ToString());
}

/// <summary>
/// Corrects the brand kit. Marking it Manual is the point of the whole record: derivation
/// never runs again over a human's answer, so a wrong guess is fixed once rather than argued
/// with on every asset.
/// </summary>
public sealed class UpdateBrandKitEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    IProjectResolver projectResolver)
    : Endpoint<UpdateBrandKitRequest, BrandKitResponse>
{
    private static readonly JsonSerializerOptions PaletteJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public override void Configure()
    {
        Put("/api/projects/{projectId}/brand");
    }

    public override async Task HandleAsync(UpdateBrandKitRequest req, CancellationToken ct)
    {
        // Same permission as approving assets — this is the same decision, one level up.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.DesignReview))
            return;

        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var palette = new BrandPalette(
            req.Palette.Primary, req.Palette.PrimaryHover, req.Palette.Secondary,
            req.Palette.Accent, req.Palette.Background, req.Palette.Surface,
            req.Palette.Text, req.Palette.TextMuted, req.Palette.Border);

        var kit = await db.BrandKits
            .FirstOrDefaultAsync(k => k.OrgId == currentUser.OrgId && k.ProjectId == projectId, ct);

        if (kit is null)
        {
            // Creatable, not only editable: a project briefed before brand kits existed has
            // none, and its owner should be able to set one without re-running provisioning.
            kit = new BrandKit
            {
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                ProjectId = projectId,
                PaletteJson = JsonSerializer.Serialize(palette, PaletteJsonOptions),
            };
            db.BrandKits.Add(kit);
        }
        else
        {
            kit.PaletteJson = JsonSerializer.Serialize(palette, PaletteJsonOptions);
        }

        kit.DisplayFont = Trimmed(req.DisplayFont);
        kit.BodyFont = Trimmed(req.BodyFont);
        kit.Tone = Trimmed(req.Tone);
        kit.Source = BrandKitSource.Manual;
        kit.UpdatedByUserId = currentUser.UserId;

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(GetBrandKitEndpoint.ToResponse(new BrandKitSummary(
            palette, kit.DisplayFont, kit.BodyFont, kit.Tone, kit.Source)), ct);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
