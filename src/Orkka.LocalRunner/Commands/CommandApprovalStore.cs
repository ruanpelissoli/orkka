namespace Orkka.LocalRunner.Commands;

using System.Text.Json;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// The developer's decisions about repository commands, on this machine only:
/// <c>~/.orkka/command-approvals.json</c>. Written by <c>./orkka commands approve</c> (a
/// person at this machine's terminal) and by the runner when it refuses a command (a pending
/// entry, so the developer can see exactly what was asked). Nothing the host sends can approve
/// a command — there is no network path into this file.
/// </summary>
public sealed class CommandApprovalStore(string? orkkaRoot = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private readonly string _path = Path.Combine(orkkaRoot ?? Cli.RunnerEnvFile.OrkkaDir, "command-approvals.json");

    /// <summary>One remembered command.</summary>
    public sealed record Entry(
        string Id, string Repo, string Command, CommandKind Kind, string Workspace,
        DateTimeOffset FirstSeenAt, DateTimeOffset? ApprovedAt);

    private sealed class FileModel
    {
        public List<Entry> Approved { get; set; } = [];
        public List<Entry> Pending { get; set; } = [];
    }

    public bool IsApproved(string repo, string command)
    {
        var id = CommandTrust.IdFor(repo, command);
        lock (Gate)
        {
            return Load().Approved.Any(e => e.Id == id && e.Repo == repo && e.Command == command);
        }
    }

    /// <summary>Remembers a refused command (once) and returns how to approve it.</summary>
    public CommandApproval RecordPending(string repo, string command, CommandKind kind, string workspace)
    {
        var id = CommandTrust.IdFor(repo, command);
        lock (Gate)
        {
            var model = Load();
            if (!model.Pending.Any(e => e.Id == id))
            {
                model.Pending.Add(new Entry(id, repo, command, kind, workspace, DateTimeOffset.UtcNow, null));
                Save(model);
            }
        }

        return new CommandApproval(id, repo, command, kind, Environment.MachineName);
    }

    public IReadOnlyList<Entry> Pending()
    {
        lock (Gate)
        {
            return Load().Pending;
        }
    }

    public IReadOnlyList<Entry> Approved()
    {
        lock (Gate)
        {
            return Load().Approved;
        }
    }

    /// <summary>Moves a pending command to approved. Null when no pending command has that id.</summary>
    public Entry? Approve(string id)
    {
        lock (Gate)
        {
            var model = Load();
            var entry = model.Pending.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return null;

            model.Pending.Remove(entry);
            var approved = entry with { ApprovedAt = DateTimeOffset.UtcNow };
            model.Approved.RemoveAll(e => e.Id == approved.Id);
            model.Approved.Add(approved);
            Save(model);
            return approved;
        }
    }

    /// <summary>Forgets an approved or pending command. False when there is none with that id.</summary>
    public bool Revoke(string id)
    {
        lock (Gate)
        {
            var model = Load();
            var removed = model.Approved.RemoveAll(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                          + model.Pending.RemoveAll(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                Save(model);
            return removed > 0;
        }
    }

    private FileModel Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json) ?? new FileModel()
                : new FileModel();
        }
        catch (JsonException)
        {
            // A damaged file approves nothing; the next save rewrites it.
            return new FileModel();
        }
    }

    private void Save(FileModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(model, Json));
        File.Move(temp, _path, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
