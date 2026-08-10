using FluentAssertions;
using UavOps.Agent.Contracts;
using Xunit;

namespace UavOps.Agent.Tests.Agents.MaintenanceAgent;

public class ServiceConfigEntryFormatterTests
{
    [Fact]
    public void Format_RendersAsASingleBlockSequenceItem_MatchingTheRealFilesStyle()
    {
        var entry = new ServiceConfigEntry
        {
            Description = "Test Service",
            Executable = @"C:\Windows\System32\notepad.exe",
            Disabled = true
        };

        var yaml = ServiceConfigEntryFormatter.Format(entry);

        yaml.Should().Contain("- description: 'Test Service'");
        yaml.Should().Contain("  executable: 'C:\\Windows\\System32\\notepad.exe'");
        yaml.Should().Contain("  disabled: true");
        yaml.Should().NotContain("enabled"); // omitted, never explicitly set
    }

    [Fact]
    public void Format_ArgsList_RendersAsQuotedSequence()
    {
        var entry = new ServiceConfigEntry
        {
            Description = "Service Two",
            Executable = "Service.exe",
            Args = ["-c arg1"]
        };

        var yaml = ServiceConfigEntryFormatter.Format(entry);

        yaml.Should().Contain("args:").And.Contain("- '-c arg1'");
    }
}
