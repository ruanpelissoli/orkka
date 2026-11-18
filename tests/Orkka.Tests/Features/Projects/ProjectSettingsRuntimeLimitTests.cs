namespace Orkka.Tests.Features.Projects;

using System.Text.Json;
using FluentAssertions;

/// <summary>
/// The task runtime limit. The property that matters most is the default: a project nobody
/// configured must resolve to no limit at all, because the behaviour it replaces — a flat 30
/// minutes for every run — killed legitimate work and threw away everything it had paid for.
/// </summary>
public sealed class ProjectSettingsRuntimeLimitTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static ProjectSettingsDto Settings(bool enabled = false, int minutes = 30) =>
        new(MaxParallelTasks: 1,
            SameRepoParallelism: false,
            TaskRuntimeLimitEnabled: enabled,
            TaskRuntimeLimitMinutes: minutes);

    [Fact]
    public void DefaultSettings_HaveNoRuntimeLimit()
    {
        Settings().TaskRuntimeLimit.Should().BeNull();
    }

    [Fact]
    public void DisabledLimit_IgnoresTheStoredMinutes()
    {
        Settings(enabled: false, minutes: 5).TaskRuntimeLimit.Should().BeNull();
    }

    [Fact]
    public void EnabledLimit_ResolvesToTheConfiguredMinutes()
    {
        Settings(enabled: true, minutes: 45).TaskRuntimeLimit
            .Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void EnabledLimit_DefaultsToThirtyMinutes()
    {
        Settings(enabled: true).TaskRuntimeLimit
            .Should().Be(TimeSpan.FromMinutes(ProjectSettingsDto.DefaultTaskRuntimeLimitMinutes));
    }

    [Theory]
    [InlineData(0, ProjectSettingsDto.MinTaskRuntimeLimitMinutes)]
    [InlineData(-10, ProjectSettingsDto.MinTaskRuntimeLimitMinutes)]
    [InlineData(100000, ProjectSettingsDto.MaxTaskRuntimeLimitMinutes)]
    public void OutOfRangeMinutes_AreClampedRatherThanTakenLiterally(int stored, int expected)
    {
        // Settings JSON written by an older client must still resolve to something a run can
        // actually happen under — a zero-minute limit would fail every task instantly.
        Settings(enabled: true, minutes: stored).TaskRuntimeLimit
            .Should().Be(TimeSpan.FromMinutes(expected));
    }

    [Fact]
    public void SettingsStoredBeforeTheFieldExisted_DeserializeWithNoLimit()
    {
        const string legacyJson = """{"maxParallelTasks":2,"sameRepoParallelism":true}""";

        var settings = JsonSerializer.Deserialize<ProjectSettingsDto>(legacyJson, JsonOptions)!;

        settings.TaskRuntimeLimitEnabled.Should().BeFalse();
        settings.TaskRuntimeLimit.Should().BeNull();
    }

    [Fact]
    public void DerivedLimit_IsNotWrittenIntoTheStoredSettingsJson()
    {
        // The bag round-trips through the project row and the API response; a computed
        // property leaking in would surface as a settings key clients could try to set.
        var json = JsonSerializer.Serialize(Settings(enabled: true), JsonOptions);

        json.Should().NotContain("\"taskRuntimeLimit\"");
        json.Should().Contain("taskRuntimeLimitEnabled");
        json.Should().Contain("taskRuntimeLimitMinutes");
    }
}
