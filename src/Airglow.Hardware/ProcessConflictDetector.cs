using System.Diagnostics;
using Airglow.Core.Hardware;

namespace Airglow.Hardware;

public sealed class ProcessConflictDetector : IConflictDetector
{
    public IReadOnlySet<string> Running(IEnumerable<string> processNames)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in processNames)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0)
            {
                running.Add(name);
            }

            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return running;
    }
}
