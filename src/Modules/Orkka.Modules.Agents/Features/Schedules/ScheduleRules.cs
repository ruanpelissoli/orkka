namespace Orkka.Modules.Agents.Features.Schedules;

using Cronos;

/// <summary>Limits and cron/time-zone helpers shared by the endpoints, the reminder tool
/// and the processor. Numbers are deliberately conservative — a schedule is a standing
/// order to spend money without anyone watching.</summary>
public static class ScheduleRules
{
    public const int MaxSchedulesPerProject = 20;
    /// <summary>Recurring playbooks may not fire more often than this.</summary>
    public static readonly TimeSpan MinPlaybookInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinReminderDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxReminderDelay = TimeSpan.FromDays(30);
    public const int MaxPendingRemindersPerAgent = 25;
    public const int MaxRemindersPerAgentPerDay = 100;
    /// <summary>Consecutive failures before a schedule disables itself.</summary>
    public const int AutoDisableAfterFailures = 5;
    public const int MessageMax = 4000;
    public const int NameMax = 120;

    /// <summary>Parses a standard 5-field cron. Returns null (with a reason) when invalid.</summary>
    public static CronExpression? TryParseCron(string? expression, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "A cron expression is required.";
            return null;
        }
        try
        {
            return CronExpression.Parse(expression.Trim(), CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            error = $"Invalid cron expression: {ex.Message}";
            return null;
        }
    }

    public static TimeZoneInfo? TryResolveTimeZone(string? id, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception)
        {
            error = $"Unknown time zone \"{id}\".";
            return null;
        }
    }

    /// <summary>Next occurrence strictly after <paramref name="after"/>, normalized to UTC
    /// (Cronos answers in the zone's offset; every stored instant is UTC).</summary>
    public static DateTimeOffset? NextOccurrence(CronExpression cron, TimeZoneInfo tz, DateTimeOffset after)
        => cron.GetNextOccurrence(after, tz)?.ToUniversalTime();

    /// <summary>
    /// A cron that fires more often than <see cref="MinPlaybookInterval"/> anywhere in the
    /// next day is rejected. Sampled rather than proven: two consecutive occurrences closer
    /// than the floor is what a "* * * * *" or "*/5" mistake looks like in practice.
    /// </summary>
    public static bool FiresTooOften(CronExpression cron, TimeZoneInfo tz, DateTimeOffset from)
    {
        var occurrences = cron.GetOccurrences(from.UtcDateTime, from.UtcDateTime.AddDays(1), tz).Take(200).ToList();
        for (var i = 1; i < occurrences.Count; i++)
            if (occurrences[i] - occurrences[i - 1] < MinPlaybookInterval)
                return true;
        return false;
    }
}
