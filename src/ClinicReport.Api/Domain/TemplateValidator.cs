using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClinicReport.Api.Domain;

/// <summary>
/// Checks a template before it can be saved or published, so a broken template never reaches the
/// person typing a report. Returns human-readable messages; an empty list means the template is valid.
/// </summary>
public static partial class TemplateValidator
{
    /// <summary>Numbers every formula can use besides other fields: the exam's height (cm), weight (kg) and the patient's age (years).</summary>
    public static readonly string[] BuiltIns = ["height", "weight", "age"];

    private static readonly string[] Placeholders = ["label", "value", "unit", "values"];

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex FieldIdPattern();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex PlaceholderPattern();

    public static List<string> Validate(string json)
    {
        TemplateDefinition def;
        try
        {
            def = TemplateDefinition.Parse(json);
        }
        catch (JsonException ex)
        {
            return [$"Not valid template JSON: {ex.Message}"];
        }
        return Validate(def);
    }

    public static List<string> Validate(TemplateDefinition def)
    {
        var errors = new List<string>();

        if (!SlugPattern().IsMatch(def.Key ?? ""))
            errors.Add("key must be lower-case letters, digits and dashes (e.g. \"echo-tte\").");
        if (string.IsNullOrWhiteSpace(def.Name))
            errors.Add("name is required.");
        if (string.IsNullOrWhiteSpace(def.Service))
            errors.Add("service is required.");
        if (def.Sections.Count == 0)
            errors.Add("A template needs at least one section.");

        var sectionIds = new HashSet<string>();
        foreach (var s in def.Sections)
        {
            if (!SlugPattern().IsMatch(s.Id ?? "") && !FieldIdPattern().IsMatch(s.Id ?? ""))
                errors.Add($"Section \"{s.Title}\": id \"{s.Id}\" must be lower-case letters, digits, dashes or underscores.");
            else if (!sectionIds.Add(s.Id!))
                errors.Add($"Section id \"{s.Id}\" is used twice.");
            if (string.IsNullOrWhiteSpace(s.Title))
                errors.Add($"Section \"{s.Id}\" needs a title.");
            if (s.Fields.Count == 0)
                errors.Add($"Section \"{s.Id}\" has no fields.");
        }

        var all = def.AllFields().ToList();
        var byId = new Dictionary<string, FieldDef>(StringComparer.Ordinal);
        foreach (var f in all)
        {
            var where = $"Field \"{f.Id}\"";
            if (!FieldIdPattern().IsMatch(f.Id ?? ""))
            {
                errors.Add($"Field \"{f.Label}\": id \"{f.Id}\" must start with a letter and use only a-z, 0-9 and _.");
                continue;
            }
            if (BuiltIns.Contains(f.Id) || Formula.Functions.Contains(f.Id))
                errors.Add($"{where}: \"{f.Id}\" is reserved; pick another id.");
            if (!byId.TryAdd(f.Id!, f))
                errors.Add($"{where} is used twice; field ids must be unique across the template.");
        }

        foreach (var f in all.Where(f => FieldIdPattern().IsMatch(f.Id ?? "")))
            ValidateField(f, byId, errors);

        ValidateFormulaCycles(all, byId, errors);

        foreach (var s in def.Sections.Where(s => s.NormalPreset is not null))
            foreach (var (fieldId, value) in s.NormalPreset!)
                ValidatePresetValue(s, fieldId, value, byId, errors);

        return errors;
    }

