using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityMqtt.V2.Core
{
    internal static class MqttV2ObjectMapper
    {
        public static bool TryConvert<T>(object value, out T result)
        {
            result = default(T);
            if (value == null)
                return false;

            if (value is T matched)
            {
                result = matched;
                return true;
            }

            try
            {
                if (value is JToken token)
                {
                    result = token.ToObject<T>();
                    return true;
                }
            }
            // JToken 変換失敗 → IConvertible へフォールスルー
            catch
            {
            }

            if (value is IConvertible)
            {
                try
                {
                    result = (T)Convert.ChangeType(value, typeof(T));
                    return true;
                }
                // IConvertible 変換失敗 → JsonConvert フォールバックへフォールスルー
                catch
                {
                }
            }

            try
            {
                result = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
                return true;
            }
            // JsonConvert フォールバックも失敗 → 変換不可として false を返す
            catch
            {
                return false;
            }
        }
    }
}