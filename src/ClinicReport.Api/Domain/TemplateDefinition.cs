using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClinicReport.Api.Domain;

/// <summary>
/// A report template as stored in <c>TemplateVersion.DefinitionJson</c> and in the <c>templates/</c> folder.
/// One template describes one service (echo, ECG, ...): sections, fields, options, the phrase each
/// answer contributes to the narrative, and which fields only appear when another answer is chosen.
/// </summary>
public sealed class TemplateDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public string? Description { get; set; }
    public List<SectionDef> Sections { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // A misspelt property ("phrse") should fail loudly rather than vanish from the template.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static TemplateDefinition Parse(string json) =>
        JsonSerializer.Deserialize<TemplateDefinition>(json, JsonOptions)
        ?? throw new JsonException("Template JSON is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Every field in document order, children after their parent.</summary>
    public IEnumerable<FieldDef> AllFields() => Sections.SelectMany(s => Flatten(s.Fields));

    public static IEnumerable<FieldDef> Flatten(IEnumerable<FieldDef> fields)
    {
        foreach (var f in fields)
        {
            yield return f;
            foreach (var c in Flatten(f.Children))
                yield return c;
        }
    }
}

public sealed class SectionDef
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>When false the section's sentences are left out of the narrative (e.g. a measurements-only section).</summary>
    public bool Narrative { get; set; } = true;

    public List<FieldDef> Fields { get; set; } = [];

    /// <summary>Values applied by the "All normal" button: field id → value (string, string[] or number).</summary>
    public Dictionary<string, JsonElement>? NormalPreset { get; set; }
}

public static class FieldTypes
{
    public const string Choice = "choice";       // one option
    public const string Multi = "multi";         // any number of options
    public const string Number = "number";       // typed measurement
    public const string Computed = "computed";   // formula over other numbers
    public const string Text = "text";           // one line
    public const string TextArea = "textarea";   // paragraph

    public static readonly string[] All = [Choice, Multi, Number, Computed, Text, TextArea];
    public static bool IsNumeric(string type) => type is Number or Computed;
    public static bool HasOptions(string type) => type is Choice or Multi;
}

public sealed class FieldDef
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = FieldTypes.Choice;
    public string? Hint { get; set; }
    public string? Unit { get; set; }

    /// <summary>Decimal places for display. Defaults to 1 for computed fields; typed numbers keep what was entered.</summary>
    public int? Decimals { get; set; }

    public NormalRange? Normal { get; set; }

    /// <summary>Arithmetic over other numeric field ids and the built-ins height, weight, age (see <see cref="Formula"/>).</summary>
    public string? Formula { get; set; }

    public List<OptionDef> Options { get; set; } = [];

    /// <summary>
    /// Sentence template. Placeholders: {label} {value} {unit} {values}. An empty string means "say nothing".
    /// Numbers only reach the narrative when a phrase is given; they always reach the measurements table.
    /// </summary>
    public string? Phrase { get; set; }

    public Condition? ShowIf { get; set; }
    public bool Required { get; set; }

    /// <summary>Numeric fields appear in the measurements table unless this is false.</summary>
    public bool? Measurement { get; set; }

    public List<FieldDef> Children { get; set; } = [];
}

public sealed class OptionDef
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>
    /// Choice field: the sentence used when this option is chosen (overrides the field phrase).
    /// Multi field: the wording used for this option inside {values}, instead of the label.
    /// </summary>
    public string? Phrase { get; set; }

    /// <summary>Marks the answer as a finding worth carrying into the conclusion draft.</summary>
    public bool Abnormal { get; set; }
}

/// <summary>Show a field only when another field has a given answer.</summary>
public sealed class Condition
{
    public string Field { get; set; } = "";

    /// <summary>Choice: the selected value is one of these. Multi: any of these is selected.</summary>
    public List<string>? AnyOf { get; set; }

    /// <summary>The field has any value other than these (useful for "anything but normal").</summary>
    public List<string>? NoneOf { get; set; }

    /// <summary>Numeric field compared with a threshold.</summary>
    public double? GreaterThan { get; set; }
    public double? LessThan { get; set; }
}

public sealed class NormalRange
{
    public double? Min { get; set; }
    public double? Max { get; set; }
    public Bounds? Male { get; set; }
    public Bounds? Female { get; set; }

    /// <summary>Where the range comes from, shown to the person editing the template.</summary>
    public string? Source { get; set; }

    public Bounds? For(string? sex) => sex switch
    {
        "M" when Male is not null => Male,
        "F" when Female is not null => Female,
        _ when Min is not null || Max is not null => new Bounds { Min = Min, Max = Max },
        _ => null,
    };
}

public sealed class Bounds
{
    public double? Min { get; set; }
    public double? Max { get; set; }
}
