using System.Data;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the process-wide tier under every simulation's token memo and Query
/// Store statement shapes (<see cref="SharedTextTier{TKey, TValue}"/>): a
/// simulation is served what another tokenized only under the same text,
/// collation, compatibility level and <c>QUOTED_IDENTIFIER</c>, answers as it
/// would alone, and counts its own memo's hits and misses as it did before the
/// tier. Each test runs texts no other test runs (a tag of its own in a
/// comment) and reads the counters of simulations it built itself.
/// </summary>
/// <remarks>
/// The tier is shared by every test in the process, and a full tier admits a
/// text only on its second arrival, so a test that expects a second
/// simulation to be served fails whenever the rest of the suite has just
/// filled it. These tests therefore run alone, after the parallel ones, each
/// on emptied tiers.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SharedTextTierTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void EmptyTheTiers()
    {
        TokenMemo.Shared.Clear();
        Simulation.SharedQueryStoreShapes.Clear();
    }

    private static SimulatedDbConnection Open(Simulation sim, string? setup = null)
    {
        var connection = sim.CreateDbConnection();
        connection.Open();
        if (setup is not null)
            _ = Run(connection, setup);
        return connection;
    }

    /// <summary>A batch's every result set and its error, as text: what a client could tell apart.</summary>
    private static string Run(SimulatedDbConnection connection, string sql, params (string Name, DbType Type, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, type, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = type;
            parameter.Value = value;
            _ = command.Parameters.Add(parameter);
        }
        var transcript = new StringBuilder();
        try
        {
            using var reader = command.ExecuteReader();
            do
            {
                _ = transcript.Append('[');
                for (var i = 0; i < reader.FieldCount; i++)
                    _ = transcript.Append(reader.GetName(i)).Append(':').Append(reader.GetDataTypeName(i)).Append(';');
                while (reader.Read())
                {
                    _ = transcript.Append('(');
                    for (var i = 0; i < reader.FieldCount; i++)
                        _ = transcript.Append(Render(reader.GetValue(i))).Append(';');
                    _ = transcript.Append(')');
                }
                _ = transcript.Append(']');
            }
            while (reader.NextResult());
        }
        catch (SimulatedSqlException error)
        {
            _ = transcript.Append(CultureInfo.InvariantCulture, $"!{error.Number}/{error.Class}/{error.State} line {error.LineNumber}: {error.Message}");
        }
        return transcript.ToString();
    }

    private static string Render(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static (long Hits, long Misses, long SharedHits, int Count) Counters(Simulation sim) =>
        (sim.TokenMemo.Hits, sim.TokenMemo.Misses, sim.TokenMemo.SharedHits, sim.TokenMemo.Count);

    private static (long Hits, long Misses, long SharedHits, int Count) Delta((long Hits, long Misses, long SharedHits, int Count) after, (long Hits, long Misses, long SharedHits, int Count) before) =>
        (after.Hits - before.Hits, after.Misses - before.Misses, after.SharedHits - before.SharedHits, after.Count - before.Count);

    [TestMethod]
    public void SecondSimulation_FirstExecution_ReadsTheSharedSequence()
    {
        // A write, so no plan cache serves the repeat and its parse reads the memo.
        const string Table = "create table t (id int not null primary key, val int not null);";
        const string Sql = "insert t values (1, 10); delete t where id = 1; -- SecondSimulation_FirstExecution_ReadsTheSharedSequence";
        var a = new Simulation();
        var b = new Simulation();
        using var onA = Open(a, Table);
        using var onB = Open(b, Table);

        // The compile walk misses and tokenizes; the run after it hits what the walk kept.
        var before = Counters(a);
        var onFirst = Run(onA, Sql);
        AreEqual((1L, 1L, 0L, 1), Delta(Counters(a), before), "A tokenizes the text itself");
        before = Counters(a);
        _ = Run(onA, Sql);
        var secondOnA = Delta(Counters(a), before);
        AreEqual(0L, secondOnA.Misses, "A's second execution is its own memo's hit");

        // B's walk misses its memo too, is served by the tier, and its memo
        // adopts the sequence, so B's run hits as A's did.
        before = Counters(b);
        AreEqual(onFirst, Run(onB, Sql));
        AreEqual((1L, 1L, 1L, 1), Delta(Counters(b), before), "B's first execution");
        before = Counters(b);
        _ = Run(onB, Sql);
        AreEqual(secondOnA, Delta(Counters(b), before), "B's second execution");
    }

    [TestMethod]
    public void PerSimulationCounters_AreWhatTheyWereWithoutTheTier()
    {
        // The same stream on two simulations, the second served by the tier:
        // its own memo's hits, misses and entries move exactly as the first's,
        // over a write, a compiled multi-statement batch, a batch ending
        // partway at an error, a text no memo keeps, a procedure body and a
        // clearing.
        const string Tag = " -- PerSimulationCounters_AreWhatTheyWereWithoutTheTier";
        string[] stream =
        [
            "create table t (id int not null primary key, val int not null);" + Tag,
            "insert t values (1, 10), (2, 20);" + Tag,
            "insert t values (3, 30); update t set val = val + 1 where id = 3; select count(*) from t;" + Tag,
            "insert t values (1, 99); insert t values (4, 40);" + Tag,
            "set xact_abort on; insert t values (1, 99); insert t values (5, 50);" + Tag,
            "set xact_abort off;" + Tag,
            "select 'unterminated" + Tag,
            "create procedure p as begin update t set val = val + 1 where id = 2; select val from t where id = 2; end" + Tag,
            "exec p;" + Tag,
            "exec p;" + Tag,
            "insert t values (6, 60);" + Tag,
            "dbcc freeproccache;" + Tag,
            "exec p;" + Tag,
            "delete t where id = 6;" + Tag,
            "insert t values (6, 60);" + Tag,
        ];
        var counters = new List<(long, long, long, int)>[2];
        var transcripts = new List<string>[2];
        for (var run = 0; run < 2; run++)
        {
            var sim = new Simulation();
            using var connection = Open(sim);
            counters[run] = [];
            transcripts[run] = [];
            foreach (var sql in stream)
            {
                var before = Counters(sim);
                transcripts[run].Add(Run(connection, sql));
                var (hits, misses, _, count) = Delta(Counters(sim), before);
                counters[run].Add((hits, misses, 0, count));
            }
            if (run == 1)
                IsGreaterThan(0L, sim.TokenMemo.SharedHits, "the second simulation was served by the tier");
        }
        CollectionAssert.AreEqual(transcripts[0], transcripts[1]);
        CollectionAssert.AreEqual(counters[0], counters[1]);
    }

    [TestMethod]
    public void ServerCollation_KeepsItsOwnTokens()
    {
        // A string literal takes the database's collation as it tokenizes.
        const string Sql = "select cast(sql_variant_property('a', 'Collation') as nvarchar(128)); -- ServerCollation_KeepsItsOwnTokens";
        var latin = new Simulation { ServerCollationName = "Latin1_General_CI_AS" };
        var japanese = new Simulation { ServerCollationName = "Japanese_CI_AS" };
        using var onLatin = Open(latin);
        using var onJapanese = Open(japanese);
        for (var i = 0; i < 3; i++)
        {
            AreEqual("[:nvarchar;(Latin1_General_CI_AS;)]", Run(onLatin, Sql));
            AreEqual("[:nvarchar;(Japanese_CI_AS;)]", Run(onJapanese, Sql));
        }
    }

    [TestMethod]
    public void CompatibilityLevel_KeepsItsOwnTokens()
    {
        // REGEXP_LIKE is a reserved word at 170 and a function name at 160.
        const string Sql = "select case when regexp_like('abc', 'b') then 1 else 0 end; -- CompatibilityLevel_KeepsItsOwnTokens";
        var at160 = new Simulation();
        var at170 = new Simulation();
        using var on160 = Open(at160, "alter database current set compatibility_level = 160");
        using var on170 = Open(at170);
        var refused = Run(on160, Sql);
        StartsWith("!", refused);
        for (var i = 0; i < 3; i++)
        {
            AreEqual("[:int;(1;)]", Run(on170, Sql));
            AreEqual(refused, Run(on160, Sql));
        }

        // A level changed mid-session reads the text under the new level.
        _ = Run(on160, "alter database current set compatibility_level = 170");
        AreEqual("[:int;(1;)]", Run(on160, Sql));
        _ = Run(on170, "alter database current set compatibility_level = 160");
        AreEqual(refused, Run(on170, Sql));
    }

    [TestMethod]
    public void DatabaseCollation_KeepsItsOwnTokens()
    {
        // Two databases of one simulation and another simulation's default,
        // each tagging the same literal with its own collation.
        const string Sql = "select cast(sql_variant_property('a', 'Collation') as nvarchar(128)); -- DatabaseCollation_KeepsItsOwnTokens";
        var sim = new Simulation();
        using var connection = Open(sim, "create database cs collate Latin1_General_CS_AS");
        var other = new Simulation();
        using var onOther = Open(other);
        for (var i = 0; i < 3; i++)
        {
            _ = Run(connection, "use cs");
            AreEqual("[:nvarchar;(Latin1_General_CS_AS;)]", Run(connection, Sql));
            _ = Run(connection, "use simulated");
            AreEqual("[:nvarchar;(SQL_Latin1_General_CP1_CI_AS;)]", Run(connection, Sql));
            AreEqual("[:nvarchar;(SQL_Latin1_General_CP1_CI_AS;)]", Run(onOther, Sql));
        }
    }

    [TestMethod]
    public void QuotedIdentifierOff_KeepsItsOwnTokens()
    {
        const string Sql = "select \"x\" from t; -- QuotedIdentifierOff_KeepsItsOwnTokens";
        const string Table = "create table t (x int); insert t values (7);";
        var off = new Simulation();
        var on = new Simulation();
        using var onOff = Open(off, Table);
        _ = Run(onOff, "set quoted_identifier off");
        using var onOn = Open(on, Table);
        for (var i = 0; i < 3; i++)
        {
            AreEqual("[:varchar;(x;)]", Run(onOff, Sql));
            AreEqual("[x:int;(7;)]", Run(onOn, Sql));
        }
    }

    [TestMethod]
    public void AnsiNulls_IsReadByTheRunNotTheTokens()
    {
        // ANSI_NULLS isn't a tokenizing input: both simulations share the
        // sequence, and each compares NULL as its own session says.
        const string Sql = "declare @n int = null; select count(*) from t where x = @n; -- AnsiNulls_IsReadByTheRunNotTheTokens";
        const string Table = "create table t (x int); insert t values (null), (1);";
        var off = new Simulation();
        var on = new Simulation();
        using var onOff = Open(off, Table);
        _ = Run(onOff, "set ansi_nulls off");
        using var onOn = Open(on, Table);
        for (var i = 0; i < 3; i++)
        {
            AreEqual("[:int;(0;)]", Run(onOn, Sql));
            AreEqual("[:int;(1;)]", Run(onOff, Sql));
        }
    }

    [TestMethod]
    public void DateFormat_IsReadByTheParseNotTheTokens()
    {
        const string Sql = "declare @d int = 0; select month(cast('01/02/2026' as datetime)); -- DateFormat_IsReadByTheParseNotTheTokens";
        var dmy = new Simulation();
        var mdy = new Simulation();
        using var onDmy = Open(dmy, "set dateformat dmy");
        using var onMdy = Open(mdy);
        for (var i = 0; i < 3; i++)
        {
            AreEqual("[:int;(1;)]", Run(onMdy, Sql));
            AreEqual("[:int;(2;)]", Run(onDmy, Sql));
        }
        IsGreaterThan(0L, dmy.TokenMemo.SharedHits + mdy.TokenMemo.SharedHits, "the two shared one sequence");
    }

    [TestMethod]
    public void QuotedIdentifierFlippedMidBatch_IsNeverShared()
    {
        // A text that changes its own tokenizing inputs has no one sequence,
        // so no memo, and no tier, keeps one.
        const string Sql = "set quoted_identifier off; select \"x\"; set quoted_identifier on; -- QuotedIdentifierFlippedMidBatch_IsNeverShared";
        var a = new Simulation();
        var b = new Simulation();
        using var onA = Open(a);
        using var onB = Open(b);
        for (var i = 0; i < 3; i++)
        {
            AreEqual("[:varchar;(x;)]", Run(onA, Sql));
            AreEqual("[:varchar;(x;)]", Run(onB, Sql));
        }
        AreEqual(0L, a.TokenMemo.SharedHits + b.TokenMemo.SharedHits);
    }

    [TestMethod]
    public void TokenizerError_IsNeverShared()
    {
        const string Sql = "select 1;\nselect 'unterminated -- TokenizerError_IsNeverShared";
        var a = new Simulation();
        var b = new Simulation();
        using var onA = Open(a);
        using var onB = Open(b);
        var first = Run(onA, Sql);
        StartsWith("!105/", first);
        Contains("line 2", first);
        for (var i = 0; i < 3; i++)
        {
            AreEqual(first, Run(onA, Sql));
            AreEqual(first, Run(onB, Sql));
        }
        AreEqual(0L, a.TokenMemo.SharedHits + b.TokenMemo.SharedHits);
        AreEqual(0L, a.TokenMemo.Hits + b.TokenMemo.Hits);
    }

    [TestMethod]
    public void LongText_StaysPerSimulation()
    {
        var sql = "declare @x int = 1; select @x; /* " + new string('x', TokenMemo.MaxSharedTextLength) + " */ -- LongText_StaysPerSimulation";
        var a = new Simulation();
        var b = new Simulation();
        using var onA = Open(a);
        using var onB = Open(b);
        var before = Counters(a);
        var onFirst = Run(onA, sql);
        var firstOnA = Delta(Counters(a), before);
        AreEqual((1L, 1L, 0L, 1), firstOnA, "A memoizes the text for itself");
        _ = Run(onA, sql);

        before = Counters(b);
        AreEqual(onFirst, Run(onB, sql));
        AreEqual(firstOnA, Delta(Counters(b), before), "B tokenizes it again");
        IsFalse(TokenMemo.Shared.TryGetValue(new TokenMemoKey(sql, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), CompatibilityLevel.Sql170, quotedIdentifiers: true), out _));
    }

    [TestMethod]
    public void QueryStoreShape_SharedAcrossSimulations()
    {
        const string Setup = """
            alter database simulated set query_store (query_capture_mode = all);
            create table t (id int primary key, val int);
            insert t values (1, 10);
            """;
        const string Sql = "update t set val = @v where id = @id; -- QueryStoreShape_SharedAcrossSimulations";
        const string Stored = """
            select qt.query_sql_text, convert(varchar(20), q.query_hash, 1), q.query_parameterization_type
            from sys.query_store_query q
            join sys.query_store_query_text qt on qt.query_text_id = q.query_text_id
            where qt.query_sql_text like '%update t set val%'
            order by q.query_id
            """;
        var a = new Simulation();
        var b = new Simulation();
        using var onA = Open(a, Setup);
        using var onB = Open(b, Setup);
        _ = Run(onA, Sql, ("@v", DbType.Int32, 11), ("@id", DbType.Int32, 1));
        var sharedBefore = b.QueryStoreShapeSharedHits;
        _ = Run(onB, Sql, ("@v", DbType.Int32, 11), ("@id", DbType.Int32, 1));
        IsGreaterThan(sharedBefore, b.QueryStoreShapeSharedHits, "B's shape came from the tier");

        var storedOnA = Run(onA, Stored);
        Contains("update t set val = @v where id = @id", storedOnA);
        AreEqual(storedOnA, Run(onB, Stored));
    }

    [TestMethod]
    public void QueryStoreShape_QuotedIdentifier_KeepsItsOwnHash()
    {
        // "x" is a name with QUOTED_IDENTIFIER on and a literal with it off,
        // which the query hash tells apart: the off session's statement
        // hashes as the literal does, the on session's as the bracketed name.
        const string Setup = """
            alter database simulated set query_store (query_capture_mode = all);
            create table t (x int); insert t values (7);
            """;
        const string Tag = " -- QueryStoreShape_QuotedIdentifier_KeepsItsOwnHash";
        static string HashOf(SimulatedDbConnection connection, string text) =>
            Run(connection, $"select convert(varchar(20), q.query_hash, 1) from sys.query_store_query q join sys.query_store_query_text qt on qt.query_text_id = q.query_text_id where qt.query_sql_text = '{text.Replace("'", "''", StringComparison.Ordinal)}'");

        var off = new Simulation();
        var on = new Simulation();
        using var onOff = Open(off, Setup);
        _ = Run(onOff, "set quoted_identifier off");
        using var onOn = Open(on, Setup);
        for (var i = 0; i < 2; i++)
        {
            _ = Run(onOff, "select \"x\" from t;" + Tag);
            _ = Run(onOn, "select \"x\" from t;" + Tag);
        }
        _ = Run(onOff, "select 'x' from t;" + Tag);
        _ = Run(onOn, "select [x] from t;" + Tag);

        var literal = HashOf(onOff, "select 'x' from t");
        var name = HashOf(onOn, "select [x] from t");
        AreNotEqual(literal, name);
        AreEqual(literal, HashOf(onOff, "select \"x\" from t"));
        AreEqual(name, HashOf(onOn, "select \"x\" from t"));
    }

    [TestMethod]
    public void QueryStoreShape_Merge_KeepsTheHashItsOwnReadingGives()
    {
        // A MERGE's text holds the ';' it requires and the parser's tokens
        // stop short of it, so its shape read off the parse and its shape
        // read off the text hash differently, and a simulation records the
        // one its own first capture reads: a MERGE that completes is read
        // off the parse, one a run-time error ends off its text. The
        // references run under server collations no other simulation here
        // uses, so nothing is shared with them; the collation changes no
        // token of this text.
        const string Merge = "merge m_SharedTextTierMerge using (values (1)) as s (a) on 1 = 0 when not matched then insert (a) values (s.a);";
        const string Setup = """
            alter database simulated set query_store (query_capture_mode = all);
            create table m_SharedTextTierMerge (a int primary key);
            """;
        const string Hash = "select convert(varchar(20), q.query_hash, 1) from sys.query_store_query q join sys.query_store_query_text qt on qt.query_text_id = q.query_text_id where qt.query_sql_text like 'merge m[_]SharedTextTierMerge%'";

        static string Recorded(string? collation, bool failsFirst)
        {
            using var connection = Open(collation is null ? new Simulation() : new Simulation { ServerCollationName = collation }, Setup);
            if (failsFirst)
                _ = Run(connection, "insert m_SharedTextTierMerge values (1)");
            var first = Run(connection, Merge);
            AreEqual(failsFirst, first.StartsWith("!2627/", StringComparison.Ordinal), first);
            return Run(connection, Hash);
        }

        var completedAlone = Recorded("Latin1_General_CS_AS", failsFirst: false);
        var failedAlone = Recorded("Latin1_General_CI_AI", failsFirst: true);
        StartsWith("[:varchar;(0x", completedAlone);
        AreNotEqual(completedAlone, failedAlone, "the two readings hash differently");

        for (var i = 0; i < 2; i++)
        {
            AreEqual(failedAlone, Recorded(null, failsFirst: true));
            AreEqual(completedAlone, Recorded(null, failsFirst: false));
        }
    }

#if DEBUG
    private static Token[] Tokenize(in TokenMemoKey key)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (Tokenizer.NextTokenPastBlanks(key.CommandText, ref index, key.Collation, key.QuotedIdentifiers, key.CompatibilityLevel) is Token token)
        {
            if (token is not (Parser.Tokens.Whitespace or Parser.Tokens.Comment))
                tokens.Add(token);
        }
        return [.. tokens];
    }

    [TestMethod]
    public void DebugAudit_ThrowsOnATokenSequenceThatDiffers()
    {
        var collation = Collation.Get("SQL_Latin1_General_CP1_CI_AS");
        var at160 = new TokenMemoKey("select regexp_like from t where a = 'x'", collation, CompatibilityLevel.Sql160, quotedIdentifiers: true);
        TokenMemo.AuditShared(at160, Tokenize(at160));

        // A keyword at 170, a name at 160.
        var at170 = new TokenMemoKey(at160.CommandText, collation, CompatibilityLevel.Sql170, quotedIdentifiers: true);
        _ = Throws<InvalidOperationException>(() => TokenMemo.AuditShared(at160, Tokenize(at170)));

        // The literal's collation.
        var otherCollation = new TokenMemoKey(at160.CommandText, Collation.Get("Latin1_General_CS_AS"), CompatibilityLevel.Sql160, quotedIdentifiers: true);
        _ = Throws<InvalidOperationException>(() => TokenMemo.AuditShared(at160, Tokenize(otherCollation)));

        // A literal's value.
        var two = new TokenMemoKey("select regexp_like from t where a = 'y'", collation, CompatibilityLevel.Sql160, quotedIdentifiers: true);
        _ = Throws<InvalidOperationException>(() => TokenMemo.AuditShared(at160, Tokenize(two)));
    }

    [TestMethod]
    public void DebugAudit_NamesAShapeFieldThatDiffers()
    {
        var sim = new Simulation();
        using var connection = Open(sim);
        var database = sim.Databases["simulated"];
        const string Text = "select \"x\" from t";
        var on = QueryStoreShape.Read(Text, parsed: null, quotedIdentifiers: true, database);
        IsNull(on.DifferenceFrom(QueryStoreShape.Read(Text, parsed: null, quotedIdentifiers: true, database)));
        AreEqual("QueryHash", on.DifferenceFrom(QueryStoreShape.Read(Text, parsed: null, quotedIdentifiers: false, database)));
    }
