using System;
using System.Collections.Generic;

namespace UnityMqtt.V2.Core
{
    public interface IMqttV2PayloadCodec
    {
        string Id { get; }
        byte[] Encode(DateTime timestampUtc, IReadOnlyDictionary<string, object> fields);
        bool TryDecode(byte[] payload, out IReadOnlyDictionary<string, object> fields, out DateTime timestampUtc, out string error);
    }
}
