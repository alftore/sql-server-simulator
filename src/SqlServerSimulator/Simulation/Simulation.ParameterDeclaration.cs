using System.Data;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The parameter declaration a parameterized text command runs under, and the
// Msg 8178 a declared parameter without a value raises before the batch runs.
partial class Simulation
{
    /// <summary>
    /// The first parameter <paramref name="command"/>'s declaration names
    /// without supplying a value, as Msg 8178, or null when every one is
    /// supplied. A parameter is unsupplied when the collection lacks it or
    /// holds it as an input whose value is C# null rather than
    /// <see cref="DBNull"/> — the parameter SqlClient declares and sends with
    /// its default flag and no value — and a declared default supplies it.
    /// The declaration is the one the client sent over the wire, or else the
    /// one SqlClient would build from the collection; real quotes it verbatim
    /// with the statement (probed 2026-10-09 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException? UnsuppliedParameter(SimulatedDbCommand command)
    {
        var parameters = command.Parameters;
        var declaration = command.ParameterDeclaration;
        if (declaration is null)
        {
            var anyUnsupplied = false;
            for (var i = 0; i < parameters.Count && !anyUnsupplied; i++)
                anyUnsupplied = IsUnsupplied(parameters[i]);
            if (!anyUnsupplied)
                return null;
            declaration = SqlClientParameterDeclaration(parameters);
        }

        foreach (var (name, hasDefault) in SplitParameterDeclaration(declaration))
        {
            if (hasDefault)
                continue;
            var bare = name.AsSpan(1);
            SimulatedDbParameter? supplied = null;
            for (var i = 0; i < parameters.Count && supplied is null; i++)
            {
                var parameter = parameters[i];
                var parameterName = parameter.ParameterName.AsSpan();
                if (parameterName.StartsWith('@'))
                    parameterName = parameterName[1..];
                if (parameterName.Equals(bare, StringComparison.OrdinalIgnoreCase))
                    supplied = parameter;
            }
            if (supplied is null || IsUnsupplied(supplied))
                return SimulatedSqlException.ParameterizedQueryExpectsParameter(declaration, command.CommandText, name).PinLine(0);
        }
        return null;

        static bool IsUnsupplied(SimulatedDbParameter parameter) =>
            parameter.Value is null
            && parameter.Direction is ParameterDirection.Input or ParameterDirection.InputOutput
            && parameter.TypeName.Length == 0;
    }

