namespace Orkka.Modules.Documents.Contracts;

/// <summary>Stored as varchar — append new values, never reorder or rename.</summary>
public enum DocumentSource
{
    Manual,
    Agent,
    Brief,

    /// <summary>Written by the post-completion documentation sync of a finished task.</summary>
    Task,
}
