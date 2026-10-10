using System.Globalization;
using System.Text.Json;

namespace ClinicReport.Api.Domain;

/// <summary>
/// The answers of one report: field id → JSON value. Choice and text fields hold a string, multi fields a
/// string array, number fields a number. Stored as one JSON object in <c>Report.ValuesJson</c>.
/// </summary>
public sealed class ReportValues
{
    private readonly Dictionary<string, JsonElement> _values;

    public ReportValues(Dictionary<string, JsonElement>? values = null) =>
        _values = values is null ? new(StringComparer.Ordinal) : new(values, StringComparer.Ordinal);

    public static ReportValues FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new()
            : new(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, TemplateDefinition.JsonOptions));

    public string ToJson() => JsonSerializer.Serialize(_values, TemplateDefinition.JsonOptions);

    public IReadOnlyDictionary<string, JsonElement> Raw => _values;

    /// <summary>Sets a value; null, an empty string or an empty array clears it.</summary>
    public void Set(string fieldId, JsonElement? value)
    {
        if (value is not { } v || IsBlank(v))
            _values.Remove(fieldId);
        else
            _values[fieldId] = v.Clone();
    }

    public static bool IsBlank(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        _ => false,
    };

    public bool Has(string fieldId) => _values.ContainsKey(fieldId);

    public string? GetString(string fieldId) =>
        _values.TryGetValue(fieldId, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public IReadOnlyList<string> GetStrings(string fieldId)
    {
        if (!_values.TryGetValue(fieldId, out var v))
            return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
            JsonValueKind.String => [v.GetString()!],
            _ => [],
        };
    }

    public double? GetNumber(string fieldId)
    {
        if (!_values.TryGetValue(fieldId, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }
}
