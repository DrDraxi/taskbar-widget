using System.Text.Json.Serialization;

namespace TaskbarWidget.Ordering;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<string>))]
internal partial class OrderJsonContext : JsonSerializerContext { }
