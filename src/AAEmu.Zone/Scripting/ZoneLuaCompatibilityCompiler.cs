using System.Dynamic;
using System.Globalization;
using System.Linq.Expressions;
using Neo.IronLua;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Keep LNUM's integer overflow promotion and floor modulo when compiling source with the
/// managed VM. Tables/userdata retain the VM's own metamethod binders.
/// </summary>
internal sealed class ZoneLuaCompatibilityCompiler : ILuaDebug
{
    public LuaDebugLevel Level => (LuaDebugLevel)0;
    public LuaChunk CreateChunk(Lua lua, LambdaExpression expression) =>
        new CompatibleChunk(lua, expression.Name ?? "zone", ((LambdaExpression)new ArithmeticVisitor().Visit(expression)).Compile());
    private sealed class CompatibleChunk(Lua lua, string name, Delegate function) : LuaChunk(lua, name, function);

    private sealed class ArithmeticVisitor : DynamicExpressionVisitor
    {
        protected override Expression VisitDynamic(DynamicExpression node)
        {
            var arguments = node.Arguments.Select(Visit).ToArray();
            if (node.Binder is BinaryOperationBinder binary && IsArithmetic(binary.Operation))
                return Expression.MakeDynamic(node.DelegateType, new ArithmeticBinder(binary), arguments!);
            if (node.Binder is UnaryOperationBinder { Operation: ExpressionType.Negate } unary)
                return Expression.MakeDynamic(node.DelegateType, new NegationBinder(unary), arguments!);
            return node.Update(arguments!);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            // The parser can emit directly typed arithmetic for literals. Replace the
            // boxing boundary, allowing an overflow result to become a double.
            if (node.NodeType == ExpressionType.Convert && node.Type == typeof(object) &&
                node.Operand is BinaryExpression binary && IsArithmetic(binary.NodeType) &&
                IsNumberType(binary.Left.Type) && IsNumberType(binary.Right.Type))
                return NumericOperand(binary);
            if (node.NodeType == ExpressionType.Convert && node.Type == typeof(object) &&
                node.Operand is UnaryExpression { NodeType: ExpressionType.Negate } negative &&
                negative.Operand.Type == typeof(long))
                return Expression.Call(typeof(ZoneLuaCompatibilityCompiler), nameof(Negate), null,
                    Expression.Convert(Visit(negative.Operand), typeof(object)));
            return base.VisitUnary(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (IsComparison(node.NodeType) && IsNumberType(node.Left.Type) && IsNumberType(node.Right.Type))
                return Expression.Call(typeof(ZoneLuaCompatibilityCompiler), nameof(Compare), null,
                    Expression.Constant(node.NodeType), NumericOperand(node.Left), NumericOperand(node.Right));
            return base.VisitBinary(node);
        }

        private Expression NumericOperand(Expression value)
        {
            // The parser widens division operands after constructing their typed child
            // expressions. Recurse through that widening before an inner integer can wrap.
            if (value is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion &&
                IsNumberType(conversion.Type) && IsNumberType(conversion.Operand.Type))
                return NumericOperand(conversion.Operand);
            if (value is BinaryExpression binary && IsArithmetic(binary.NodeType) &&
                IsNumberType(binary.Left.Type) && IsNumberType(binary.Right.Type))
                return NumericCall(binary.NodeType, NumericOperand(binary.Left), NumericOperand(binary.Right));
            if (value is UnaryExpression { NodeType: ExpressionType.Negate } negative && IsNumberType(negative.Operand.Type))
                return Expression.Call(typeof(ZoneLuaCompatibilityCompiler), nameof(Negate), null, NumericOperand(negative.Operand));
            return Expression.Convert(Visit(value), typeof(object));
        }
    }

    private sealed class NegationBinder(UnaryOperationBinder original) : DynamicMetaObjectBinder
    {
        public override Type ReturnType => original.ReturnType;
        public override DynamicMetaObject Bind(DynamicMetaObject target, DynamicMetaObject[] args) =>
            args.Length == 0 && IsNumber(target.Value)
                ? new(Expression.Call(typeof(ZoneLuaCompatibilityCompiler), nameof(Negate), null,
                    Expression.Convert(target.Expression, typeof(object))),
                    BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType))
                : original.Bind(target, args);
    }

