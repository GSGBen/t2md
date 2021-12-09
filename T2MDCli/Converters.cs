using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GoldenSyrupGames.T2MD
{
    /// <summary>
    /// Trello sometimes has e.g. pos as "bottom" instead of 123.45 or "123.45", so handle that.
    /// </summary>
    public class TrelloDoubleJsonConverter : JsonConverter<Double>
    {
        public override Double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string? stringValue = reader.GetString();
                // handle the weird values
                if (stringValue == "bottom")
                {
                    return double.MaxValue - 1;
                }
                else if (stringValue == "top")
                {
                    return 0.0;
                }
                // standard double as string
                else if (double.TryParse(stringValue, out double value))
                {
                    return value;
                }
                else
                {
                    throw new System.Text.Json.JsonException($"Couldn't parse the string `{stringValue}` as a double or as one of Trello's positions");
                }
            }
            // double without string
            else if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetDouble();
            }
            // else
            throw new System.Text.Json.JsonException();
        }

        public override void Write(Utf8JsonWriter writer, Double doubleValue, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(doubleValue);
        }
    }
}
