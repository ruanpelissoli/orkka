namespace Orkka.LocalRunner.Cli;

using Orkka.LocalRunner.Commands;

/// <summary>
/// `./orkka commands [list|approve|revoke]` — the developer's say over which repository
/// commands this machine runs. Orkka runs a repo's setup/check/build commands and run targets
/// only when the repository's orkka.json declares them on its default branch, or the developer
/// approved them here. Approval happens at this terminal and nowhere else.
/// </summary>
public static class CommandsCommand
{
    public const string Usage =
        """
        ./orkka commands                 commands waiting for your approval, and the approved ones
        ./orkka commands approve <id>    allow one command for one repository on this machine
                                         (shows it in full and asks; --yes skips the question)
        ./orkka commands revoke <id>     forget an approval (or a pending request)

        Commands declared in a repository's orkka.json on its default branch never need approval.
        """;

    public static int Run(string[] args, string? orkkaRoot = null, TextReader? input = null)
    {
        var store = new CommandApprovalStore(orkkaRoot);
        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        var yes = args.Any(a => a is "--yes" or "-y");
        var id = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        switch (sub)
        {
            case null or "list" or "ls":
                List(store);
                return 0;
            case "approve":
                return id is null ? Fail("Usage: ./orkka commands approve <id>") : Approve(store, id, yes, input ?? Console.In);
            case "revoke" or "remove" or "rm":
                if (id is null)
                    return Fail("Usage: ./orkka commands revoke <id>");
                if (!store.Revoke(id))
                    return Fail($"No command with id {id}. Run `./orkka commands` to see them.");
                Console.WriteLine($"Forgot {id}. Orkka will ask again before running it.");
                return 0;
            default:
                Console.WriteLine(Usage);
                return sub is "help" or "--help" or "-h" ? 0 : 1;
        }
    }

    private static void List(CommandApprovalStore store)
    {
        var pending = store.Pending();
        var approved = store.Approved();

        if (pending.Count == 0 && approved.Count == 0)
        {
            Console.WriteLine("No repository commands have been asked for or approved on this machine.");
            Console.WriteLine("Commands in a repository's orkka.json on its default branch run without approval.");
            return;
        }

        if (pending.Count > 0)
        {
            Console.WriteLine("Waiting for your approval:");
            foreach (var e in pending)
                Print(e);
            Console.WriteLine("Approve one with `./orkka commands approve <id>`.");
            Console.WriteLine();
        }

        if (approved.Count > 0)
        {
            Console.WriteLine("Approved on this machine:");
            foreach (var e in approved)
                Print(e);
        }
    }

    private static void Print(CommandApprovalStore.Entry e)
    {
        Console.WriteLine($"  {e.Id}  {e.Kind.ToString().ToLowerInvariant(),-9} {e.Repo}  [{e.Workspace}]");
        foreach (var line in e.Command.Split('\n'))
            Console.WriteLine($"            $ {line}");
    }

    private static int Approve(CommandApprovalStore store, string id, bool yes, TextReader input)
    {
        var entry = store.Pending().FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return store.Approved().Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ? Fail($"{id} is already approved.")
                : Fail($"No pending command with id {id}. Run `./orkka commands` to see them.");
        }

        Console.WriteLine($"Repository: {entry.Repo}");
        Console.WriteLine($"Kind:       {entry.Kind.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Workspace:  {entry.Workspace}");
        Console.WriteLine("Command:");
        foreach (var line in entry.Command.Split('\n'))
            Console.WriteLine($"  $ {line}");
        Console.WriteLine();

        if (!yes)
        {
            Console.Write("Allow Orkka to run exactly this command for this repository on this machine? [y/N] ");
            var answer = input.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes"))
            {
                Console.WriteLine("Not approved.");
                return 1;
            }
        }

        store.Approve(entry.Id);
        Console.WriteLine($"Approved {entry.Id}. Orkka will run it the next time it is needed.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
