using ClinicReport.Api.Domain;

namespace ClinicReport.Tests;

public class TemplateValidatorTests
{
    public static TheoryData<string> BundledTemplates()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "templates"), "*.json"))
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(BundledTemplates))]
    public void Bundled_templates_are_valid(string file)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "templates", file));
        Assert.Empty(TemplateValidator.Validate(json));
    }

    [Fact]
    public void Ships_the_echo_and_ecg_templates() =>
        Assert.Subset(
            Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "templates"), "*.json").Select(Path.GetFileName).ToHashSet(),
            new HashSet<string?> { "echo-tte.json", "ecg-12-lead.json" });

    private const string Minimal = """
        {
          "key": "demo", "name": "Demo", "service": "Demo",
          "sections": [ { "id": "s", "title": "S", "fields": [
            { "id": "size", "label": "Size", "type": "choice", "options": [ { "value": "normal", "label": "Normal" }, { "value": "big", "label": "Big" } ] },
            { "id": "len", "label": "Length", "type": "number", "unit": "mm" }
          ] } ]
        }
        """;

    private static List<string> ValidateWith(Action<TemplateDefinition> change)
    {
        var def = TemplateDefinition.Parse(Minimal);
        change(def);
        return TemplateValidator.Validate(def);
    }

    [Fact]
    public void Minimal_template_is_valid() => Assert.Empty(TemplateValidator.Validate(Minimal));

    [Fact]
    public void Broken_json_is_reported() =>
        Assert.Contains("Not valid template JSON", Assert.Single(TemplateValidator.Validate("{ \"key\": ")));

    [Fact]
    public void Duplicate_field_ids_are_rejected() =>
        Assert.Contains(ValidateWith(d => d.Sections[0].Fields[1].Id = "size"), e => e.Contains("used twice"));

    [Fact]
    public void Reserved_ids_are_rejected() =>
        Assert.Contains(ValidateWith(d => d.Sections[0].Fields[1].Id = "height"), e => e.Contains("reserved"));

    [Fact]
    public void Choice_without_options_is_rejected() =>
        Assert.Contains(ValidateWith(d => d.Sections[0].Fields[0].Options.Clear()), e => e.Contains("no options"));

    [Fact]
    public void Formula_must_reference_numeric_fields() =>
        Assert.Contains(ValidateWith(d => d.Sections[0].Fields.Add(new FieldDef
        {
            Id = "ratio", Label = "Ratio", Type = FieldTypes.Computed, Formula = "len / size",
        })), e => e.Contains("not a number field"));

    [Fact]
    public void Formula_cycles_are_rejected() =>
        Assert.Contains(ValidateWith(d =>
        {
            d.Sections[0].Fields.Add(new FieldDef { Id = "a", Label = "A", Type = FieldTypes.Computed, Formula = "b + 1" });
            d.Sections[0].Fields.Add(new FieldDef { Id = "b", Label = "B", Type = FieldTypes.Computed, Formula = "a + 1" });
        }), e => e.Contains("loop"));

    [Fact]
    public void ShowIf_values_must_be_real_options() =>
        Assert.Contains(ValidateWith(d => d.Sections[0].Fields[1].ShowIf = new Condition { Field = "size", AnyOf = ["huge"] }),
            e => e.Contains("not an option"));

    [Fact]
    public void Unknown_phrase_placeholders_are_rejected() =>
        Assert.Contains(ValidateWith(d => d.Sections[0].Fields[1].Phrase = "Length {lenght}"), e => e.Contains("placeholder"));

    [Fact]
    public void Preset_values_must_fit_the_field() =>
        Assert.Contains(ValidateWith(d => d.Sections[0].NormalPreset = new()
        {
            ["size"] = System.Text.Json.JsonDocument.Parse("\"tiny\"").RootElement,
        }), e => e.Contains("normalPreset"));
}
