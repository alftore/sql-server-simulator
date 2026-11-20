using System.Data;
using System.Globalization;
using System.Text;

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
            AppendDeclaredParameter(declaration, parameter.ParameterName, parameter.DbType, parameter.Size, parameter.Precision, parameter.Scale, parameter.Direction);
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
            if (matches)
            {
                ref readonly var known = ref entries[i];
                if (known.DbType == dbType && known.Size == p.Size && known.Precision == p.Precision && known.Scale == p.Scale
                    && known.Direction == p.Direction && string.Equals(known.Name, p.ParameterName, StringComparison.Ordinal))
                {
                    continue;
                }
                matches = false;
                entries = new ParameterShapeEntry[count];
                Array.Copy(memo!.Entries, entries, i);
            }
            entries[i] = new ParameterShapeEntry(p.ParameterName, dbType, p.Size, p.Precision, p.Scale, p.Direction);
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
