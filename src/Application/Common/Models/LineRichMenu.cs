using System.Text.Json.Serialization;

namespace ChatbotApi.Application.Common.Models;

public class LineRichMenu
{
    [JsonPropertyName("size")]
    public RichMenuSize Size { get; set; }

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("chatBarText")]
    public string ChatBarText { get; set; }

    [JsonPropertyName("areas")]
    public List<RichMenuArea> Areas { get; set; }
}

public class RichMenuSize
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public class RichMenuArea
{
    [JsonPropertyName("bounds")]
    public RichMenuBounds Bounds { get; set; }

    [JsonPropertyName("action")]
    public RichMenuAction Action { get; set; }
}

public class RichMenuBounds
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public class RichMenuAction
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
    
    [JsonPropertyName("displayText")]
    public string? DisplayText { get; set; }
    
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}