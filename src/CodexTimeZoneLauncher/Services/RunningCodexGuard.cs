using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexTimeZoneLauncher.Services;

public static class RunningCodexGuard
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder fileName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static IReadOnlyList<int> FindMatchingProcesses(string executablePath)
    {
        var target = Path.GetFullPath(executablePath);
        var matches = new List<int>();
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    var buffer = new StringBuilder(32768);
                    var length = buffer.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, buffer, ref length)) continue;
                    var candidate = Path.GetFullPath(buffer.ToString());
                    if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(process.Id);
                    }
                }
                catch
                {
                    // A process can disappear while it is being inspected. Ignore and continue.
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        return matches;
    }
}