    /// <summary>Whether every one of <paramref name="parameters"/> is an input.</summary>
    private static bool AllInputParameters(SimulatedDbParameterCollection parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Direction is not ParameterDirection.Input)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The declaration SqlClient's <c>sp_executesql</c> call carries for
    /// <paramref name="parameters"/>: each non-return parameter in order,
    /// comma-joined without spaces, its type spelled from the parameter's
    /// <see cref="DbType"/> and size as SqlClient spells it — a MAX type with
    /// a trailing space — and an output-capable one suffixed <c>output</c>
    /// (probed 2026-10-09 against SQL Server 2025 through SqlClient 7).
    /// </summary>
    internal static string SqlClientParameterDeclaration(SimulatedDbParameterCollection parameters)
    {
        var declaration = new StringBuilder();
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.Direction is ParameterDirection.ReturnValue)
                continue;
            var dbType = parameter.DbType;
            var precision = parameter.Precision;
            var scale = parameter.Scale;
            if (dbType is DbType.Decimal)
                EffectiveDecimal(parameter, out precision, out scale);
            AppendDeclaredParameter(declaration, parameter.ParameterName, dbType, parameter.Size, precision, scale, parameter.Direction);
        }
        return declaration.ToString();
    }

    internal static void AppendDeclaredParameter(StringBuilder declaration, string name, DbType dbType, int size, byte precision, byte scale, ParameterDirection direction)
    {
        if (declaration.Length != 0)
            _ = declaration.Append(',');
        if (!name.StartsWith('@'))
            _ = declaration.Append('@');
        _ = declaration.Append(name).Append(' ').Append(SqlClientTypeName(dbType, size, precision, scale));
        if (direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            _ = declaration.Append(" output");
    }

    private static string SqlClientTypeName(DbType dbType, int size, byte precision, byte scale)
    {
        return dbType switch
        {
            DbType.AnsiString => Sized("varchar", 8000, size),
            DbType.AnsiStringFixedLength => Sized("char", 8000, size),
            DbType.Binary => Sized("varbinary", 8000, size),
            DbType.Boolean => "bit",
            DbType.Byte => "tinyint",
            DbType.Currency => "money",
            DbType.Date => "date",
            DbType.DateTime => "datetime",
            DbType.DateTime2 => Scaled("datetime2", scale),
            DbType.DateTimeOffset => Scaled("datetimeoffset", scale),
            DbType.Decimal => string.Create(CultureInfo.InvariantCulture, $"decimal({(precision == 0 ? 29 : precision)},{scale})"),
            DbType.Double => "float",
            DbType.Guid => "uniqueidentifier",
            DbType.Int16 => "smallint",
            DbType.Int32 => "int",
            DbType.Int64 => "bigint",
            DbType.Object => "sql_variant",
            DbType.Single => "real",
            DbType.StringFixedLength => Sized("nchar", 4000, size),
            DbType.Time => Scaled("time", scale),
            DbType.Xml => "xml",
            _ => Sized("nvarchar", 4000, size),
        };

        static string Sized(string name, int limit, int size) =>
            size < 0 || size > limit ? $"{name}(max) "
            : string.Create(CultureInfo.InvariantCulture, $"{name}({(size == 0 ? limit : size)})");

        static string Scaled(string name, byte scale) => string.Create(CultureInfo.InvariantCulture, $"{name}({(scale == 0 ? 7 : scale)})");
    }

    /// <summary>
    /// A decimal parameter as SqlClient 7.1.1 declares and sends it (probed
    /// 2026-11-20 through the simulator's listener, which quotes the
    /// declaration in Msg 8178): the precision is the set one, else the
    /// value's own digits (its scale at least, one at least), else 29 for a
    /// null; the scale is the set one, else the value's; the value is rounded
    /// half away from zero to that scale. <see cref="Fits"/> is false when the
    /// rounded value has more integer digits than the precision leaves, or the
    /// scale passes the precision, which SqlClient refuses before sending.
    /// </summary>
    internal struct ResolvedDecimal
    {
        public byte Precision;

        public byte Scale;

        /// <summary>The value rounded to <see cref="Scale"/>, when it is a .NET decimal.</summary>
        public decimal Rounded;

        /// <summary>The value, when it arrived at full width instead of as a .NET decimal.</summary>
        public Decimal38? Wide;

        /// <summary>False for a null value and for one that does not convert to a decimal.</summary>
        public bool HasValue;

        public bool Fits;

        /// <summary>Whether the value is non-null yet cannot be read as a decimal; the engine's own conversion raises its error.</summary>
        public bool Unconvertible;
    }

    /// <summary>The precision SqlClient declares for a null decimal parameter with none set.</summary>
    private const byte NullDecimalPrecision = 29;

    private static readonly decimal[] DecimalPowersOfTen =
    [
        1m, 10m, 100m, 1000m, 10000m, 100000m, 1000000m, 10000000m, 100000000m, 1000000000m,
        10000000000m, 100000000000m, 1000000000000m, 10000000000000m, 100000000000000m,
        1000000000000000m, 10000000000000000m, 100000000000000000m, 1000000000000000000m,
        10000000000000000000m, 100000000000000000000m, 1000000000000000000000m,
        10000000000000000000000m, 100000000000000000000000m, 1000000000000000000000000m,
        10000000000000000000000000m, 100000000000000000000000000m, 1000000000000000000000000000m,
        10000000000000000000000000000m,
    ];

    /// <summary>
    /// <see cref="SimulatedDbParameter.ResolveDecimal"/>'s computation, which
    /// every reader goes through that member so one execution resolves a
    /// parameter once.
    /// </summary>
    internal static ResolvedDecimal ResolveDecimalUncached(SimulatedDbParameter parameter)
    {
        var raw = parameter.Value;
        if (raw is Decimal38 wide)
        {
            return new ResolvedDecimal
            {
                Precision = parameter.Precision != 0 ? parameter.Precision : (byte)Math.Max(wide.SignificantDigits(), wide.Scale),
                Scale = parameter.Scale != 0 ? parameter.Scale : wide.Scale,
                Wide = wide,
                HasValue = true,
                Fits = true,
            };
        }

        var value = 0m;
        var unconvertible = false;
        if (raw is not (null or DBNull or SqlValue))
        {
            try
            {
                value = BinaryFloatToDecimal.FromParameter(raw);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                unconvertible = true;
            }
        }

        if (raw is null or DBNull or SqlValue || unconvertible)
        {
            return new ResolvedDecimal
            {
                Precision = parameter.Precision != 0 ? parameter.Precision : NullDecimalPrecision,
                Scale = parameter.Scale,
                Fits = true,
                Unconvertible = unconvertible,
            };
        }

        var valueScale = value.Scale;
        var precision = parameter.Precision != 0 ? parameter.Precision : new System.Data.SqlTypes.SqlDecimal(value).Precision;
        var scale = parameter.Scale != 0 ? parameter.Scale : valueScale;
        var rounded = scale >= valueScale ? value : decimal.Round(value, scale, MidpointRounding.AwayFromZero);
        var fits = scale <= precision && (precision - scale > 28 || Math.Abs(rounded) < DecimalPowersOfTen[precision - scale]);
        return new ResolvedDecimal
        {
            Precision = precision,
            Scale = scale,
            Rounded = rounded,
            HasValue = true,
            Fits = fits,
        };
    }

    /// <summary>
    /// The precision and scale <paramref name="parameter"/> is declared with,
    /// as SqlClient resolves them from <see cref="SimulatedDbParameter.Precision"/> /
    /// <see cref="SimulatedDbParameter.Scale"/> and the value.
    /// </summary>
    internal static void EffectiveDecimal(SimulatedDbParameter parameter, out byte precision, out byte scale)
    {
        var resolved = parameter.ResolveDecimal();
        precision = resolved.Precision;
        scale = resolved.Scale;
    }

    /// <summary>
    /// The decimal <paramref name="parameter"/> binds as: its declared type
    /// and its value rounded to it, a typed NULL for a null. False for a value
    /// that does not convert, which the caller's own conversion then raises.
    /// </summary>
    internal static bool TryBindDecimal(SimulatedDbParameter parameter, out SqlType type, out SqlValue seed)
    {
        var resolved = parameter.ResolveDecimal();
        if (resolved.Unconvertible)
        {
            type = SqlType.GetByDbType(DbType.Decimal);
            seed = default;
            return false;
        }

        var declared = DecimalSqlType.Get(resolved.Precision, resolved.Scale);
        type = declared;
        seed = !resolved.HasValue ? SqlValue.Null(declared)
            : resolved.Wide is { } wide ? declared.ConvertParameter(wide)
            : declared.ConvertParameter(resolved.Rounded);
        return true;
    }

    /// <summary>
    /// The decimal or temporal <paramref name="parameter"/> whose
    /// <see cref="SimulatedDbParameter.Precision"/> /
    /// <see cref="SimulatedDbParameter.Scale"/> decide its type, bound as
    /// SqlClient sends it; false for any other parameter, and for one whose
    /// value does not convert, which the caller's own conversion then raises.
    /// </summary>
    internal static bool TryBindPrecisionScale(SimulatedDbParameter parameter, out SqlType type, out SqlValue seed)
    {
        switch (parameter.DbType)
        {
            case DbType.Decimal:
                return TryBindDecimal(parameter, out type, out seed);
            case DbType.DateTime2 or DbType.Time or DbType.DateTimeOffset when parameter.Scale is > 0 and < 7:
                type = parameter.DbType switch
                {
                    DbType.DateTime2 => SqlType.GetDateTime2(parameter.Scale),
                    DbType.Time => SqlType.GetTime(parameter.Scale),
                    _ => SqlType.GetDateTimeOffset(parameter.Scale),
                };
                seed = parameter.Value is null or DBNull or SqlValue
                    ? SqlValue.Null(type)
                    : type.ConvertParameter(TruncateToScale(parameter.Value, TimeSpan.TicksPerSecond / (long)Math.Pow(10, parameter.Scale)));
                return true;
            default:
                type = SqlType.Int32;
                seed = default;
                return false;
        }
    }

    /// <summary>
    /// <paramref name="value"/> with its time of day cut, not rounded, to a
    /// multiple of <paramref name="unit"/> ticks, as SqlClient cuts a
    /// temporal value to its parameter's scale before sending it.
    /// </summary>
    private static object TruncateToScale(object value, long unit) => value switch
    {
        DateTime dateTime => new DateTime(dateTime.Ticks - (dateTime.Ticks % unit), dateTime.Kind),
        DateTimeOffset dateTimeOffset => new DateTimeOffset(dateTimeOffset.Ticks - (dateTimeOffset.Ticks % unit), dateTimeOffset.Offset),
        TimeSpan timeSpan => TimeSpan.FromTicks(timeSpan.Ticks - (timeSpan.Ticks % unit)),
        TimeOnly timeOnly => new TimeOnly(timeOnly.Ticks - (timeOnly.Ticks % unit)),
        _ => value,
    };

    /// <summary>
    /// The text of the <see cref="ArgumentException"/> SqlClient raises, before
    /// sending, for a decimal parameter whose value does not fit its precision
    /// and scale, or null when every parameter fits. The message quotes the
    /// value rounded to its scale.
    /// </summary>
    private static string? DecimalParameterOutOfRange(SimulatedDbParameterCollection parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.Value is null or DBNull or SqlValue)
                continue;
            DbType dbType;
            try
            {
                dbType = parameter.DbType;
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (dbType != DbType.Decimal)
                continue;
            var resolved = parameter.ResolveDecimal();
            if (!resolved.Fits)
                return string.Create(CultureInfo.InvariantCulture, $"Parameter value '{resolved.Rounded.ToString("F" + resolved.Scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}' is out of range.");
        }
        return null;
    }

    /// <summary>
    /// <paramref name="command"/>'s parameters as the plan-cache key and the
    /// cached-plan declaration read them: the command's memo when every
    /// parameter still matches it, else a fresh snapshot, which replaces the
    /// memo. Null when a parameter's DbType can't be
    /// read (a value mapping to no type, the table-valued-parameter reader),
    /// which bypasses the cache and keeps no plan, with the memo left as it
    /// was. Each field is compared through its getter, so an unset type that
    /// follows its value changes the shape as the value changes it.
    /// </summary>
    internal static CommandParameterShape? ParameterShapeOf(SimulatedDbCommand command)
    {
        var parameters = command.Parameters;
        var memo = command.ParameterShape;
        var count = parameters.Count;
        var matches = memo is not null && memo.Entries.Length == count;
        var entries = matches ? memo!.Entries : new ParameterShapeEntry[count];
        for (var i = 0; i < count; i++)
        {
            var p = parameters[i];
            DbType dbType;
            try
            {
                dbType = p.DbType;
            }
            catch (ArgumentException)
            {
                return null;
            }
            var precision = p.Precision;
            var scale = p.Scale;
            if (dbType is DbType.Decimal)
                EffectiveDecimal(p, out precision, out scale);
            if (matches)
            {
                ref readonly var known = ref entries[i];
                if (known.DbType == dbType && known.Size == p.Size && known.Precision == precision && known.Scale == scale
                    && known.Direction == p.Direction && string.Equals(known.Name, p.ParameterName, StringComparison.Ordinal))
                {
                    continue;
                }
                matches = false;
                entries = new ParameterShapeEntry[count];
                Array.Copy(memo!.Entries, entries, i);
            }
            entries[i] = new ParameterShapeEntry(p.ParameterName, dbType, p.Size, precision, scale, p.Direction);
        }
        return matches ? memo : command.ParameterShape = new CommandParameterShape(entries);
    }

    /// <summary>
    /// The parameters a declaration string like <c>@a int, @b decimal(10,2)
    /// OUTPUT, @c int = 5</c> declares, in order, each name with its <c>@</c>
    /// and whether it declares a default, honoring parenthesized type
    /// arguments and quoted defaults when splitting.
    /// </summary>
    internal static List<(string Name, bool HasDefault)> SplitParameterDeclaration(string declaration)
    {
        var declared = new List<(string, bool)>();
        var depth = 0;
        var quoted = false;
        var hasDefault = false;
        var segmentStart = 0;
        for (var i = 0; i <= declaration.Length; i++)
        {
            if (i < declaration.Length)
            {
                var c = declaration[i];
                if (c == '\'')
                    quoted = !quoted;
                if (quoted)
                    continue;
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;
                else if (c == '=' && depth == 0)
                    hasDefault = true;

                if (c != ',' || depth != 0)
                    continue;
            }

            var segment = declaration.AsSpan(segmentStart, i - segmentStart).Trim();
            segmentStart = i + 1;
            var segmentHasDefault = hasDefault;
            hasDefault = false;
            if (segment.Length == 0 || segment[0] != '@')
                continue;

            var end = 0;
            while (end < segment.Length && !char.IsWhiteSpace(segment[end]))
                end++;

            declared.Add((segment[..end].ToString(), segmentHasDefault));
        }

        return declared;
    }
}

