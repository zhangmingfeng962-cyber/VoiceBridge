using System.Text.Json.Serialization;

namespace VoiceBridge.Core;

[JsonConverter(typeof(JsonStringEnumConverter<MusicChannelMode>))]
public enum MusicChannelMode { Stereo, MonoAverage, LeftOnly, RightOnly }
