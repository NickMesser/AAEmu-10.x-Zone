using System.Collections.Frozen;
using System.Globalization;

namespace AAEmu.Zone.Content;

/// <summary>
/// Bounded arithmetic expressions from game.compact. Native formula evaluation uses f32
/// operations (39B1B630), powf and log10f (39D41180). No script execution or reflection.
/// </summary>
public sealed class ContentFormula
{
    private readonly Node _root;
    public string Text { get; }
    public IReadOnlySet<string> Variables { get; }

    public ContentFormula(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > 4096)
            throw new InvalidDataException("Content formula exceeds the 4096-character parser limit.");
        Text = text;
        var parser = new Parser(text);
        _root = parser.Parse();
        Variables = parser.Variables.ToFrozenSet(StringComparer.Ordinal);
    }

    public float Evaluate(Func<string, float> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var result = _root.Evaluate(variable);
        if (!float.IsFinite(result))
            throw new InvalidDataException($"Content formula produced a non-finite result: {Text}");
        return result;
    }

    // Native stat callers convert the resulting f32 with CVTTSS2SI/CVTTSD2SI.
    // Their overflow/NaN behavior belongs to that conversion, not this expression.
    internal float EvaluateNative(Func<string, float> variable) => _root.Evaluate(variable);

    private abstract class Node
    {
        public abstract float Evaluate(Func<string, float> variable);
    }
    private sealed class Constant(float value) : Node
    {
        public override float Evaluate(Func<string, float> variable) => value;
    }
    private sealed class Variable(string name) : Node
    {
        public override float Evaluate(Func<string, float> variable) => variable(name);
    }
    private sealed class Unary(char operation, Node operand) : Node
    {
        public override float Evaluate(Func<string, float> variable) => operation == '-' ? -operand.Evaluate(variable) : operand.Evaluate(variable);
    }
    private sealed class Binary(char operation, Node left, Node right) : Node
    {
        public override float Evaluate(Func<string, float> variable)
        {
            // FUN_39B1AC20 evaluates the denominator first and returns zero without
            // evaluating the numerator when it is zero (the native process also logs it).
            if (operation == '/')
            {
                var denominator = right.Evaluate(variable);
                return denominator == 0 ? 0 : left.Evaluate(variable) / denominator;
            }
            var a = left.Evaluate(variable);
            var b = right.Evaluate(variable);
            return operation switch
            {
                '+' => a + b, '-' => a - b, '*' => a * b, '^' => MathF.Pow(a, b),
                _ => throw new InvalidOperationException("Invalid compiled formula operator.")
            };
        }
    }
    private sealed class Function(string name, Node[] arguments) : Node
    {
        public override float Evaluate(Func<string, float> variable)
        {
            var a = arguments[0].Evaluate(variable);
            // Native compiled conditionals select a branch; an inactive log(0) must not poison it.
            if (name == "if_negative") return arguments[a < 0 ? 1 : 2].Evaluate(variable);
            if (name == "if_positive") return arguments[a > 0 ? 1 : 2].Evaluate(variable);
            if (name == "if_zero") return arguments[a == 0 ? 1 : 2].Evaluate(variable);
            return name switch
            {
                "abs" => MathF.Abs(a), "floor" => MathF.Floor(a), "sqrt" => MathF.Sqrt(a), "log" => MathF.Log10(a),
                "min" => Min(a, arguments[1].Evaluate(variable)),
                "max" => Max(a, arguments[1].Evaluate(variable)),
                "clamp" => Clamp(a, arguments[1].Evaluate(variable), arguments[2].Evaluate(variable)),
                _ => throw new InvalidOperationException("Invalid compiled formula function.")
            };
        }

        // Preserve native comparison order even when an intermediate expression is NaN.
        private static float Min(float a, float b) => a <= b ? a : b;
        private static float Max(float a, float b) => b <= a ? a : b;
        private static float Clamp(float value, float minimum, float maximum) =>
            minimum <= value ? value < maximum ? value : maximum : minimum;
    }

    private sealed class Parser(string text)
    {
        private int _position;
        private int _depth;
        public HashSet<string> Variables { get; } = new(StringComparer.Ordinal);

        public Node Parse()
        {
            var result = Expression(0);
            SkipWhitespace();
            if (_position != text.Length)
                throw Error("Unexpected trailing token");
            return result;
        }

        private Node Expression(int minimumPrecedence)
        {
            if (++_depth > 64)
                throw Error("Formula nesting exceeds 64 levels");
            try
            {
                var left = Prefix();
                while (true)
                {
                    SkipWhitespace();
                    var operation = Peek();
                    var precedence = operation switch { '+' or '-' => 1, '*' or '/' => 2, '^' => 4, _ => -1 };
                    if (precedence < minimumPrecedence)
                        return left;
                    _position++;
                    var right = Expression(operation == '^' ? precedence : precedence + 1);
                    left = new Binary(operation, left, right);
                }
            }
            finally { _depth--; }
        }

        private Node Prefix()
        {
            SkipWhitespace();
            var token = Peek();
            if (token is '+' or '-')
            {
                _position++;
                return new Unary(token, Expression(3));
            }
            if (token == '(')
            {
                _position++;
                var result = Expression(0);
                Expect(')');
                return result;
            }
            if (char.IsAsciiDigit(token) || token == '.')
                return Number();
            if (char.IsAsciiLetter(token) || token == '_')
            {
                var start = _position++;
                while (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '_') _position++;
                var name = text[start.._position];
                SkipWhitespace();
                if (Peek() != '(')
                {
                    Variables.Add(name);
                    return new Variable(name);
                }
                _position++;
                var arity = name switch
                {
                    "abs" or "floor" or "sqrt" or "log" => 1,
                    "min" or "max" => 2,
                    "clamp" or "if_negative" or "if_positive" or "if_zero" => 3,
                    _ => throw Error($"Unsupported function '{name}'")
                };
                var arguments = new Node[arity];
                for (var i = 0; i < arity; i++)
                {
                    if (i != 0) Expect(',');
                    arguments[i] = Expression(0);
                }
                Expect(')');
                return new Function(name, arguments);
            }
            throw Error("Expected a number, variable, function or parenthesis");
        }

        private Constant Number()
        {
            var start = _position;
            while (char.IsAsciiDigit(Peek()) || Peek() == '.') _position++;
            if (Peek() is 'e' or 'E')
            {
                _position++;
                if (Peek() is '+' or '-') _position++;
                while (char.IsAsciiDigit(Peek())) _position++;
            }
            if (!float.TryParse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                throw Error("Invalid numeric literal");
            return new Constant(value);
        }

        private void Expect(char token)
        {
            SkipWhitespace();
            if (Peek() != token) throw Error($"Expected '{token}'");
            _position++;
        }
        private char Peek() => _position < text.Length ? text[_position] : '\0';
        private void SkipWhitespace() { while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++; }
        private InvalidDataException Error(string message) => new($"{message} at position {_position} in content formula '{text}'.");
    }
}
