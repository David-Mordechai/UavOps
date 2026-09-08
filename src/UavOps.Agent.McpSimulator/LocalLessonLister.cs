using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>Lists <c>*.ps1</c> files from <see cref="SimulatorOptions.LessonsFolder"/>, a local
/// folder on this machine.</summary>
public sealed class LocalLessonLister(SimulatorOptions options) : ILessonLister
{
    public IReadOnlyList<string> ListLessons()
    {
        if (!Directory.Exists(options.LessonsFolder))
        {
            return [];
        }

        return Directory.GetFiles(options.LessonsFolder, "*.ps1")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
