using System.Text.Json;
using NzuaTeacher.Core.AI;
using Xunit;

namespace NzuaTeacher.Tests;

public class ToolSchemaSanitizerTests
{
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void RemovesSchemaKeyword_RejectedByGemini()
    {
        var result = ToolSchemaSanitizer.Sanitize(Parse("""
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{}}
            """));

        Assert.False(result.TryGetProperty("$schema", out _));
    }

    [Fact]
    public void CollapsesNullableTypeArrays()
    {
        var result = ToolSchemaSanitizer.Sanitize(Parse("""
            {"type":"object","properties":{"semesterId":{"type":["string","null"]}}}
            """));

        var prop = result.GetProperty("properties").GetProperty("semesterId");
        Assert.Equal(JsonValueKind.String, prop.GetProperty("type").ValueKind);
        Assert.Equal("string", prop.GetProperty("type").GetString());
    }

    [Fact]
    public void RemovesAdditionalPropertiesAndDefaults()
    {
        var result = ToolSchemaSanitizer.Sanitize(Parse("""
            {"type":"object","additionalProperties":false,"properties":{"page":{"type":"integer","default":1}}}
            """));

        Assert.False(result.TryGetProperty("additionalProperties", out _));
        Assert.False(result.GetProperty("properties").GetProperty("page").TryGetProperty("default", out _));
    }

    [Fact]
    public void AddsPropertiesForArgumentlessTools()
    {
        var result = ToolSchemaSanitizer.Sanitize(Parse("""{"type":"object"}"""));

        Assert.Equal(JsonValueKind.Object, result.GetProperty("properties").ValueKind);
    }

    [Fact]
    public void KeepsRequiredEnumAndNestedStructures()
    {
        var result = ToolSchemaSanitizer.Sanitize(Parse("""
            {"$schema":"x","type":"object","required":["action"],
             "properties":{
               "action":{"type":"string","enum":["status","login"]},
               "entries":{"type":"array","items":{"type":"object","additionalProperties":false,
                          "properties":{"mark":{"type":["integer","null"]}}}}}}
            """));

        Assert.Equal("action", result.GetProperty("required")[0].GetString());
        Assert.Equal(2, result.GetProperty("properties").GetProperty("action").GetProperty("enum").GetArrayLength());

        var item = result.GetProperty("properties").GetProperty("entries").GetProperty("items");
        Assert.False(item.TryGetProperty("additionalProperties", out _));
        Assert.Equal("integer", item.GetProperty("properties").GetProperty("mark").GetProperty("type").GetString());
    }
}
