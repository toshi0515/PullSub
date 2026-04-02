using System;
using System.Collections.Generic;

namespace PullSub.Tests.PerfShared
{
    public static class PerfMath
    {
        public static double Percentile95(List<double> values)
        {
            if (values == null || values.Count == 0)
                return 0d;

            values.Sort();
            var index = (int)Math.Ceiling(values.Count * 0.95d) - 1;
            if (index < 0)
                index = 0;
            if (index >= values.Count)
                index = values.Count - 1;
            return values[index];
        }
    }
}
