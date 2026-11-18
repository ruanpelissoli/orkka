namespace Orkka.Modules.Tasks.Infrastructure.Services;

using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Makes a plan's client work depend on the API work it calls.
///
/// The model is asked to declare this itself, and usually does — but "usually" is not a
/// guarantee, and the failure is silent and expensive: both tasks start at once, the client
/// agent finds no endpoint to call, and invents one. The prompt's own dependency rule is
/// phrased in terms of reading, importing or extending code, which a task calling an HTTP
/// endpoint in a DIFFERENT repository does none of — so the model is often right by its own
/// instructions and wrong in effect.
///
/// This pass closes that gap deterministically, after the model has spoken.
///
/// Ordering alone would not be enough. Submission order only decides when a task enters the
/// queue; the queue happily runs two tasks in different repos at the same time, because
/// same-repo serialisation is the only concurrency rule that applies. Only a real dependency
/// edge defers the client work until the API is merged.
/// </summary>
public static class ApiContractOrdering
{
    /// <summary>
    /// Reorders a proposal so API work precedes the work that calls it, then adds the missing
    /// edges. Returns the list unchanged when the project is not layered, or when the plan sits
    /// entirely on one side of the contract.
    /// </summary>
    /// <param name="resolveLayer">A planned task's layer, from the repo it was filed against.
    /// Passed in because the caller owns the name-to-repo resolution the model's output needs.</param>
    public static IReadOnlyList<DecomposedTask> Apply(
        IReadOnlyList<DecomposedTask> tasks,
        RepoLayerMap layers,
        Func<DecomposedTask, Ulid?> resolveRepo)
    {
        if (!layers.IsLayered || tasks.Count < 2)
            return tasks;

        var layerOf = tasks
            .Select(t => layers.LayerOf(resolveRepo(t)))
            .ToArray();

        var providers = layerOf.Count(l => l == WorkLayer.ApiProvider);
        var consumers = layerOf.Count(l => l == WorkLayer.ApiConsumer);

        // Nothing to order: a plan that is all API work, or all client work, has no cross-layer
        // edge to add and must not be reshuffled.
        if (providers == 0 || consumers == 0)
            return tasks;

        // Stable partition by layer. Stable matters: within a side, the model's build order is
        // real information — it knows the schema migration comes before the endpoint that reads
        // it — and this pass has no business reordering that.
        var order = Enumerable.Range(0, tasks.Count)
            .OrderBy(i => Rank(layerOf[i]))
            .ToArray();

        // Old index -> new index, so the edges the model already declared survive the move.
        // Without this remap, deleting or reordering silently slides every later edge onto its
        // neighbour — the same hazard the proposal's stable Key exists to avoid.
        var newIndexOf = new int[tasks.Count];
        for (var newIndex = 0; newIndex < order.Length; newIndex++)
            newIndexOf[order[newIndex]] = newIndex;

        var reordered = new List<DecomposedTask>(tasks.Count);

        for (var newIndex = 0; newIndex < order.Length; newIndex++)
        {
            var oldIndex = order[newIndex];
            var task = tasks[oldIndex];

            var edges = new SortedSet<int>();

            foreach (var oldDependency in task.DependsOn ?? [])
            {
                if (oldDependency >= 0 && oldDependency < tasks.Count)
                    edges.Add(newIndexOf[oldDependency]);
            }

            // Every API task in this plan, for every client task. Scoped to the plan, which is
            // one milestone — i.e. one feature — so "all of them" is the right answer rather
            // than an over-reach: the screen needs the whole surface the feature exposes.
            if (layerOf[oldIndex] == WorkLayer.ApiConsumer)
            {
                for (var i = 0; i < order.Length; i++)
                {
                    if (layerOf[order[i]] == WorkLayer.ApiProvider)
                        edges.Add(i);
                }
            }

            // Backwards-only, exactly as the decomposer's own rule requires: acyclic by
            // construction rather than by detection. The partition above is what guarantees
            // every provider index is lower than every consumer index.
            var resolved = edges.Where(e => e < newIndex).ToList();

            reordered.Add(task with { DependsOn = resolved.Count > 0 ? resolved : null });
        }

        return reordered;
    }

    private static int Rank(WorkLayer layer) => layer switch
    {
        WorkLayer.ApiProvider => 0,
        // Unknown sits between the two: it may be either side, so it must not be allowed to
        // start before the API, and must not become a blocker for the client.
        WorkLayer.Unknown => 1,
        _ => 2,
    };
}
