// Models/FfprobeData.cs

using System.Collections.Generic;
using Newtonsoft.Json;

namespace ffmpeg.Models
{
    public class FfprobeOutput
    {
        [JsonProperty("format")]
        public FfprobeFormat? Format { get; set; }

        [JsonProperty("streams")]
        public List<FfprobeStream>? Streams { get; set; }
    }

    public class FfprobeFormat
    {
        [JsonProperty("format_name")]
        public string? FormatName { get; set; }

        [JsonProperty("duration")]
        public string? Duration { get; set; }
    }

    public class FfprobeStream
    {
        [JsonProperty("codec_type")]
        public string? CodecType { get; set; }

        [JsonProperty("codec_name")]
        public string? CodecName { get; set; }
    }
}