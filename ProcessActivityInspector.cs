using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcLens;

internal readonly record struct GlobalActivitySnapshot(int ForegroundPid, double LastInputAgeSeconds, DateTimeOffset CapturedAtUtc);

internal readonly record struct ProcessActivitySnapshot(
    bool IsForeground,
    bool HasVisibleMainWindow,
    int SessionId,
    double LastInputAgeSeconds);

/// <summary>Collects privacy-safe activity facts without reading window text or user identity.</summary>
internal sealed class ProcessActivityInspector
{
    public GlobalActivitySnapshot CaptureGlobal(DateTimeOffset now)
    {
        var foregroundPid = 0;
        try
        {
            var window = GetForegroundWindow();
            if (window != IntPtr.Zero) _ = GetWindowThreadProcessId(window, out foregroundPid);
        }
        catch
        {
            foregroundPid = 0;
        }

        var lastInputAgeSeconds = double.PositiveInfinity;
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (GetLastInputInfo(ref info))
            {
                var elapsed = unchecked((uint)Environment.TickCount - info.Time);
                lastInputAgeSeconds = Math.Round(elapsed / 1000d, 1);
            }
        }
        catch
        {
            // Missing activity is treated as unknown, never as evidence that a process is idle.
        }

        return new GlobalActivitySnapshot(foregroundPid, lastInputAgeSeconds, now);
    }

    public ProcessActivitySnapshot Inspect(Process process, GlobalActivitySnapshot global)
    {
        var visible = false;
        try
        {
            var window = process.MainWindowHandle;
            visible = window != IntPtr.Zero && IsWindowVisible(window);
        }
        catch
        {
            // Processes can disappear or deny access between enumeration and inspection.
        }

        var sessionId = -1;
        try { sessionId = process.SessionId; }
        catch { /* Unknown stays -1 and is surfaced as a safety uncertainty. */ }

        return new ProcessActivitySnapshot(
            process.Id == global.ForegroundPid,
            visible,
            sessionId,
            global.LastInputAgeSeconds);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }
}
