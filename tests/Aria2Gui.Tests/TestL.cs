namespace Aria2Gui.Helpers;

/// <summary>Test stand-in for the app's MRT-backed localization lookup — the real one
/// needs the WinAppSDK runtime, which the test host doesn't load. Returns the key, which
/// is also what the real L does on a miss.</summary>
internal static class L
{
    public static string Get(string key) => key;

    public static string Get(string key, params object?[] args) =>
        key + ":" + string.Join(',', args);
}
