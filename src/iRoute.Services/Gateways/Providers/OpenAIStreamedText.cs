using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using iRoute.Common;

namespace iRoute.Services;

internal sealed class OpenAIStreamedText(ModelGatewayRequest request)
{
    private readonly SortedDictionary<(int Output, int Content), TextPart> parts = [];

    public void Observe(string? type, JsonElement value)
    {
        switch (type)
        {
            case "response.output_text.delta":
                var delta = Part(value);
                if (delta.Final is not null) throw Invalid();
                delta.Deltas.Append(Text(value, "delta"));
                break;
            case "response.output_text.done":
                Finish(Part(value), Text(value, "text"));
                break;
            case "response.content_part.added":
            case "response.content_part.done":
                var content = value.GetProperty("part");
                EnsureText(content);
                if (type == "response.content_part.done") Finish(Part(value), Text(content, "text"));
                break;
            case "response.output_item.added":
            case "response.output_item.done":
                var item = value.GetProperty("item");
                var itemType = Text(item, "type");
                if (itemType == "reasoning") break;
                if (itemType != "message") throw Invalid();
                var index = 0;
                foreach (var part in item.GetProperty("content").EnumerateArray())
                {
                    EnsureText(part);
                    if (type == "response.output_item.done") Finish(Part(Index(value, "output_index"), index), Text(part, "text"));
                    index++;
                }
                break;
            case "response.refusal.delta":
            case "response.refusal.done":
            case "response.function_call_arguments.delta":
            case "response.function_call_arguments.done":
            case "response.custom_tool_call_input.delta":
            case "response.custom_tool_call_input.done":
                throw Invalid();
        }
    }

    public JsonElement Complete(JsonElement response)
    {
        if (Text(response, "status") != "completed") throw Invalid();
        var output = response.GetProperty("output");
        if (output.GetArrayLength() != 0)
        {
            foreach (var part in parts)
            {
                if (part.Key.Output >= output.GetArrayLength()) throw Invalid();
                var item = output[part.Key.Output];
                if (Text(item, "type") != "message") throw Invalid();
                var content = item.GetProperty("content");
                if (part.Key.Content >= content.GetArrayLength()) throw Invalid();
                EnsureText(content[part.Key.Content]);
                Finish(part.Value, Text(content[part.Key.Content], "text"));
            }
            return response.Clone();
        }
        if (parts.Count == 0) throw Invalid();
        var result = JsonNode.Parse(response.GetRawText())!.AsObject();
        var messages = new JsonArray();
        foreach (var group in parts.GroupBy(part => part.Key.Output))
        {
            var content = new JsonArray();
            foreach (var part in group)
                content.Add(new JsonObject { ["type"] = "output_text", ["text"] = part.Value.Final ?? part.Value.Deltas.ToString() });
            messages.Add(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = content });
        }
        result["output"] = messages;
        return JsonSerializer.SerializeToElement(result);
    }

    private TextPart Part(JsonElement value) => Part(Index(value, "output_index"), Index(value, "content_index"));

    private TextPart Part(int output, int content)
    {
        if (!parts.TryGetValue((output, content), out var part)) parts.Add((output, content), part = new TextPart());
        return part;
    }

    private int Index(JsonElement value, string name)
    {
        if (!value.GetProperty(name).TryGetInt32(out var index) || index is < 0 or > 1024) throw Invalid();
        return index;
    }

    private void Finish(TextPart part, string text)
    {
        if ((part.Final is not null && part.Final != text) || (part.Deltas.Length > 0 && part.Deltas.ToString() != text)) throw Invalid();
        part.Final = text;
    }

    private void EnsureText(JsonElement value)
    {
        if (Text(value, "type") != "output_text") throw Invalid();
    }

    private string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw Invalid();

    private ModelGatewayException Invalid() => ProviderResponse.Invalid(request,
        "The ChatGPT stream contained a refusal, unsupported output, or inconsistent text.");

    private sealed class TextPart
    {
        public StringBuilder Deltas { get; } = new();
        public string? Final { get; set; }
    }
}
