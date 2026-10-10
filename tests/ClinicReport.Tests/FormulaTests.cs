using ClinicReport.Api.Domain;

namespace ClinicReport.Tests;

public class FormulaTests
{
    private static double? Eval(string formula, params (string Name, double? Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Name, v => v.Value);
        return Formula.Parse(formula).Evaluate(n => map.GetValueOrDefault(n));
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("2 ^ 3 ^ 2", 512)]      // right associative
    [InlineData("-2 ^ 2", 4)]           // unary minus binds tighter, like a calculator
    [InlineData("10 / 4", 2.5)]
    [InlineData("round(2.345, 2)", 2.35)]
    [InlineData("max(1, 5, 3) - min(4, 2)", 3)]
    [InlineData("sqrt(16) + abs(-1)", 5)]
    public void Evaluates_arithmetic(string formula, double expected) =>
        Assert.Equal(expected, Eval(formula)!.Value, 6);

    [Fact]
    public void Mosteller_bsa_from_height_and_weight()
    {
        var bsa = Eval("sqrt(height * weight / 3600)", ("height", 175), ("weight", 80));
        Assert.Equal(1.972, bsa!.Value, 3);
    }

    [Fact]
    public void Missing_input_gives_null_not_an_error() =>
        Assert.Null(Eval("la_volume / bsa", ("la_volume", 60)));

    [Fact]
    public void Division_by_zero_gives_null() =>
        Assert.Null(Eval("e_wave / a_wave", ("e_wave", 80), ("a_wave", 0)));

    [Fact]
    public void Avg_ignores_blank_arguments()
    {
        Assert.Equal(8, Eval("avg(e_sept, e_lat)", ("e_sept", 8), ("e_lat", null)));
        Assert.Null(Eval("avg(e_sept, e_lat)"));
    }

    [Fact]
    public void Collects_variable_names() =>
        Assert.Equal(["lvidd", "ivsd", "lvpwd"], Formula.Parse("((lvidd + ivsd + lvpwd) / 10) ^ 3").Variables);

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("exec(1)")]
    [InlineData("sqrt(1, 2)")]
    [InlineData("a b")]
    [InlineData("1 $ 2")]
    public void Rejects_bad_formulas(string formula) =>
        Assert.Throws<FormatException>(() => Formula.Parse(formula));
}