    private static void ValidateField(FieldDef f, Dictionary<string, FieldDef> byId, List<string> errors)
    {
        var where = $"Field \"{f.Id}\"";
        if (string.IsNullOrWhiteSpace(f.Label))
            errors.Add($"{where} needs a label.");
        if (!FieldTypes.All.Contains(f.Type))
        {
            errors.Add($"{where}: type \"{f.Type}\" is not one of {string.Join(", ", FieldTypes.All)}.");
            return;
        }

        if (FieldTypes.HasOptions(f.Type))
        {
            if (f.Options.Count == 0)
                errors.Add($"{where} is a {f.Type} field but has no options.");
            var seen = new HashSet<string>();
            foreach (var o in f.Options)
            {
                if (string.IsNullOrWhiteSpace(o.Value))
                    errors.Add($"{where}: every option needs a value.");
                else if (!seen.Add(o.Value))
                    errors.Add($"{where}: option value \"{o.Value}\" is used twice.");
                if (string.IsNullOrWhiteSpace(o.Label))
                    errors.Add($"{where}: option \"{o.Value}\" needs a label.");
                CheckPlaceholders(o.Phrase, $"{where}, option \"{o.Value}\"", errors);
            }
        }
        else if (f.Options.Count > 0)
        {
            errors.Add($"{where}: only choice and multi fields have options.");
        }

        if (f.Type == FieldTypes.Computed)
        {
            if (string.IsNullOrWhiteSpace(f.Formula))
                errors.Add($"{where} is computed but has no formula.");
            else
            {
                try
                {
                    var formula = Formula.Parse(f.Formula);
                    foreach (var v in formula.Variables)
                    {
                        if (BuiltIns.Contains(v))
                            continue;
                        if (!byId.TryGetValue(v, out var target))
                            errors.Add($"{where}: formula uses \"{v}\", which is not a field or one of {string.Join(", ", BuiltIns)}.");
                        else if (!FieldTypes.IsNumeric(target.Type))
                            errors.Add($"{where}: formula uses \"{v}\", which is not a number field.");
                    }
                }
                catch (FormatException ex)
                {
                    errors.Add($"{where}: formula error - {ex.Message}");
                }
            }
        }
        else if (f.Formula is not null)
        {
            errors.Add($"{where}: only computed fields have a formula.");
        }

        if (f.Normal is { } n)
        {
            if (!FieldTypes.IsNumeric(f.Type))
                errors.Add($"{where}: only number and computed fields have a normal range.");
            foreach (var (label, b) in new[] { ("", new Bounds { Min = n.Min, Max = n.Max }), (" (male)", n.Male), (" (female)", n.Female) })
                if (b is { Min: { } min, Max: { } max } && min > max)
                    errors.Add($"{where}: normal range{label} has min greater than max.");
        }

        if (f.Decimals is < 0 or > 6)
            errors.Add($"{where}: decimals must be between 0 and 6.");

        CheckPlaceholders(f.Phrase, where, errors);

        if (f.ShowIf is { } c)
        {
            if (c.Field == f.Id)
                errors.Add($"{where}: showIf cannot depend on the field itself.");
            else if (!byId.TryGetValue(c.Field, out var target))
                errors.Add($"{where}: showIf refers to unknown field \"{c.Field}\".");
            else
            {
                if (c.AnyOf is null && c.NoneOf is null && c.GreaterThan is null && c.LessThan is null)
                    errors.Add($"{where}: showIf needs anyOf, noneOf, greaterThan or lessThan.");
                foreach (var value in (c.AnyOf ?? []).Concat(c.NoneOf ?? []))
                    if (FieldTypes.HasOptions(target.Type) && target.Options.All(o => o.Value != value))
                        errors.Add($"{where}: showIf value \"{value}\" is not an option of \"{target.Id}\".");
                if ((c.GreaterThan is not null || c.LessThan is not null) && !FieldTypes.IsNumeric(target.Type))
                    errors.Add($"{where}: greaterThan/lessThan need a number field, but \"{target.Id}\" is {target.Type}.");
            }
        }
    }

    private static void CheckPlaceholders(string? phrase, string where, List<string> errors)
    {
        if (phrase is null)
            return;
        foreach (Match m in PlaceholderPattern().Matches(phrase))
            if (!Placeholders.Contains(m.Groups[1].Value))
                errors.Add($"{where}: unknown placeholder {{{m.Groups[1].Value}}} in phrase; use {string.Join(", ", Placeholders.Select(p => "{" + p + "}"))}.");
    }

    private static void ValidateFormulaCycles(List<FieldDef> all, Dictionary<string, FieldDef> byId, List<string> errors)
    {
        var deps = new Dictionary<string, IReadOnlySet<string>>();
        foreach (var f in all.Where(f => f.Type == FieldTypes.Computed && f.Formula is not null))
        {
            try { deps[f.Id] = Formula.Parse(f.Formula!).Variables; }
            catch (FormatException) { /* already reported */ }
        }

        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done
        bool Visit(string id)
        {
            if (state.TryGetValue(id, out var s))
                return s == 1;
            state[id] = 1;
            if (deps.TryGetValue(id, out var next) && next.Any(n => deps.ContainsKey(n) && Visit(n)))
                return true;
            state[id] = 2;
            return false;
        }

        foreach (var id in deps.Keys)
            if (Visit(id))
            {
                errors.Add($"Field \"{id}\": computed formulas refer to each other in a loop.");
                return;
            }
    }

    private static void ValidatePresetValue(SectionDef s, string fieldId, JsonElement value, Dictionary<string, FieldDef> byId, List<string> errors)
    {
        var where = $"Section \"{s.Id}\" normalPreset";
        if (!byId.TryGetValue(fieldId, out var f))
        {
            errors.Add($"{where}: unknown field \"{fieldId}\".");
            return;
        }
        var ok = f.Type switch
        {
            FieldTypes.Choice => value.ValueKind == JsonValueKind.String && f.Options.Any(o => o.Value == value.GetString()),
            FieldTypes.Multi => value.ValueKind == JsonValueKind.Array
                && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String && f.Options.Any(o => o.Value == e.GetString())),
            FieldTypes.Number => value.ValueKind == JsonValueKind.Number,
            FieldTypes.Text or FieldTypes.TextArea => value.ValueKind == JsonValueKind.String,
            _ => false,
        };
        if (!ok)
            errors.Add($"{where}: value for \"{fieldId}\" does not fit a {f.Type} field{(FieldTypes.HasOptions(f.Type) ? " (use option values)" : "")}.");
    }
}
