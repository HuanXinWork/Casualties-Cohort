using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Together;

internal static class RunSettingsCodec {
    // SaveSystem.TupleListToDic 逐键调用 RunSettings.GetSetting，遇到无对应 UI 项的 Fullgame 预设键会抛异常并导致整包回退默认值；
    // 此处直接按预设标量类型逐条解码，单条非法或未知扩展项仅局部跳过，避免连坐清空其余联机世界规则。
    internal static Dictionary<string, object> Decode(JArray entries, Dictionary<string, object> defaults, Action<string> rejected) {
        var result = new Dictionary<string, object>(defaults);
        if (entries.Count > 256) throw new FormatException("Too many run settings.");
        foreach (var entry in entries) {
            string key = null;
            try {
                if (!(entry is JObject pair)) throw new FormatException("Invalid run setting entry.");
                key = pair.Value<string>("Item1");
                if (string.IsNullOrEmpty(key) || key.Length > 128 || !(entry["Item2"] is JValue scalar) || scalar.Value == null)
                    throw new FormatException("Invalid run setting.");
                object value = scalar.Value;
                if (defaults.TryGetValue(key, out object preset) && preset != null)
                    value = Convert.ChangeType(value, preset.GetType(), CultureInfo.InvariantCulture);
                else if (scalar.Type == JTokenType.Integer) value = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                else if (scalar.Type == JTokenType.Float) value = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                else if (scalar.Type != JTokenType.Boolean && scalar.Type != JTokenType.String) throw new FormatException("Unsupported setting type.");
                if (value is float number && (float.IsNaN(number) || float.IsInfinity(number))) throw new FormatException("Non-finite setting value.");
                result[key] = value;
            } catch (Exception e) when (e is FormatException || e is OverflowException || e is InvalidCastException || e is ArgumentException) {
                rejected?.Invoke(key ?? "(unnamed)");
            }
        }
        return result;
    }
}
