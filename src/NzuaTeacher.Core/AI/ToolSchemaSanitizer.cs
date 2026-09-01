using System.Text.Json;
using System.Text.Json.Nodes;

namespace NzuaTeacher.Core.AI;

/// <summary>
/// Приводить JSON-схеми MCP-інструментів до вигляду, який приймають усі провайдери.
/// Gemini через OpenAI-сумісний ендпоінт відхиляє ($schema, additionalProperties, type: [..., "null"])
/// з помилкою 400 Bad Request.
/// </summary>
public static class ToolSchemaSanitizer
{
    private static readonly string[] UnsupportedKeywords =
        ["$schema", "$id", "additionalProperties", "unevaluatedProperties", "default", "examples", "const"];

    public static JsonElement Sanitize(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        if (node is null) return schema;

        Clean(node);

        // Провайдери вимагають об'єкт із properties навіть для інструментів без аргументів.
        if (node is JsonObject root && root["type"]?.GetValue<string>() == "object" && root["properties"] is null)
            root["properties"] = new JsonObject();

        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }

    private static void Clean(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var keyword in UnsupportedKeywords)
                    obj.Remove(keyword);

                if (obj["type"] is JsonArray types)
                {
                    var primary = types
                        .Select(t => t?.GetValue<string>())
                        .FirstOrDefault(t => t is not null && t != "null");
                    if (primary is not null) obj["type"] = primary;
                    else obj.Remove("type");
                }

                foreach (var child in obj.Select(kv => kv.Value).ToList())
                    if (child is not null) Clean(child);
                break;

            case JsonArray array:
                foreach (var item in array.ToList())
                    if (item is not null) Clean(item);
                break;
        }
    }
}
