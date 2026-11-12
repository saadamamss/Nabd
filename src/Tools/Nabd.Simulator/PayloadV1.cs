using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nabd.Simulator;

public static class PayloadV1
{
    public static string Compose(long seq, DateTimeOffset ts, IReadOnlyList<(double Value, string Channel)> readings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("ts", ts.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteNumber("seq", seq);
            writer.WriteStartArray("rr");
            foreach (var (value, channel) in readings)
            {
                writer.WriteStartObject();
                writer.WriteNumber("t", value);
                writer.WriteString("c", channel);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}