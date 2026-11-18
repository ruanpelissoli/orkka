namespace Orkka.Modules.Projects.Contracts;

// Owned by the Projects module but public: these enums appear in contract records
// and in legacy code (via GlobalUsings shims) until the remaining modules extract.

public enum WorkspaceStatus
{
    Pending,
    Cloning,
    Ready,
    Error
}

/// <summary>What a project's base folder on the developer's machine is: one repository
/// (the folder itself is a git repo) or a folder holding several repositories.</summary>
public enum BaseFolderShape
{
    Monorepo = 0,
    MultiRepo = 1
}

