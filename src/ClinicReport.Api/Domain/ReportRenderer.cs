using System.Globalization;
using System.Text.RegularExpressions;

namespace ClinicReport.Api.Domain;

/// <summary>Patient and exam facts a template can use: sex picks the normal range, the rest feed formulas.</summary>
public sealed record RenderContext(string? Sex, double? HeightCm, double? WeightKg, double? AgeYears);

public sealed record RenderedSection(string Id, string Title, IReadOnlyList<string> Sentences);

public sealed record Measurement(
    string FieldId, string Section, string Label, double Value, string Display, string? Unit, string? Range, string? Flag);

public sealed record RenderedReport(
    IReadOnlyList<RenderedSection> Sections,
    IReadOnlyList<Measurement> Measurements,
    IReadOnlyDictionary<string, double?> Computed,
    IReadOnlyList<string> AbnormalFindings,
    IReadOnlyList<string> MissingRequired);

/// <summary>
/// Turns a template plus answers into the report a referring physician reads: one paragraph per section,
/// a measurements table with normal ranges and H/L flags, and the computed values. Fields hidden by
/// showIf never contribute, even if they still hold an old answer.
/// </summary>
public sealed partial class ReportRenderer
{
    private readonly TemplateDefinition _def;
    private readonly ReportValues _values;
    private readonly RenderContext _ctx;
    private readonly Dictionary<string, FieldDef> _fields;
    private readonly Dictionary<string, FieldDef?> _parents = new();
    private readonly Dictionary<string, bool> _visible = new();
    private readonly Dictionary<string, double?> _computed = new();
    private readonly HashSet<string> _inProgress = new();

