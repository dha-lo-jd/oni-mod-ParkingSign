using System.Diagnostics;

namespace ParkingSign;

internal static class ParkingSignLog
{
    private const string Prefix = "[ParkingSign] ";

    /// <summary>Verbose trace output. Compiled out of Release builds.</summary>
    [Conditional("DEBUG")]
    internal static void Trace(string message)
    {
        UnityEngine.Debug.LogWarning(Prefix + message);
    }

    internal static void Warn(string message)
    {
        UnityEngine.Debug.LogWarning(Prefix + message);
    }

    internal static void Error(string message)
    {
        UnityEngine.Debug.LogError(Prefix + message);
    }
}
