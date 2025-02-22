﻿using MultiFunPlayer.Settings.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace MultiFunPlayer.Common;

[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
[TypeConverter(typeof(DeviceAxisTypeConverter))]
public sealed class DeviceAxis
{
    private int _id;

    [JsonProperty] public string Name { get; init; }
    [JsonProperty] public float DefaultValue { get; init; }
    [JsonProperty] public string FriendlyName { get; init; }
    [JsonProperty] public IEnumerable<string> FunscriptNames { get; init; }

    public override string ToString() => Name;
    public override int GetHashCode() => _id;

    [OnDeserialized]
    internal void OnDeserializedMethod(StreamingContext context) => _id = _count++;

    private static int _count;
    private static int _outputMaximum;
    private static string _outputFormat;
    private static Dictionary<string, DeviceAxis> _axes;
    public static IReadOnlyCollection<DeviceAxis> All => _axes.Values;

    public static DeviceAxis Parse(string name) => _axes.GetValueOrDefault(name, null);
    public static IEnumerable<DeviceAxis> Parse(params string[] names) => names.Select(n => Parse(n));

    public static bool TryParse(string name, out DeviceAxis axis)
    {
        axis = Parse(name);
        return axis != null;
    }

    public static string ToString(DeviceAxis axis, float value) => $"{axis}{string.Format(_outputFormat, value * _outputMaximum)}";
    public static string ToString(DeviceAxis axis, float value, float interval) => $"{ToString(axis, value)}I{(int)Math.Floor(interval + 0.75f)}";

    public static string ToString(IEnumerable<KeyValuePair<DeviceAxis, float>> values, float interval)
        => $"{values.Aggregate(string.Empty, (s, x) => $"{s} {ToString(x.Key, x.Value, interval)}")}\n".TrimStart();

    public static bool IsDirty(float value, float lastValue)
        => float.IsFinite(value) && (!float.IsFinite(lastValue) || MathF.Abs(lastValue - value) * (_outputMaximum + 1) >= 1);

    public static void LoadSettings(JObject settings, JsonSerializer serializer)
    {
        if (!settings.TryGetValue<List<DeviceAxis>>("Axes", serializer, out var axes)
         || !settings.TryGetValue<int>("OutputPrecision", serializer, out var precision))
            throw new JsonReaderException("Unable to read device settings");

        _outputMaximum = (int)(MathF.Pow(10, precision) - 1);
        _outputFormat = $"{{0:{new string('0', precision)}}}";
        _axes = axes.ToDictionary(a => a.Name, a => a);
    }
}
