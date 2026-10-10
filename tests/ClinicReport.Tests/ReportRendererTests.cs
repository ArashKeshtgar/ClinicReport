using System.Text.Json;
using ClinicReport.Api.Domain;

namespace ClinicReport.Tests;

public class ReportRendererTests
{
    private static readonly TemplateDefinition Tte = TemplateDefinition.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "templates", "echo-tte.json")));

    private static readonly RenderContext Male = new("M", HeightCm: 175, WeightKg: 80, AgeYears: 60);

    private static ReportValues Values(string json) => ReportValues.FromJson(json);

    private static IReadOnlyList<string> Sentences(RenderedReport r, string section) =>
        r.Sections.Single(s => s.Id == section).Sentences;

    [Fact]
    public void Choice_options_produce_their_phrase()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "lv_size": "normal", "lv_function": "moderate", "lvef": 38 }"""), Male);

        Assert.Equal(
        [
            "The left ventricle is normal in size.",
            "Left ventricular systolic function is moderately reduced.",
            "The ejection fraction is estimated at 38%.",
        ], Sentences(r, "lv"));
    }

    [Fact]
    public void Abnormal_answers_and_flagged_numbers_feed_the_conclusion_draft()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "lv_size": "normal", "lv_function": "moderate", "lvef": 38 }"""), Male);

        Assert.Equal(
        [
            "Left ventricular systolic function is moderately reduced.",
            "The ejection fraction is estimated at 38%.",
        ], r.AbnormalFindings);
    }

    [Fact]
    public void Empty_phrase_says_nothing()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "quality": "good", "rhythm": "sinus" }"""), Male);
        Assert.Empty(Sentences(r, "study"));
    }

    [Fact]
    public void Hidden_children_are_ignored_even_with_stale_values()
    {
        // The typist picked "abnormal" + findings, then changed back to "normal".
        var r = ReportRenderer.Render(Tte, Values("""{ "mv_morph": "normal", "mv_findings": ["thickened", "calcified"] }"""), Male);
        Assert.Equal(["The mitral valve is structurally normal."], Sentences(r, "mitral"));
    }

    [Fact]
    public void Multi_options_are_joined_into_one_sentence()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "mv_morph": "abnormal", "mv_findings": ["thickened", "calcified", "mac"] }"""), Male);
        Assert.Equal(["The mitral valve shows thickened leaflets, calcified leaflets and mitral annular calcification."], Sentences(r, "mitral"));
    }

    [Fact]
    public void NoneOf_condition_shows_child_for_any_other_answer()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "ms": "moderate", "mv_mean_grad": 8 }"""), Male);
        Assert.Equal(["There is moderate mitral stenosis.", "The mean transmitral gradient is 8 mmHg."], Sentences(r, "mitral"));

        var none = ReportRenderer.Render(Tte, Values("""{ "ms": "none", "mv_mean_grad": 8 }"""), Male);
        Assert.Empty(Sentences(none, "mitral"));
    }

    [Fact]
    public void Nested_conditions_follow_the_chain()
    {
        // tamponade depends on effusion_size, which depends on pericardium.
        var shown = ReportRenderer.Render(Tte, Values("""{ "pericardium": "effusion", "effusion_size": "large", "tamponade": "yes" }"""), Male);
        Assert.Contains("There are echocardiographic features of tamponade.", Sentences(shown, "pericardium"));

        var parentChanged = ReportRenderer.Render(Tte, Values("""{ "pericardium": "normal", "effusion_size": "large", "tamponade": "yes" }"""), Male);
        Assert.Equal(["There is no pericardial effusion."], Sentences(parentChanged, "pericardium"));
    }

    [Fact]
    public void Computes_bsa_indexed_values_and_flags_by_sex()
    {
        var values = Values("""{ "ivsd": 12, "lvidd": 50, "lvpwd": 11, "la_volume": 80 }""");
        var male = ReportRenderer.Render(Tte, values, Male);

        Assert.Equal(1.97, male.Computed["bsa"]!.Value, 2);
        Assert.Equal(0.44, male.Computed["rwt"]!.Value, 2);
        Assert.Equal(41, male.Computed["lavi"]!.Value, 0);

        var ivsd = male.Measurements.Single(m => m.FieldId == "ivsd");
        Assert.Equal(("12", "6–10", "H"), (ivsd.Display, ivsd.Range, ivsd.Flag));
        Assert.Equal("H", male.Measurements.Single(m => m.FieldId == "lavi").Flag);

        var female = ReportRenderer.Render(Tte, Values("""{ "lvidd": 55 }"""), Male with { Sex = "F" });
        Assert.Equal(("38–52", "H"), (female.Measurements.Single(m => m.FieldId == "lvidd").Range, female.Measurements.Single(m => m.FieldId == "lvidd").Flag));
    }

    [Fact]
    public void Unknown_sex_shows_no_sex_specific_range()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "lvidd": 55 }"""), Male with { Sex = "U" });
        var m = r.Measurements.Single(m => m.FieldId == "lvidd");
        Assert.Null(m.Range);
        Assert.Null(m.Flag);
    }

    [Fact]
    public void Rvsp_uses_tr_velocity_and_ra_pressure()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "tr": "mild", "tr_vmax": 3.2, "rap": 8 }"""), Male);
        Assert.Equal(49, r.Computed["rvsp"]!.Value, 1);
        Assert.Contains("Estimated right ventricular systolic pressure is 49 mmHg.", Sentences(r, "tricuspid"));
        Assert.Contains("Estimated right ventricular systolic pressure is 49 mmHg.", r.AbnormalFindings);
    }

    [Fact]
    public void Measurements_section_stays_out_of_the_narrative()
    {
        var r = ReportRenderer.Render(Tte, Values("""{ "lvidd": 50, "tapse": 20 }"""), Male);
        Assert.Empty(Sentences(r, "measurements"));
        Assert.Equal(["bsa", "lvidd", "tapse"], r.Measurements.Select(m => m.FieldId));
    }

    [Fact]
    public void Missing_required_fields_are_listed()
    {
        var r = ReportRenderer.Render(Tte, Values("{}"), Male);
        Assert.Contains("Left ventricle: Systolic function", r.MissingRequired);
        Assert.Contains("Pericardium: Pericardium", r.MissingRequired);
    }

    [Fact]
    public void Normal_presets_render_a_complete_normal_study()
    {
        var values = new ReportValues();
        foreach (var s in Tte.Sections.Where(s => s.NormalPreset is not null))
            foreach (var (id, v) in s.NormalPreset!)
                values.Set(id, v);

        var r = ReportRenderer.Render(Tte, values, Male);

        Assert.Empty(r.MissingRequired);
        Assert.Empty(r.AbnormalFindings);
        Assert.Contains("Left ventricular systolic function is normal.", Sentences(r, "lv"));
    }

    [Fact]
    public void Blank_values_clear_the_answer()
    {
        var v = Values("""{ "lv_size": "normal" }""");
        v.Set("lv_size", JsonDocument.Parse("\"\"").RootElement);
        Assert.False(v.Has("lv_size"));
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "a", "b" }, "a and b")]
    [InlineData(new[] { "a", "b", "c" }, "a, b and c")]
    public void Joins_lists_in_english(string[] items, string expected) =>
        Assert.Equal(expected, ReportRenderer.JoinList(items));
}
