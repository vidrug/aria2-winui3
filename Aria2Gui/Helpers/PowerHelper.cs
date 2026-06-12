using System.Runtime.InteropServices;

namespace Aria2Gui.Helpers;

/// <summary>
/// Holds the system awake while transfers run (SetThreadExecutionState). The display may
/// still turn off — only sleep/hibernation is held off. ES_CONTINUOUS state is per-thread,
/// so every call must come from the same long-lived (UI) thread; the state clears itself
/// when the process exits.
/// </summary>
internal static class PowerHelper
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private static bool _active;

    public static void SetKeepAwake(bool on)
    {
        if (on == _active)
            return;
        _active = on;
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
