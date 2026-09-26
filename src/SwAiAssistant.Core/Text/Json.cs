using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace SwAiAssistant.Core.Text
{
    /// <summary>
    /// 全局 JSON 帮助类。配置/模型协议/特征树均使用统一设置：
    /// 枚举按字符串序列化，忽略循环引用，空值不写出。
    /// </summary>
    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.Indented
        };

        /// <summary>紧凑（无缩进）序列化，用于网络请求体。</summary>
        public static string Serialize(object value, bool indented = false)
        {
            var settings = indented ? Settings : CloneCompact();
            return JsonConvert.SerializeObject(value, settings);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        public static bool TryDeserialize<T>(string json, out T value, out Exception error)
        {
            try
            {
                value = Deserialize<T>(json);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                value = default;
                error = ex;
                return false;
            }
        }

        private static JsonSerializerSettings CloneCompact()
        {
            return new JsonSerializerSettings
            {
                ContractResolver = Settings.ContractResolver,
                Converters = Settings.Converters,
                NullValueHandling = Settings.NullValueHandling,
                ReferenceLoopHandling = Settings.ReferenceLoopHandling,
                Formatting = Formatting.None
            };
        }
    }
}
