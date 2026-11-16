using System.Diagnostics;

namespace ParkingSign;

// Development trace. The [Conditional] attribute removes both the call and the evaluation of its (string-interpolated)
// argument from Release builds, so the Workshop build writes nothing here, per duplicant and per second or otherwise.
internal static class Trace
{
    [Conditional("DEBUG")]
    public static void Log(string message)
    {
        UnityEngine.Debug.Log(message);
    }
}
