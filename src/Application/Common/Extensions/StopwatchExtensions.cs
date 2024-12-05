using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Common.Extensions;

public static class StopwatchExtensions
{
    public static double ElapsedNanoSeconds(this Stopwatch watch)
    {
        return watch.ElapsedTicks * 1000000000 / (double)Stopwatch.Frequency;
    }

    public static double ElapsedMicroSeconds(this Stopwatch watch)
    {
        return watch.ElapsedTicks * 1000000 / (double)Stopwatch.Frequency;
    }

    public static double ElapsedMilliSeconds(this Stopwatch watch)
    {
        return watch.ElapsedTicks * 1000 / (double)Stopwatch.Frequency;
    }
}
