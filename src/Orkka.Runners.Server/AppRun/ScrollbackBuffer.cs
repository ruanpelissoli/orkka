namespace Orkka.Runners.Server.AppRun;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// A bounded byte tail of one run's terminal output, on the host side.
///
/// Stored as bytes, not as the base64 the runner streams: each streamed chunk is
/// independently padded, and a string made by concatenating padded base64 segments is not
/// valid base64 — <c>atob</c> in the browser and <c>Convert.FromBase64String</c> here both
/// refuse it. Decoding on the way in and encoding once on the way out is what makes the
/// replay a tab receives, and the failure tail an error message quotes, actually decodable.
/// </summary>
public sealed class ScrollbackBuffer(int maxBytes)
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private int _size;

    /// <summary>Appends one streamed chunk. A chunk that is not valid base64 is dropped —
    /// better a gap in the log than a poisoned buffer nobody can decode.</summary>
    public void Append(string base64)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return;
        }
        if (bytes.Length == 0)
            return;

        lock (_gate)
        {
            _chunks.Enqueue(bytes);
            _size += bytes.Length;
            while (_size > maxBytes && _chunks.Count > 1)
                _size -= _chunks.Dequeue().Length;
        }
    }

    /// <summary>Everything kept, as one base64 string. Null when nothing was printed, so the
    /// UI can tell "no output" from "an empty log".</summary>
    public string? ToBase64()
    {
        lock (_gate)
        {
            if (_size == 0)
                return null;
            var all = new byte[_size];
            var offset = 0;
            foreach (var chunk in _chunks)
            {
                chunk.CopyTo(all, offset);
                offset += chunk.Length;
            }
            return Convert.ToBase64String(all);
        }
    }

    /// <summary>The last few meaningful lines, ANSI stripped and runner narration removed —
    /// what an error message quotes when a process dies right after starting.</summary>
    public string LastLines(int count)
    {
        byte[] tail;
        lock (_gate)
        {
            // The last 16 KB is more than an error message needs.
            const int tailBytes = 16 * 1024;
            var take = Math.Min(_size, tailBytes);
            tail = new byte[take];
            var offset = take;
            foreach (var chunk in _chunks.Reverse())
            {
                if (offset == 0)
                    break;
                var n = Math.Min(chunk.Length, offset);
                Array.Copy(chunk, chunk.Length - n, tail, offset - n, n);
                offset -= n;
            }
        }
        return ExtractLastLines(Encoding.UTF8.GetString(tail), count);
    }

    /// <summary>Pure half of <see cref="LastLines"/>, for tests and reuse.</summary>
    internal static string ExtractLastLines(string text, int count)
    {
        var clean = AnsiPattern.Replace(text, "");
        var lines = clean.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("[orkka]", StringComparison.Ordinal))
            // Stack frames and the punctuation lines around a thrown error push the one line
            // that says what went wrong out of a short window; the frames are in the log pane
            // for whoever wants them.
            .Where(l => !l.StartsWith("at ", StringComparison.Ordinal)
                && !NoiseLine.IsMatch(l))
            .ToList();
        return string.Join("\n", lines.TakeLast(count));
    }

    private static readonly Regex NoiseLine = new(@"^[\{\}\^\[\]\(\);,]+$|^throw err;$", RegexOptions.Compiled);

    private static readonly Regex AnsiPattern = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07|\x1B[@-Z\\-_]",
        RegexOptions.Compiled);
}