    private sealed class ArithmeticBinder(BinaryOperationBinder original) : DynamicMetaObjectBinder
    {
        public override Type ReturnType => original.ReturnType;
        public override DynamicMetaObject Bind(DynamicMetaObject target, DynamicMetaObject[] args)
        {
            if (args.Length == 1 && IsNumber(target.Value) && IsNumber(args[0].Value))
            {
                var restrictions = BindingRestrictions.GetTypeRestriction(target.Expression, target.LimitType)
                    .Merge(BindingRestrictions.GetTypeRestriction(args[0].Expression, args[0].LimitType));
                return new(NumericCall(original.Operation, target.Expression, args[0].Expression), restrictions);
            }
            // Preserve table/userdata/string conversion and their original metamethod dispatch.
            return original.Bind(target, args);
        }
    }

    private static MethodCallExpression NumericCall(ExpressionType operation, Expression? left, Expression? right) =>
        Expression.Call(typeof(ZoneLuaCompatibilityCompiler), nameof(Arithmetic), null,
            Expression.Constant(operation), Expression.Convert(left!, typeof(object)), Expression.Convert(right!, typeof(object)));
    private static bool IsArithmetic(ExpressionType operation) => operation is ExpressionType.Add or
        ExpressionType.Subtract or ExpressionType.Multiply or ExpressionType.Divide or ExpressionType.Modulo or ExpressionType.Power;
    private static bool IsComparison(ExpressionType operation) => operation is ExpressionType.Equal or
        ExpressionType.NotEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
        ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
    private static bool IsNumber(object? value) => value is sbyte or byte or short or ushort or int or uint or long or float or double;
    private static bool IsNumberType(Type type) => type == typeof(long) || type == typeof(int) || type == typeof(double) || type == typeof(float);
    private static bool IsInteger(object value) => value is sbyte or byte or short or ushort or int or uint or long;

    public static object Negate(object value)
    {
        if (IsInteger(value))
        { var number = Convert.ToInt64(value, CultureInfo.InvariantCulture); return number == long.MinValue ? -(double)number : (object)-number; }
        return -Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    public static bool Compare(ExpressionType operation, object left, object right)
    {
        if (IsInteger(left) && IsInteger(right))
        {
            var a = Convert.ToInt64(left, CultureInfo.InvariantCulture);
            var b = Convert.ToInt64(right, CultureInfo.InvariantCulture);
            return operation switch
            {
                ExpressionType.Equal => a == b, ExpressionType.NotEqual => a != b,
                ExpressionType.LessThan => a < b, ExpressionType.LessThanOrEqual => a <= b,
                ExpressionType.GreaterThan => a > b, ExpressionType.GreaterThanOrEqual => a >= b,
                _ => throw new InvalidOperationException("Unsupported numeric comparison."),
            };
        }
        var x = Convert.ToDouble(left, CultureInfo.InvariantCulture);
        var y = Convert.ToDouble(right, CultureInfo.InvariantCulture);
        return operation switch
        {
            ExpressionType.Equal => x == y, ExpressionType.NotEqual => x != y,
            ExpressionType.LessThan => x < y, ExpressionType.LessThanOrEqual => x <= y,
            ExpressionType.GreaterThan => x > y, ExpressionType.GreaterThanOrEqual => x >= y,
            _ => throw new InvalidOperationException("Unsupported numeric comparison."),
        };
    }