    private ReportRenderer(TemplateDefinition def, ReportValues values, RenderContext ctx)
    {
        _def = def;
        _values = values;
        _ctx = ctx;
        _fields = def.AllFields().GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var s in def.Sections)
            IndexParents(s.Fields, null);
    }

    public static RenderedReport Render(TemplateDefinition def, ReportValues values, RenderContext ctx) =>
        new ReportRenderer(def, values, ctx).Render();

    private void IndexParents(IEnumerable<FieldDef> fields, FieldDef? parent)
    {
        foreach (var f in fields)
        {
            _parents.TryAdd(f.Id, parent);
            IndexParents(f.Children, f);
        }
    }

    private RenderedReport Render()
    {
        var sections = new List<RenderedSection>();
        var measurements = new List<Measurement>();
        var abnormal = new List<string>();
        var missing = new List<string>();

        foreach (var s in _def.Sections)
        {
            var sentences = new List<string>();
            foreach (var f in TemplateDefinition.Flatten(s.Fields).Where(f => IsVisible(f.Id)))
            {
                if (f.Required && f.Type != FieldTypes.Computed && !_values.Has(f.Id))
                    missing.Add($"{s.Title}: {f.Label}");

                var (sentence, isAbnormal) = Describe(f);
                if (sentence is not null)
                {
                    if (s.Narrative)
                        sentences.Add(sentence);
                    if (isAbnormal)
                        abnormal.Add(sentence);
                }

                if (FieldTypes.IsNumeric(f.Type) && f.Measurement != false && NumberOf(f.Id) is { } value)
                {
                    var bounds = f.Normal?.For(_ctx.Sex);
                    measurements.Add(new Measurement(
                        f.Id, s.Title, f.Label, value, Format(f, value), f.Unit, RangeText(bounds), Flag(bounds, value)));
                }
            }
            sections.Add(new RenderedSection(s.Id, s.Title, sentences));
        }

        var computed = _fields.Values.Where(f => f.Type == FieldTypes.Computed)
            .ToDictionary(f => f.Id, f => IsVisible(f.Id) ? NumberOf(f.Id) : null);

        return new RenderedReport(sections, measurements, computed, abnormal, missing);
    }

    /// <summary>The sentence a field contributes, or null when it has nothing to say.</summary>
    private (string? Sentence, bool Abnormal) Describe(FieldDef f)
    {
        switch (f.Type)
        {
            case FieldTypes.Choice:
            {
                var opt = f.Options.FirstOrDefault(o => o.Value == _values.GetString(f.Id));
                if (opt is null)
                    return (null, false);
                var phrase = opt.Phrase ?? f.Phrase ?? "{label}: {value}.";
                return (Fill(phrase, f, opt.Label, opt.Label), opt.Abnormal);
            }
            case FieldTypes.Multi:
            {
                var selected = _values.GetStrings(f.Id);
                var opts = f.Options.Where(o => selected.Contains(o.Value)).ToList();
                if (opts.Count == 0)
                    return (null, false);
                var list = JoinList(opts.Select(o => o.Phrase ?? o.Label));
                return (Fill(f.Phrase ?? "{label}: {values}.", f, list, list), opts.Any(o => o.Abnormal));
            }
            case FieldTypes.Number or FieldTypes.Computed:
            {
                if (f.Phrase is null || NumberOf(f.Id) is not { } value)
                    return (null, false);
                var flagged = Flag(f.Normal?.For(_ctx.Sex), value) is not null;
                return (Fill(f.Phrase, f, Format(f, value), Format(f, value)), flagged);
            }
            case FieldTypes.Text:
            {
                var text = _values.GetString(f.Id)?.Trim();
                return string.IsNullOrEmpty(text) ? (null, false) : (Fill(f.Phrase ?? "{label}: {value}.", f, text, text), false);
            }
            case FieldTypes.TextArea:
            {
                var text = _values.GetString(f.Id)?.Trim();
                return string.IsNullOrEmpty(text) ? (null, false) : (Fill(f.Phrase ?? "{value}", f, text, text), false);
            }
            default:
                return (null, false);
        }
    }

    private static string? Fill(string phrase, FieldDef f, string value, string values)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return null;
        var s = phrase
            .Replace("{label}", f.Label)
            .Replace("{value}", value)
            .Replace("{values}", values)
            .Replace("{unit}", f.Unit ?? "");
        s = Spaces().Replace(s, " ").Replace(" .", ".").Replace(" ,", ",").Trim();
        if (s.Length == 0)
            return null;
        s = char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
        return s[^1] is '.' or '!' or '?' or ':' ? s : s + ".";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string JoinList(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list[..^1]) + " and " + list[^1],
        };
    }

    public bool IsVisible(string fieldId)
    {
        if (_visible.TryGetValue(fieldId, out var cached))
            return cached;
        if (!_fields.TryGetValue(fieldId, out var f) || !_inProgress.Add("v:" + fieldId))
            return false;

        var visible = (_parents.GetValueOrDefault(fieldId) is not { } parent || IsVisible(parent.Id))
            && (f.ShowIf is null || Satisfies(f.ShowIf));

        _inProgress.Remove("v:" + fieldId);
        _visible[fieldId] = visible;
        return visible;
    }

    private bool Satisfies(Condition c)
    {
        if (!_fields.TryGetValue(c.Field, out var target) || !IsVisible(target.Id))
            return false;

        if (FieldTypes.IsNumeric(target.Type))
        {
            if (NumberOf(target.Id) is not { } n)
                return false;
            if (c.GreaterThan is { } gt && !(n > gt)) return false;
            if (c.LessThan is { } lt && !(n < lt)) return false;
            return true;
        }

        var selected = target.Type == FieldTypes.Multi
            ? _values.GetStrings(target.Id)
            : _values.GetString(target.Id) is { Length: > 0 } s ? [s] : [];
        if (selected.Count == 0)
            return false;
        if (c.AnyOf is not null && !selected.Any(c.AnyOf.Contains))
            return false;
        if (c.NoneOf is not null && selected.All(c.NoneOf.Contains))
            return false;
        return true;
    }

    /// <summary>A typed or computed number, but only if its field is showing.</summary>
    private double? NumberOf(string fieldId)
    {
        if (!_fields.TryGetValue(fieldId, out var f) || !FieldTypes.IsNumeric(f.Type))
            return null;
        if (f.Type == FieldTypes.Number)
            return IsVisible(fieldId) ? _values.GetNumber(fieldId) : null;

        if (_computed.TryGetValue(fieldId, out var cached))
            return cached;
        if (!_inProgress.Add("c:" + fieldId))
            return null;
        double? result = null;
        if (IsVisible(fieldId) && f.Formula is not null)
        {
            try
            {
                result = Formula.Parse(f.Formula).Evaluate(Resolve);
            }
            catch (FormatException)
            {
                result = null; // a published template is validated; drafts may be mid-edit
            }
        }
        _inProgress.Remove("c:" + fieldId);
        _computed[fieldId] = result;
        return result;
    }

    private double? Resolve(string name) => name switch
    {
        "height" => _ctx.HeightCm,
        "weight" => _ctx.WeightKg,
        "age" => _ctx.AgeYears,
        _ => NumberOf(name),
    };

    public static string Format(FieldDef f, double value)
    {
        var decimals = f.Decimals ?? (f.Type == FieldTypes.Computed ? 1 : (int?)null);
        return decimals is { } d
            ? value.ToString("F" + d, CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string? Flag(Bounds? b, double value) =>
        b is null ? null
        : b.Min is { } min && value < min ? "L"
        : b.Max is { } max && value > max ? "H"
        : null;

    private static string? RangeText(Bounds? b) => b switch
    {
        { Min: { } min, Max: { } max } => $"{Num(min)}–{Num(max)}",
        { Min: { } min } => $"≥ {Num(min)}",
        { Max: { } max } => $"≤ {Num(max)}",
        _ => null,
    };

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
