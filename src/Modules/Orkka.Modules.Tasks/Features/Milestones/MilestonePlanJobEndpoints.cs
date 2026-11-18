namespace Orkka.Modules.Tasks.Features.Milestones;

using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Domain;
using Orkka.Modules.Tasks.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>
/// How a finished plan is stored and read back. Web-cased on purpose: this is the same JSON the
/// endpoint used to return inline, so a client that already knew how to read a proposal did not
/// have to learn a second shape when planning went asynchronous.
/// </summary>
internal static class MilestonePlanSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(MilestonePlanResponse plan) =>
        JsonSerializer.Serialize(plan, Options);

    /// <summary>
    /// Null for a row with no result, and null rather than an exception for one written by an
    /// incompatible version. A plan that cannot be read is a plan the user re-runs; throwing
    /// here would turn it into a 500 on a page that is otherwise fine.
    /// </summary>
    public static MilestonePlanResponse? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MilestonePlanResponse>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads one planning run. This is what makes the operation survive a refresh: the plan lives in
/// the database rather than in the response body of a request the browser may no longer be
/// waiting on.
/// </summary>
public sealed class GetMilestonePlanJobEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MilestonePlanJobResponse>
{
    public override void Configure()
    {
        Get("/api/projects/{projectId}/milestone-plans/{jobId}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId || !Ulid.TryParse(Route<string>("jobId"), out var jobId))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var job = await db.MilestonePlanJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.OrgId == currentUser.OrgId
                && j.ProjectId == projectId, ct);

        if (job is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(
            job.ToResponse(MilestonePlanSerialization.Deserialize(job.ResultJson)), ct);
    }
}

/// <summary>
/// The caller's newest planning run for a project (optionally for one milestone), so a client
/// that reloaded — or opened the page on another device — reattaches without having kept the
/// job id.
///
/// Returns a finished run too, not only an in-flight one: someone who closed the tab at 2:59
/// and comes back at 4:00 should find the plan waiting, which is the whole point of persisting
/// it. Nothing is consumed by reading — the plan is only spent when they accept it.
/// </summary>
public sealed class GetActiveMilestonePlanJobEndpoint(
    TasksDbContext db,
    IProjectResolver projectResolver,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MilestonePlanJobResponse>
{
    /// <summary>How far back a finished plan is still offered. Long enough to survive a lunch
    /// break, short enough that yesterday's abandoned proposal does not reappear as if the user
    /// had just asked for it.</summary>
    private static readonly TimeSpan CompletedWindow = TimeSpan.FromHours(2);

    public override void Configure()
    {
        Get("/api/projects/{projectId}/milestone-plans/active");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await projectResolver.ResolveAsync(Route<string>("projectId")!, ct);
        if (resolved is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Absent = a plan for a new milestone; present = planning into that one. Two different
        // runs, and showing one where the other belongs would be worse than showing neither.
        Ulid? milestoneId = Ulid.TryParse(Query<string>("milestoneId", isRequired: false),
            out var parsed) ? parsed : null;

        var cutoff = DateTimeOffset.UtcNow - CompletedWindow;

        var query = db.MilestonePlanJobs
            .AsNoTracking()
            .Where(j => j.OrgId == currentUser.OrgId
                && j.ProjectId == projectId
                && j.RequesterUserId == currentUser.UserId
                && (j.Status == MilestonePlanStatus.Pending
                    || j.Status == MilestonePlanStatus.Running
                    || j.CreatedAt >= cutoff));

        // Split rather than `j.MilestoneId == milestoneId`, for the same reason the enqueue
        // path does: the null case is the roadmap's own "plan a new milestone" run, and it must
        // not rest on how a provider compares a null parameter to a null column.
        query = milestoneId is { } scoped
            ? query.Where(j => j.MilestoneId == scoped)
            : query.Where(j => j.MilestoneId == null);

        var job = await query
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (job is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(
            job.ToResponse(MilestonePlanSerialization.Deserialize(job.ResultJson)), ct);
    }
}
