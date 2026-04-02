using System;

namespace PullSub.Tests.PerfShared
{
    [Serializable]
    public struct Phase0PerfDataPayload
    {
        public long Sequence { get; set; }
        public long SentUtcTicks { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }
}