    public static object Arithmetic(ExpressionType operation, object left, object right)
    {
        if (IsInteger(left) && IsInteger(right))
        {
            var x = Convert.ToInt64(left, CultureInfo.InvariantCulture);
            var y = Convert.ToInt64(right, CultureInfo.InvariantCulture);
            try
            {
                checked
                {
                    switch (operation)
                    {
                        case ExpressionType.Add: return x + y;
                        case ExpressionType.Subtract: return x - y;
                        case ExpressionType.Multiply: return x * y;
                        case ExpressionType.Divide when y != 0 && !(x == long.MinValue && y == -1) && x % y == 0:
                            return x / y;
                        case ExpressionType.Modulo when y != 0:
                            if (x == long.MinValue && y == -1) return 0L;
                            var remainder = x % y;
                            return remainder != 0 && (remainder < 0) != (y < 0) ? remainder + y : remainder;
                    }
                }
            }
            catch (OverflowException) { /* LNUM promotes overflowing arithmetic to double. */ }
        }
        var a = Convert.ToDouble(left, CultureInfo.InvariantCulture);
        var b = Convert.ToDouble(right, CultureInfo.InvariantCulture);
        return operation switch
        {
            ExpressionType.Add => a + b, ExpressionType.Subtract => a - b,
            ExpressionType.Multiply => a * b, ExpressionType.Divide => a / b,
            ExpressionType.Modulo => a - Math.Floor(a / b) * b,
            ExpressionType.Power => Math.Pow(a, b),
            _ => throw new InvalidOperationException("Unsupported managed Lua arithmetic operation."),
        };
    }
}

/// <summary>
/// NeoLua accumulates decimal fractions before scaling them. Feed fractional literals as
/// their exact IEEE significand and binary exponent, preserving the original token positions.
/// This uses the lexer boundary, so comments and string contents are never rewritten.
/// </summary>
internal sealed class ZoneLuaCompileOptions : LuaCompileOptions
{
    public ZoneLuaCompileOptions()
    { ClrEnabled = false; DebugEngine = new ZoneLuaCompatibilityCompiler(); }

    public override ILuaLexer CreateLexer(string chunkName, TextReader reader) =>
        new LuaLexer(ConvertTokens(base.CreateLexer(chunkName, reader)).GetEnumerator());

    private static IEnumerable<Token> ConvertTokens(ILuaLexer source)
    {
        using (source)
        {
            while (true)
            {
                source.Next();
                var token = source.Current;
                if (token.Typ == LuaToken.Number && token.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(token.Value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex) &&
                    hex > long.MaxValue)
                {
                    // LNUM's hexadecimal integer conversion retains the signed 64-bit bit
                    // pattern. Spell MinValue as exact integer arithmetic, avoiding the
                    // managed parser's signed-literal promotion at that one boundary.
                    yield return Make(LuaToken.BracketOpen, "(");
                    yield return Make(LuaToken.Minus, "-");
                    if (hex == 1UL << 63)
                    {
                        yield return Make(LuaToken.Number, "9223372036854775807");
                        yield return Make(LuaToken.Minus, "-");
                        yield return Make(LuaToken.Number, "1");
                    }
                    else yield return Make(LuaToken.Number, unchecked(0UL - hex).ToString(CultureInfo.InvariantCulture));
                    yield return Make(LuaToken.BracketClose, ")");
                }
                else yield return ConvertToken(token);
                if (token.Typ == LuaToken.Eof) yield break;
                Token Make(LuaToken kind, string value) => LuaLexer.CreateToken(kind, value, token.Start, token.End);
            }
        }
    }

    private static Token ConvertToken(Token token)
    {
        if (token.Typ == LuaToken.Number && !token.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var integer) && integer > long.MaxValue)
            return LuaLexer.CreateToken(token.Typ, token.Value + ".", token.Start, token.End);
        if (token.Typ != LuaToken.Number || token.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !double.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) || value == 0 ||
                // Keep integral decimal literals in the parser's original numeric category.
                value == Math.Truncate(value) && value <= ulong.MaxValue)
            return token;
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = bits & 0xfffffffffffff;
        if (exponent != 0) significand |= 1UL << 52;
        var scale = exponent == 0 ? -1074 : exponent - 1023 - 52;
        var exact = $"0x{significand:X}p{scale.ToString(CultureInfo.InvariantCulture)}";
        return LuaLexer.CreateToken(token.Typ, exact, token.Start, token.End);
    }
}
