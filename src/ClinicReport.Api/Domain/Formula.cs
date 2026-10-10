using System.Globalization;

namespace ClinicReport.Api.Domain;

/// <summary>
/// A small, safe arithmetic language for computed fields: numbers, identifiers (other field ids or the
/// built-ins), + - * / ^, parentheses and the functions sqrt, abs, min, max, avg, round(x, digits).
/// No reflection, no code execution. A missing input or a division by zero yields null, not an error,
/// because half-filled reports are the normal state.
/// </summary>
public sealed class Formula
{
    public static readonly string[] Functions = ["sqrt", "abs", "min", "max", "avg", "round"];

    private readonly Node _root;
    public string Source { get; }
    public IReadOnlySet<string> Variables { get; }

    private Formula(string source, Node root, HashSet<string> variables)
    {
        Source = source;
        _root = root;
        Variables = variables;
    }

    public static Formula Parse(string source)
    {
        var parser = new Parser(source);
        var root = parser.ParseExpression();
        parser.ExpectEnd();
        return new Formula(source, root, parser.Variables);
    }

    public double? Evaluate(Func<string, double?> resolve)
    {
        var result = _root.Eval(resolve);
        return result is { } v && (double.IsNaN(v) || double.IsInfinity(v)) ? null : result;
    }

    private abstract record Node
    {
        public abstract double? Eval(Func<string, double?> resolve);
    }

    private sealed record Num(double Value) : Node
    {
        public override double? Eval(Func<string, double?> resolve) => Value;
    }

    private sealed record Var(string Name) : Node
    {
        public override double? Eval(Func<string, double?> resolve) => resolve(Name);
    }

    private sealed record Neg(Node Operand) : Node
    {
        public override double? Eval(Func<string, double?> resolve) => -Operand.Eval(resolve);
    }

    private sealed record Binary(char Op, Node Left, Node Right) : Node
    {
        public override double? Eval(Func<string, double?> resolve)
        {
            if (Left.Eval(resolve) is not { } a || Right.Eval(resolve) is not { } b)
                return null;
            return Op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => b == 0 ? null : a / b,
                '^' => Math.Pow(a, b),
                _ => throw new InvalidOperationException($"Unknown operator {Op}"),
            };
        }
    }

    private sealed record Call(string Name, Node[] Args) : Node
    {
        public override double? Eval(Func<string, double?> resolve)
        {
            var values = Args.Select(a => a.Eval(resolve)).ToArray();
            switch (Name)
            {
                case "avg":
                    // Averages ignore blanks: e' septal alone still gives an E/e' when lateral was not measured.
                    var present = values.Where(v => v is not null).Select(v => v!.Value).ToArray();
                    return present.Length == 0 ? null : present.Average();
                case "round":
                    if (values[0] is not { } x) return null;
                    var digits = values.Length > 1 && values[1] is { } d ? (int)d : 0;
                    return Math.Round(x, Math.Clamp(digits, 0, 10), MidpointRounding.AwayFromZero);
            }
            if (values.Any(v => v is null))
                return null;
            var n = values.Select(v => v!.Value).ToArray();
            return Name switch
            {
                "sqrt" => n[0] < 0 ? null : Math.Sqrt(n[0]),
                "abs" => Math.Abs(n[0]),
                "min" => n.Min(),
                "max" => n.Max(),
                _ => throw new InvalidOperationException($"Unknown function {Name}"),
            };
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;
        public HashSet<string> Variables { get; } = new(StringComparer.Ordinal);

        // expression := term (('+'|'-') term)*
        public Node ParseExpression()
        {
            var node = ParseTerm();
            while (Peek() is '+' or '-')
            {
                var op = Next();
                node = new Binary(op, node, ParseTerm());
            }
            return node;
        }

        // term := power (('*'|'/') power)*
        private Node ParseTerm()
        {
            var node = ParsePower();
            while (Peek() is '*' or '/')
            {
                var op = Next();
                node = new Binary(op, node, ParsePower());
            }
            return node;
        }

        // power := unary ('^' power)?   (right associative)
        private Node ParsePower()
        {
            var node = ParseUnary();
            if (Peek() == '^')
            {
                Next();
                return new Binary('^', node, ParsePower());
            }
            return node;
        }

        private Node ParseUnary()
        {
            if (Peek() == '-')
            {
                Next();
                return new Neg(ParseUnary());
            }
            if (Peek() == '+')
            {
                Next();
                return ParseUnary();
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            SkipSpace();
            if (_pos >= text.Length)
                throw Error("Unexpected end of formula");

            var c = text[_pos];
            if (c == '(')
            {
                _pos++;
                var inner = ParseExpression();
                Expect(')');
                return inner;
            }
            if (char.IsDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
                    _pos++;
                var s = text[start.._pos];
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw Error($"Bad number '{s}'");
                return new Num(value);
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                    _pos++;
                var name = text[start.._pos];
                if (Peek() == '(')
                {
                    if (!Functions.Contains(name))
                        throw Error($"Unknown function '{name}'");
                    Next();
                    var args = new List<Node>();
                    if (Peek() != ')')
                    {
                        args.Add(ParseExpression());
                        while (Peek() == ',')
                        {
                            Next();
                            args.Add(ParseExpression());
                        }
                    }
                    Expect(')');
                    var (min, max) = name switch
                    {
                        "sqrt" or "abs" => (1, 1),
                        "round" => (1, 2),
                        _ => (1, int.MaxValue),
                    };
                    if (args.Count < min || args.Count > max)
                        throw Error($"{name}() takes {(min == max ? min.ToString() : $"{min}+")} argument(s)");
                    return new Call(name, [.. args]);
                }
                Variables.Add(name);
                return new Var(name);
            }
            throw Error($"Unexpected '{c}'");
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_pos < text.Length)
                throw Error($"Unexpected '{text[_pos]}'");
        }

        private void Expect(char c)
        {
            if (Peek() != c)
                throw Error($"Expected '{c}'");
            _pos++;
        }

        private char? Peek()
        {
            SkipSpace();
            return _pos < text.Length ? text[_pos] : null;
        }

        private char Next()
        {
            SkipSpace();
            return text[_pos++];
        }

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }

        private FormatException Error(string message) => new($"{message} at position {_pos + 1}.");
    }
}
