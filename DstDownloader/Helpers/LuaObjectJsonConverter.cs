using System.Text.Json;
using System.Text.Json.Serialization;
using MoonSharp.Interpreter;

namespace DstDownloaders.Helpers;

public class LuaObjectJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {


        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDouble(),
            _ => null,
        };
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if(value.GetType() == typeof(string))
        {
            writer.WriteStringValue(value.ToString());
        }
        else if(value.GetType() == typeof(bool))
        {
            writer.WriteBooleanValue((bool)value);
        }
        else if(value.GetType() == typeof(double))
        {
            writer.WriteNumberValue((double)value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