#endif

    [TestMethod]
    public void ClientWritingIntoAQueryHash_ChangesNoOtherSimulation()
    {
        const string Setup = """
            alter database simulated set query_store (query_capture_mode = all);
            create table t (id int primary key);
            """;
        const string Sql = "select count(*) from t; -- ClientWritingIntoAQueryHash_ChangesNoOtherSimulation";
        string[] hashes =
        [
            "select q.query_hash, p.query_plan_hash from sys.query_store_query q join sys.query_store_query_text qt on qt.query_text_id = q.query_text_id join sys.query_store_plan p on p.query_id = q.query_id where qt.query_sql_text = 'select count(*) from t'",
            "select s.query_hash, s.query_plan_hash from sys.dm_exec_query_stats s cross apply sys.dm_exec_sql_text(s.sql_handle) t where t.text like 'select count(*) from t; -- ClientWritingIntoAQueryHash%'",
            "select top (1) query_hash, query_hash from sys.query_store_query order by query_id",
            "select top (1) query_plan_hash, query_plan_hash from sys.query_store_plan order by plan_id",
        ];
        var a = new Simulation();
        var b = new Simulation();
        using var onA = Open(a, Setup);
        using var onB = Open(b, Setup);
        _ = Run(onA, Sql);
        _ = Run(onB, Sql);
        foreach (var hash in hashes)
        {
            var expected = Run(onB, hash);
            Contains("(0x", expected);
            using (var command = onA.CreateCommand())
            {
                command.CommandText = hash;
                using var reader = command.ExecuteReader();
                IsTrue(reader.Read());
                Array.Fill((byte[])reader.GetValue(0), (byte)0xEE);
                Array.Fill((byte[])reader.GetValue(1), (byte)0xEE);
            }
            AreEqual(expected, Run(onA, hash));
            AreEqual(expected, Run(onB, hash));
        }
    }

    [TestMethod]
    public void ClientWritingIntoABinaryLiteral_ChangesNoOtherSimulation()
    {
        // A binary literal's bytes live in its token, which the tier shares.
        const string Sql = "declare @d int = 0; select 0x0102 as b; -- ClientWritingIntoABinaryLiteral_ChangesNoOtherSimulation";
        var a = new Simulation();
        var b = new Simulation();
        using var onA = Open(a);
        using var onB = Open(b);
        using (var command = onA.CreateCommand())
        {
            command.CommandText = Sql;
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            Array.Fill((byte[])reader.GetValue(0), (byte)0xEE);
        }
        AreEqual("[b:varbinary;(0x0102;)]", Run(onB, Sql));
        AreEqual("[b:varbinary;(0x0102;)]", Run(onA, Sql));
    }

    /// <summary>
    /// The EF Core stream <c>EfTier</c> replays: the model's tables and
    /// indexes, its batched inserts as <c>MERGE … OUTPUT</c>, its queries, and
    /// its updates and deletes with <c>OUTPUT 1</c>, with Query Store capturing
    /// every statement and the store's texts and hashes read back at the end.
    /// </summary>
    private static string[] EfStream(string tag) =>
    [
        "SELECT 1" + tag,
        """
        IF EXISTS
            (SELECT *
             FROM [sys].[objects] o
             WHERE [o].[type] = 'U'
             AND [o].[is_ms_shipped] = 0
             AND [o].[object_id] NOT IN (SELECT [ep].[major_id]
                FROM [sys].[extended_properties] AS [ep]
                WHERE [ep].[minor_id] = 0
                    AND [ep].[class] = 1
                    AND [ep].[name] = N'microsoft_database_tools_support'
            )
        )
        SELECT 1 ELSE SELECT 0
        """ + tag,
        "alter database simulated set query_store (query_capture_mode = all)" + tag,
        """
        CREATE TABLE [Customers] (
            [Id] int NOT NULL IDENTITY,
            [Name] nvarchar(200) NOT NULL,
            [Email] nvarchar(200) NOT NULL,
            [Tier] int NOT NULL,
            CONSTRAINT [PK_Customers] PRIMARY KEY ([Id])
        );
        """ + tag,
        """
        CREATE TABLE [Orders] (
            [Id] int NOT NULL IDENTITY,
            [CustomerId] int NOT NULL,
            [PlacedAt] datetime2 NOT NULL,
            [Status] nvarchar(20) NOT NULL,
            CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
            CONSTRAINT [FK_Orders_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]) ON DELETE NO ACTION
        );
        """ + tag,
        "CREATE UNIQUE INDEX [IX_Customers_Email] ON [Customers] ([Email]);" + tag,
        "CREATE INDEX [IX_Orders_CustomerId] ON [Orders] ([CustomerId]);" + tag,
        """
        SET IMPLICIT_TRANSACTIONS OFF;
        SET NOCOUNT ON;
        MERGE [Customers] USING (
        VALUES (N'c1@example.test', N'Customer 1', 1, 0),
        (N'c2@example.test', N'Customer 2', 2, 1),
        (N'c3@example.test', N'Customer 3', 0, 2)) AS i ([Email], [Name], [Tier], _Position) ON 1=0
        WHEN NOT MATCHED THEN
        INSERT ([Email], [Name], [Tier])
        VALUES (i.[Email], i.[Name], i.[Tier])
        OUTPUT INSERTED.[Id], i._Position;
        """ + tag,
        """
        SET IMPLICIT_TRANSACTIONS OFF;
        SET NOCOUNT ON;
        MERGE [Orders] USING (
        VALUES (1, '2026-01-02T00:00:00', N'New', 0),
        (2, '2026-01-03T00:00:00', N'New', 1),
        (1, '2026-01-01T00:00:00', N'New', 2)) AS i ([CustomerId], [PlacedAt], [Status], _Position) ON 1=0
        WHEN NOT MATCHED THEN
        INSERT ([CustomerId], [PlacedAt], [Status])
        VALUES (i.[CustomerId], i.[PlacedAt], i.[Status])
        OUTPUT INSERTED.[Id], i._Position;
        """ + tag,
        """
        SELECT [o].[Id], [o].[CustomerId], [o].[PlacedAt], [o].[Status], [c].[Id], [c].[Name]
        FROM [Orders] AS [o]
        INNER JOIN [Customers] AS [c] ON [o].[CustomerId] = [c].[Id]
        WHERE [c].[Tier] = 1
        ORDER BY [o].[PlacedAt], [o].[Id], [c].[Id]
        """ + tag,
        """
        SELECT COUNT(*)
        FROM [Customers] AS [c]
        WHERE [c].[Email] LIKE N'%example%'
        """ + tag,
        """
        SET NOCOUNT ON;
        UPDATE [Orders] SET [Status] = N'Shipped'
        OUTPUT 1
        WHERE [Id] = 2;
        """ + tag,
        """
        SET IMPLICIT_TRANSACTIONS OFF;
        SET NOCOUNT ON;
        DELETE FROM [Orders]
        OUTPUT 1
        WHERE [Id] = 3;
        """ + tag,
        """
        SELECT CASE
            WHEN EXISTS (
                SELECT 1
                FROM [Orders] AS [o]
                WHERE [o].[Status] = N'Shipped') THEN CAST(1 AS bit)
            ELSE CAST(0 AS bit)
        END
        """ + tag,
        """
        SELECT [o].[Id]
        FROM [Orders] AS [o]
        WHERE [o].[PlacedAt] >= '2026-01-02T00:00:00.0000000'
        """ + tag,
        """
        select qt.query_sql_text, convert(varchar(20), q.query_hash, 1), q.query_parameterization_type, p.is_trivial_plan
        from sys.query_store_query q
        join sys.query_store_query_text qt on qt.query_text_id = q.query_text_id
        join sys.query_store_plan p on p.query_id = q.query_id
        where qt.query_sql_text not like '%query[_]store%'
        order by q.query_id
        """ + tag,
    ];

    private static string RunStream(string[] stream, string tag)
    {
        var sim = new Simulation();
        using var connection = Open(sim);
        var transcript = new StringBuilder();
        foreach (var sql in stream)
            _ = transcript.AppendLine(Run(connection, sql).Replace(tag, "", StringComparison.Ordinal));
        return transcript.ToString();
    }

    [TestMethod]
    public void ManySimulations_InParallel_AnswerAsAlone()
    {
        // The reference tokenizes and reads every shape itself: no other
        // simulation ever runs its texts, which differ from the parallel
        // ones' only in a trailing comment.
        const string Alone = " -- ManySimulations_InParallel_AnswerAsAlone, alone";
        const string Together = " -- ManySimulations_InParallel_AnswerAsAlone, together";
        var reference = RunStream(EfStream(Alone), Alone);
        Contains("Customer 1", reference);
        Contains("MERGE", reference);

        var stream = EfStream(Together);
        const int Workers = 8, Rounds = 3;
        var transcripts = new string[Workers * Rounds];
        var cancellation = this.TestContext.CancellationToken;
        using var start = new Barrier(Workers);
        var threads = new Thread[Workers];
        for (var w = 0; w < Workers; w++)
        {
            var worker = w;
            threads[w] = new Thread(() =>
            {
                for (var round = 0; round < Rounds; round++)
                {
                    // Every worker's first execution of every text races the
                    // others' to publish it; the later rounds read the tier.
                    start.SignalAndWait(cancellation);
                    transcripts[(round * Workers) + worker] = RunStream(stream, Together);
                }
            });
            threads[w].Start();
        }
        foreach (var thread in threads)
            thread.Join();
        for (var i = 0; i < transcripts.Length; i++)
            AreEqual(reference, transcripts[i], $"worker {i % Workers}, round {i / Workers}");
    }

    [TestMethod]
    public void Tier_PastItsEntryCount_EvictsTheLeastRecentlyUsed()
    {
        var tier = new SharedTextTier<string, string>(capacity: 8, textBudget: 1_000_000, maxTextLength: 100);
        for (var i = 0; i < 8; i++)
            IsTrue(tier.TryAdd($"k{i}", 2, $"v{i}"));
        // The first four are read again, so the sweep keeps them.
        for (var i = 0; i < 4; i++)
            IsTrue(tier.TryGetValue($"k{i}", out _));
        // Full, it admits a key on its second arrival only.
        IsFalse(tier.TryAdd("k8", 2, "v8"));
        AreEqual(0L, tier.Evictions);
        IsTrue(tier.TryAdd("k8", 2, "v8"));
        IsGreaterThan(0L, tier.Evictions);
        IsLessThanOrEqualTo(8, tier.Count);
        for (var i = 0; i < 4; i++)
        {
            IsTrue(tier.TryGetValue($"k{i}", out var value));
            AreEqual($"v{i}", value);
        }
        IsFalse(tier.TryGetValue("k4", out _));
        AreEqual(2L * tier.Count, tier.TextLength);
    }

    [TestMethod]
    public void Tier_PastItsTextBudget_EvictsUntilHalfIsLeft()
    {
        var tier = new SharedTextTier<string, string>(capacity: 1000, textBudget: 100, maxTextLength: 40);
        IsTrue(tier.TryAdd("a", 40, "a"));
        IsTrue(tier.TryAdd("b", 40, "b"));
        AreEqual(80L, tier.TextLength);
        IsTrue(tier.TryGetValue("b", out _));
        // Past the budget: the oldest go until at most half of it is left.
        IsTrue(tier.TryAdd("c", 30, "c"));
        IsLessThanOrEqualTo(50L, tier.TextLength);
        IsFalse(tier.TryGetValue("a", out _));
        IsFalse(tier.TryGetValue("b", out _));
        IsTrue(tier.TryGetValue("c", out _));
        AreEqual(1, tier.Count);

        // A text over the per-text bound is never kept.
        IsFalse(tier.TryAdd("long", 41, "long"));
        IsFalse(tier.TryGetValue("long", out _));
    }

    [TestMethod]
    public void Tier_Full_AdmitsAKeyOnItsSecondArrival()
    {
        var tier = new SharedTextTier<string, string>(capacity: 1000, textBudget: 10, maxTextLength: 10);
        IsTrue(tier.TryAdd("a", 10, "a"));
        IsFalse(tier.TryAdd("b", 1, "b"), "a full tier refuses a key on its first arrival");
        IsTrue(tier.TryAdd("b", 1, "b"), "and admits it on its second, sweeping the oldest");
        IsFalse(tier.TryGetValue("a", out _));
        AreEqual(1L, tier.TextLength);
    }

    [TestMethod]
    public void Tier_UnderConcurrentReadsAndEvictions_StaysConsistent()
    {
        // Eight threads add and read over a tier far smaller than their keys,
        // so sweeps run while others read and add. Every read answers its
        // key's own value, and once quiet the tier's count and text length
        // are its entries'.
        const int Keys = 400, Threads = 8;
        var tier = new SharedTextTier<string, string>(capacity: 32, textBudget: 2_000, maxTextLength: 100);
        static int LengthOf(int key) => 1 + (key % 97);
        var wrong = 0;
        var cancellation = this.TestContext.CancellationToken;
        using var start = new Barrier(Threads);
        var threads = new Thread[Threads];
        for (var t = 0; t < Threads; t++)
        {
            var seed = t;
            threads[t] = new Thread(() =>
            {
                var random = new Random(seed);
                start.SignalAndWait(cancellation);
                for (var i = 0; i < 20_000; i++)
                {
                    var key = random.Next(Keys);
                    if (tier.TryGetValue($"k{key}", out var value))
                    {
                        if (value != $"v{key}")
                            _ = Interlocked.Increment(ref wrong);
                    }
                    else
                    {
                        _ = tier.TryAdd($"k{key}", LengthOf(key), $"v{key}");
                    }
                }
            });
            threads[t].Start();
        }
        foreach (var thread in threads)
            thread.Join();

        AreEqual(0, wrong);
        IsGreaterThan(0L, tier.Evictions, $"count {tier.Count}, text {tier.TextLength}");
        var present = 0;
        var length = 0L;
        for (var key = 0; key < Keys; key++)
        {
            if (tier.TryGetValue($"k{key}", out _))
            {
                present++;
                length += LengthOf(key);
            }
        }
        AreEqual(present, tier.Count);
        AreEqual(length, tier.TextLength);
        // A sweep finding another under way leaves its overshoot to the next.
        IsLessThanOrEqualTo(32 + Threads, tier.Count);
        IsLessThanOrEqualTo(2_000L + (Threads * 100), tier.TextLength);
    }
}