/// <summary>One parameter as <see cref="Simulation.ParameterShapeOf"/> read it.</summary>
internal readonly struct ParameterShapeEntry(string name, DbType dbType, int size, byte precision, byte scale, ParameterDirection direction)
{
    public readonly string Name = name;
    public readonly DbType DbType = dbType;
    public readonly int Size = size;
    public readonly byte Precision = precision;
    public readonly byte Scale = scale;
    public readonly ParameterDirection Direction = direction;
}

/// <summary>
/// A command's parameters as of one execution, immutable: the plan-cache key's
/// signature and the SqlClient declaration the cached-plan views show, each
/// built once from the same snapshot so the declaration can't disagree with
/// the key. Held on the command, which compares it with its parameters before
/// each use.
/// </summary>
internal sealed class CommandParameterShape
{
    public readonly ParameterShapeEntry[] Entries;

    /// <summary>
    /// <c>name:dbtype:size:precision:scale</c> of each parameter, in order,
    /// joined by <c>|</c>; the empty string for none.
    /// </summary>
    public readonly string Signature;

    // Built on first use; two threads building it make the same string.
    private string? declaration;

    public CommandParameterShape(ParameterShapeEntry[] entries)
    {
        this.Entries = entries;
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (sb.Length > 0)
                _ = sb.Append('|');
            _ = sb.Append(e.Name).Append(':').Append((int)e.DbType).Append(':')
                .Append(e.Size).Append(':').Append((int)e.Precision).Append(':').Append((int)e.Scale);
        }
        this.Signature = sb.ToString();
    }

    /// <summary>The declaration <see cref="Simulation.SqlClientParameterDeclaration"/> builds for the same parameters.</summary>
    public string GetDeclaration()
    {
        if (this.declaration is { } built)
            return built;
        var sb = new StringBuilder();
        foreach (var e in this.Entries)
        {
            if (e.Direction is not ParameterDirection.ReturnValue)
                Simulation.AppendDeclaredParameter(sb, e.Name, e.DbType, e.Size, e.Precision, e.Scale, e.Direction);
        }
        return this.declaration = sb.ToString();
    }
}
