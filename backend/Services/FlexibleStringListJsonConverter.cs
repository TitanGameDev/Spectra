using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spectra.Api.Services;

// PowerShell's ConvertTo-Json collapses a property whose value is an array
// with exactly one element down to a bare scalar, at any nesting depth —
// not just its top-level input — regardless of the array being explicitly
// constructed with @(...) on the PowerShell side (see Collect-ExoSecurityData.ps1
// and Collect-SccSecurityData.ps1's AccessRights/NotifyUser/etc. properties).
// A List<string>? property backed by one of those scripts' output can
// therefore arrive as either a JSON array of strings or a single JSON
// string, depending on how many values a given tenant happened to have —
// this accepts both instead of throwing on the single-value case.
public class FlexibleStringListJsonConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }

        var list = new List<string>();
        foreach (var element in JsonDocument.ParseValue(ref reader).RootElement.EnumerateArray())
        {
            var value = element.GetString();
            if (value is not null)
            {
                list.Add(value);
            }
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}
