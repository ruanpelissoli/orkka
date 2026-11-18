namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Thread-safe rolling buffer of the most recent CLI output lines. Stderr callbacks can
/// fire on a pump thread while the run loop reads on failure, and stderr is unbounded —
/// keep only the tail that fits the error-message budget.
/// </summary>
internal sealed class BoundedLineBuffer(int maxChars)
{
    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();
    private int _totalChars;

    public void Append(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            _totalChars += line.Length + 1;
            while (_totalChars > maxChars && _lines.Count > 1)
                _totalChars -= _lines.Dequeue().Length + 1;
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return string.Join('\n', _lines);
        }
    }
}
