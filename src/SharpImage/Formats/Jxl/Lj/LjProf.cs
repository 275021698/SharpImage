// Temporary stage timing for the libjxl port (enabled with LJPROF=1).
using System;
using System.Diagnostics;

namespace SharpImage.Formats.Jxl.Lj;

internal static class LjProf
{
    private static readonly bool On = Environment.GetEnvironmentVariable("LJPROF") == "1";
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static long last;

    public static void Mark(string what)
    {
        if (!On)
        {
            return;
        }

        long now = Sw.ElapsedTicks;
        Console.Error.WriteLine($"  {what,-28} {(now - last) * 1000.0 / Stopwatch.Frequency,8:F1} ms");
        last = now;
    }
}
