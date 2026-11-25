using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER TABLE … DROP COLUMN</c> moves every later column down a position,
/// which every key, index, statistic and lock anchor naming a later column has
/// to follow, and a rolled-back drop has to move each of them back. Each case
/// runs over a table that lost its leading column while it was empty, one that
/// lost it holding rows, and one of each whose drop rolled back, and all four
/// must answer as a table the column was never in does.
/// </summary>
[TestClass]
public sealed class DropColumnIndexPositionTests
{
    private const string Abc = "a int not null, b int not null, c varchar(20)";
    private const string AbcComputed = Abc + ", d as b * 2";
    private const string AbcRows = "insert t (a, b, c) values (1, 10, 'p'), (2, 20, 'q'), (3, 30, 'r')";
    private const string DropX = "alter table t drop column x";
    private const string RolledBackDropX = "begin tran; alter table t drop column x; rollback";
    private const string IndexColumns = """
        select concat(i.name, ':', ic.key_ordinal, ':', c.name, ':', ic.is_included_column, ':', ic.is_descending_key)
        from sys.indexes i
        join sys.index_columns ic on ic.object_id = i.object_id and ic.index_id = i.index_id
        join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
        where i.object_id = object_id('t') and i.name like 'ix%'
        order by i.name, ic.index_column_id
        """;

    // The rows every result set holds, then the number of the error raised.
    private static string Outcome(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(sql);
        var rows = new List<string>();
        try
        {
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    _ = reader.GetValues(values);
                    rows.Add(string.Join(",", values));
                }
            }
            while (reader.NextResult());
        }
        catch (SimulatedSqlException exception)
        {
            rows.Add("Msg " + exception.Number);
        }
        return string.Join(";", rows);
    }

    private static string AfterSetup(string probe, params string[] setup)
    {
        var simulation = new Simulation();
        foreach (var statement in setup)
            _ = simulation.ExecuteNonQuery(statement);
        return Outcome(simulation, probe);
    }

    private static void SameHoweverTheColumnLeft(string columns, string ddl, string rows, string probe, string expected)
    {
        var without = $"create table t ({columns})";
        var with = $"create table t (x int, {columns})";
        AreEqual(expected, AfterSetup(probe, without, ddl, rows), "never had it");
        AreEqual(expected, AfterSetup(probe, with, ddl, DropX, rows), "dropped while empty");
        AreEqual(expected, AfterSetup(probe, with, ddl, rows, DropX), "dropped with rows");
        AreEqual(expected, AfterSetup(probe, with, ddl, RolledBackDropX, rows), "rolled back while empty");
        AreEqual(expected, AfterSetup(probe, with, ddl, rows, RolledBackDropX), "rolled back with rows");
    }

    // --- What a rowstore index or key answers ---

    [TestMethod]
    [DataRow("create index ix on t(b)", "select a, b, c from t where b = 20", "2,20,q", DisplayName = "seek")]
    [DataRow("create index ix on t(b)", "select a, b, c from t with (forceseek) where b = 20", "2,20,q", DisplayName = "forceseek")]
    [DataRow("create index ix on t(b) include (c)", "select b, c from t with (index(ix)) where b = 20", "20,q", DisplayName = "index hint with an included column")]
    [DataRow("create index ix on t(c)", "select t.a from t join (values ('q'), ('r')) v(c) on v.c = t.c order by 1", "2;3", DisplayName = "join")]
    [DataRow("create index ix on t(c desc)", "select c from t order by c desc", "r;q;p", DisplayName = "ordered read")]
    [DataRow("create clustered index ix on t(b desc)", "select b from t", "30;20;10", DisplayName = "clustered order")]
    [DataRow("create index ix on t(b desc) include (c); create unique index ix2 on t(a)", IndexColumns, "ix:1:b:0:1;ix:0:c:1:0;ix2:1:a:0:0", DisplayName = "sys.index_columns")]
    [DataRow("create nonclustered columnstore index ix on t(b, c)", IndexColumns, "ix:0:b:1:0;ix:0:c:1:0", DisplayName = "columnstore sys.index_columns")]
    [DataRow("create unique index ix on t(b)", "insert t (a, b, c) values (9, 20, 'z')", "Msg 2601", DisplayName = "unique duplicate")]
    [DataRow("create unique index ix on t(c)", "insert t (a, b, c) values (9, 99, 'q')", "Msg 2601", DisplayName = "unique duplicate on the last column")]
    [DataRow("create unique index ix on t(c)", "insert t (a, b, c) values (9, 99, 'z'); select count(*) from t", "4", DisplayName = "unique insert on the last column")]
    [DataRow("create unique index ix on t(c)", "update t set c = 'q' where a = 1", "Msg 2601", DisplayName = "unique update")]
    [DataRow("create unique index ix on t(c) where c is not null", "insert t (a, b, c) values (9, 99, null); insert t (a, b, c) values (8, 98, 'q')", "Msg 2601", DisplayName = "filtered unique")]
    [DataRow("create unique index ix on t(c)", "alter index ix on t rebuild; select c from t where c = 'q'", "q", DisplayName = "rebuild")]
    [DataRow("create unique index ix on t(b) with (ignore_dup_key = on)", "insert t (a, b, c) values (9, 20, 'z'), (8, 88, 'y'); select a, b from t order by b", "1,10;2,20;3,30;8,88", DisplayName = "ignore_dup_key")]
    [DataRow("alter table t add constraint pk primary key (a)", "insert t (a, b, c) values (2, 99, 'z')", "Msg 2627", DisplayName = "primary key duplicate")]
    [DataRow("alter table t add constraint pk primary key (a)", "insert t (a, b, c) values (9, 99, 'z'); update t set a = 8 where a = 9; delete t where a = 1; select a, b, c from t order by a", "2,20,q;3,30,r;8,99,z", DisplayName = "primary key writes")]
    [DataRow("alter table t add constraint uq unique (c)", "insert t (a, b, c) values (9, 99, 'q')", "Msg 2627", DisplayName = "unique constraint on the last column")]
    [DataRow("create unique index ix on t(b)", "create table child (b int references t(b)); insert child values (20); select count(*) from child", "1", DisplayName = "foreign key over a unique index")]
    [DataRow("create unique index ix on t(b)", "create table child (b int references t(b)); insert child values (77)", "Msg 547", DisplayName = "foreign key over a unique index, violated")]
    [DataRow("alter table t add constraint pk primary key (a); create table child (a int references t(a))", "insert child values (9)", "Msg 547", DisplayName = "referenced primary key, violated")]
    [DataRow("alter table t add constraint pk primary key (a); create table child (a int references t(a))", "insert child values (2); select count(*) from child", "1", DisplayName = "referenced primary key")]
    [DataRow("create table parent (b int primary key); insert parent values (10), (20), (30); alter table t add constraint fk foreign key (b) references parent(b)", "insert t (a, b, c) values (4, 30, 's'); select count(*) from t", "4", DisplayName = "referencing foreign key")]
    [DataRow("create table parent (b int primary key); insert parent values (10), (20), (30); alter table t add constraint fk foreign key (b) references parent(b)", "insert t (a, b, c) values (5, 55, 's')", "Msg 547", DisplayName = "referencing foreign key, violated")]
    [DataRow("create index ix on t(c)", "update t set c = 'zz' where a = 1; select user_updates from sys.dm_db_index_usage_stats where object_id = object_id('t') and index_id = 2", "2", DisplayName = "usage counts a write of the key")]
    public void Rowstore(string ddl, string probe, string expected) =>
        SameHoweverTheColumnLeft(Abc, ddl, AbcRows, probe, expected);

    [TestMethod]
    [DataRow("create unique index ix on t(d)", "insert t (a, b, c) values (9, 20, 'z')", "Msg 2601", DisplayName = "index duplicate")]
    [DataRow("create unique index ix on t(d)", "insert t (a, b, c) values (9, 11, 'z'); select a, d from t where d = 22", "9,22", DisplayName = "index insert")]
    [DataRow("alter table t add constraint uq unique (d)", "insert t (a, b, c) values (9, 20, 'z')", "Msg 2627", DisplayName = "unique constraint duplicate")]
    [DataRow("alter table t add constraint uq unique (d)", "update t set b = 20 where a = 1", "Msg 2627", DisplayName = "unique constraint update")]
    [DataRow("create index ix on t(a) include (d)", IndexColumns, "ix:1:a:0:0;ix:0:d:1:0", DisplayName = "included column")]
    public void ComputedKey(string ddl, string probe, string expected) =>
        SameHoweverTheColumnLeft(AbcComputed, ddl, AbcRows, probe, expected);

    // --- What a key or index blocks ---

    [TestMethod]
    [DataRow(Abc, "create index ix on t(b)", "alter table t drop column b", "Msg 5074")]
    [DataRow(Abc, "create index ix on t(b)", "alter table t drop column a; select b, c from t where b = 20", "20,q")]
    [DataRow(Abc, "create index ix on t(a) include (c)", "alter table t drop column c", "Msg 5074")]
    [DataRow(Abc, "create index ix on t(b)", "alter table t alter column b bigint not null", "Msg 5074")]
    [DataRow(Abc, "create index ix on t(b)", "alter table t alter column a bigint not null; select a from t where b = 20", "2")]
    [DataRow(Abc, "alter table t add constraint pk primary key (a)", "alter table t alter column a bigint not null", "Msg 5074")]
    [DataRow(AbcComputed, "create index ix on t(d)", "alter table t drop column d", "Msg 5074")]
    [DataRow(AbcComputed, "alter table t add constraint uq unique (d)", "alter table t drop column d", "Msg 5074")]
    [DataRow(AbcComputed, "create index ix on t(a) include (d)", "alter table t drop column d", "Msg 5074")]
    public void Blockers(string columns, string ddl, string probe, string expected) =>
        SameHoweverTheColumnLeft(columns, ddl, AbcRows, probe, expected);

    // --- The indexes that are no rowstore ---

    [TestMethod]
    [DataRow("g geometry", "create spatial index ix on t(g) with (bounding_box = (0, 0, 10, 10))", DisplayName = "spatial")]
    [DataRow("g xml", "create primary xml index ix on t(g)", DisplayName = "xml")]
    [DataRow("g json", "create json index ix on t(g)", DisplayName = "json")]
    public void SpecialIndex_NamesAndBlocksItsColumn(string column, string ddl)
    {
        var columns = "a int not null primary key, n int, " + column;
        const string rows = "insert t (a, n, g) values (1, 2, null)";
        const string named = """
            select c.name from sys.indexes i
            join sys.index_columns ic on ic.object_id = i.object_id and ic.index_id = i.index_id
            join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
            where i.object_id = object_id('t') and i.name = 'ix'
            """;
        SameHoweverTheColumnLeft(columns, ddl, rows, named, "g");
        SameHoweverTheColumnLeft(columns, ddl, rows, "alter table t drop column g", "Msg 5074");
        SameHoweverTheColumnLeft(columns, ddl, rows, "alter table t drop column n; " + named, "g");
    }

    private const string FullTextColumns = "a int not null constraint pk primary key, n int, e varchar(10), g varchar(50), h varchar(50)";
    private const string FullTextDdl = "create fulltext catalog fc as default; create fulltext index on t(g) key index pk";
    private const string FullTextRows = "insert t (a, n, e, g, h) values (1, 2, '.txt', 'hello world', 'zebra')";

    [TestMethod]
    [DataRow("select a from t where contains(g, 'hello')", "1")]
    [DataRow("select a from t where contains(*, 'hello')", "1")]
    [DataRow("select a from t where contains(h, 'zebra')", "Msg 7601")]
    [DataRow("select t.a from t join containstable(t, g, 'hello') k on k.[key] = t.a", "1")]
    [DataRow("alter table t drop column g", "Msg 7614")]
    [DataRow("alter table t drop column h; select a from t where contains(g, 'hello')", "1")]
    [DataRow("alter fulltext index on t add (h); exec('select a from t where contains(h, ''zebra'')')", "1")]
    [DataRow("alter fulltext index on t add (h); alter fulltext index on t drop (g); exec('select a from t where contains(*, ''zebra'')')", "1")]
    [DataRow("select c.name from sys.fulltext_index_columns f join sys.columns c on c.object_id = f.object_id and c.column_id = f.column_id", "g")]
    [DataRow("select display_term from sys.dm_fts_index_keywords(db_id(), object_id('t')) order by 1", "END OF FILE;hello;world")]
    [DataRow("select fulltextcatalogproperty('fc', 'UniqueKeyCount')", "3")]
    public void FullText(string probe, string expected) =>
        SameHoweverTheColumnLeft(FullTextColumns, FullTextDdl, FullTextRows, probe, expected);

    [TestMethod]
    [DataRow("select a from t where contains(g, 'hello')", "1")]
    [DataRow("alter table t drop column e", "Msg 5074")]
    [DataRow("alter table t drop column n; select a from t where contains(g, 'hello')", "1")]
    [DataRow("select c.name from sys.fulltext_index_columns f join sys.columns c on c.object_id = f.object_id and c.column_id = f.type_column_id", "e")]
    public void FullTextTypeColumn(string probe, string expected) =>
        SameHoweverTheColumnLeft(
            "a int not null constraint pk primary key, n int, e varchar(10), g varbinary(max)",
            "create fulltext catalog fc as default; create fulltext index on t(g type column e) key index pk",
            "insert t (a, n, e, g) values (1, 2, '.txt', cast('hello world' as varbinary(max)))",
            probe,
            expected);

    [TestMethod]
    public void FullTextIndexCreatedAfterTheDrop_ReportsTheColumnsIds()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (x int, {FullTextColumns}); alter table t drop column x");
        _ = simulation.ExecuteNonQuery(FullTextDdl);
        _ = simulation.ExecuteNonQuery(FullTextRows);
        AreEqual("g", simulation.ExecuteScalar("select c.name from sys.fulltext_index_columns f join sys.columns c on c.object_id = f.object_id and c.column_id = f.column_id"));
        AreEqual(1, simulation.ExecuteScalar("select columnproperty(object_id('t'), 'g', 'IsFulltextIndexed')"));
        AreEqual(1, simulation.ExecuteScalar("select a from t where contains(g, 'hello')"));
    }

    [TestMethod]
    public void OrderedColumnstore_KeepsItsOrderColumn()
    {
        const string probe = """
            select c.name from sys.index_columns ic
            join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
            where ic.object_id = object_id('t') and ic.column_store_order_ordinal = 1
            """;
        SameHoweverTheColumnLeft(Abc, "create clustered columnstore index ix on t order (b)", AbcRows, probe, "b");
    }

    // --- What is cached per index ---

    [TestMethod]
    public void SeekCacheOfAnEmptyTable_DoesNotOutliveTheLayout()
    {
        // The seek while empty caches the two-column key under its positions;
        // after the drop its leading position is another column's.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (x int, a int not null, b int not null); create index ia on t(a, b); create index ib on t(b)");
        _ = simulation.ExecuteNonQuery("select * from t where a = 1 and b = 2; select * from t where a = 1");
        _ = simulation.ExecuteNonQuery(DropX);
        _ = simulation.ExecuteNonQuery("insert t values (1, 2), (3, 4)");
        AreEqual("1,2", Outcome(simulation, "select a, b from t where b = 2"));
        AreEqual("3,4", Outcome(simulation, "select a, b from t where a = 3 and b = 4"));
        AreEqual("1,2", Outcome(simulation, "select a, b from t with (forceseek) where a = 1 and b = 2"));
    }

    [TestMethod]
    public void SeekCacheOfAnEmptiedTable_DoesNotReplayTheOldLayout()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (x varchar(20), a int not null, b int not null); create index ia on t(a, b); create index ib on t(b)");
        _ = simulation.ExecuteNonQuery("insert t values ('old', 1, 2); select * from t where b = 2");
        _ = simulation.ExecuteNonQuery("delete t; insert t values ('old', 5, 6); delete t");
        _ = simulation.ExecuteNonQuery(DropX);
        _ = simulation.ExecuteNonQuery("insert t values (7, 8)");
        AreEqual("7,8", Outcome(simulation, "select a, b from t where b = 8"));
        AreEqual("7,8", Outcome(simulation, "select a, b from t where a = 7 and b = 8"));
    }

    [TestMethod]
    public void KeyLocks_FollowTheColumn()
    {
        // A serializable read anchors its key locks on the index's columns,
        // and an index's anchors are kept from one transaction to the next.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (x int, {Abc}); create index ix on t(b); {AbcRows}");
        const string lockOne = "begin tran; select a from t with (holdlock, forceseek) where b = 20;";
        _ = simulation.ExecuteNonQuery(lockOne + " commit");
        _ = simulation.ExecuteNonQuery(DropX);

        using var holder = simulation.CreateOpenConnection();
        using var writer = simulation.CreateOpenConnection();
        _ = holder.CreateCommand(lockOne).ExecuteNonQuery();
        _ = writer.CreateCommand("set lock_timeout 0").ExecuteNonQuery();
        _ = writer.CreateCommand("insert t (a, b, c) values (9, 99, 'far')").ExecuteNonQuery();
        var blocked = Throws<SimulatedSqlException>(() => writer.CreateCommand("insert t (a, b, c) values (8, 20, 'same')").ExecuteNonQuery());
        AreEqual(1222, blocked.Number);
        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
        _ = writer.CreateCommand("insert t (a, b, c) values (8, 20, 'same')").ExecuteNonQuery();
    }

    [TestMethod]
    public void KeyLocksHeldAcrossTheSameTransactionsDrop_AreReleased()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (x int, {Abc}); create index ix on t(b); {AbcRows}");
        _ = simulation.ExecuteNonQuery($"begin tran; select a from t with (holdlock, forceseek) where b = 20; {DropX}; select a from t with (holdlock, forceseek) where b = 20; commit");
        using var writer = simulation.CreateOpenConnection();
        _ = writer.CreateCommand("set lock_timeout 0").ExecuteNonQuery();
        _ = writer.CreateCommand("insert t (a, b, c) values (8, 20, 'same')").ExecuteNonQuery();
        AreEqual("0", Outcome(simulation, "select count(*) from sys.dm_tran_locks where resource_type = 'KEY'"));
    }

    [TestMethod]
    [DataRow("alter table t add constraint pk primary key (a)", "select 1", DisplayName = "gained")]
    [DataRow("alter table t add constraint pk primary key (a)", "alter table t drop constraint pk", DisplayName = "lost")]
    [DataRow("create clustered index cx on t(a)", "create clustered index cx on t(c) with (drop_existing = on)", DisplayName = "exchanged")]
    public void KeyLocks_FollowTheClusteredKey(string first, string second)
    {
        // A non-unique nonclustered index's anchors end in the clustered key.
        const string locked = "begin tran; select a from t with (holdlock, index(ix), forceseek) where b = 20; select rtrim(resource_description) from sys.dm_tran_locks where resource_type = 'KEY' order by 1; commit";
        var create = $"create table t ({Abc}); create index ix on t(b); {AbcRows}";
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(create);
        _ = Outcome(simulation, locked);
        _ = simulation.ExecuteNonQuery(first);
        _ = Outcome(simulation, locked);
        _ = simulation.ExecuteNonQuery(second);
        AreEqual(AfterSetup(locked, create, first, second), Outcome(simulation, locked));
    }

    [TestMethod]
    public void KeyLocks_OutliveAnotherSessionsDdlThatLeavesTheLayoutAlone()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ({Abc}); create index ix on t(b); {AbcRows}");
        using var holder = simulation.CreateOpenConnection();
        using var other = simulation.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; select a from t with (holdlock, forceseek) where b = 20").ExecuteNonQuery();
        _ = other.CreateCommand("set lock_timeout 0").ExecuteNonQuery();
        try
        {
            _ = other.CreateCommand("begin tran; create statistics sc on t(c); rollback").ExecuteNonQuery();
        }
        catch (SimulatedSqlException refused)
        {
            AreEqual(1222, refused.Number);
        }
        _ = other.CreateCommand("if @@trancount > 0 rollback").ExecuteNonQuery();
        var blocked = Throws<SimulatedSqlException>(() => other.CreateCommand("insert t (a, b, c) values (8, 20, 'same')").ExecuteNonQuery());
        AreEqual(1222, blocked.Number);
    }

    // --- Statistics counters ---

    [TestMethod]
    [DataRow("alter table t drop column x")]
    [DataRow("alter table t add n int not null default 1")]
    [DataRow("alter table t alter column x bigint")]
    [DataRow("begin tran; alter table t drop column x; rollback")]
    public void RewritingTheRows_IsNoModification(string statement)
    {
        const string counter = """
            select p.modification_counter from sys.stats s
            cross apply sys.dm_db_stats_properties(s.object_id, s.stats_id) p
            where s.object_id = object_id('t') and s.name = 'sc'
            """;
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (x int, {Abc}); {AbcRows}; delete t where a > 1; {AbcRows}; delete t where a > 1");
        _ = simulation.ExecuteNonQuery("create statistics sc on t(c); insert t (a, b, c) values (4, 40, 's')");
        AreEqual("1", Outcome(simulation, counter));
        _ = simulation.ExecuteNonQuery(statement);
        AreEqual("1", Outcome(simulation, counter));
    }

    [TestMethod]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    [DataRow("begin tran; alter table t drop column x; update t set c = c + 'd'; update statistics t sc; rollback; update statistics t sc")]
    [DataRow("begin tran; alter table t drop column x; update t set c = c + 'd'; exec sp_updatestats; rollback; update statistics t sc")]
    public void StatisticFreshBeforeTheRewrite_IsStaleAfterAWrite(string statement)
    {
        const string counter = """
            select p.modification_counter from sys.stats s
            cross apply sys.dm_db_stats_properties(s.object_id, s.stats_id) p
            where s.object_id = object_id('t') and s.name = 'sc'
            """;
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (x int, {Abc}); {AbcRows}; update t set c = c + 'a'; update t set c = c + 'b'; create statistics sc on t(c)");
        _ = simulation.ExecuteNonQuery(statement);
        _ = simulation.ExecuteNonQuery("update t set c = 'k' where a = 1");
        AreEqual("1", Outcome(simulation, counter));
        _ = simulation.ExecuteNonQuery("exec sp_updatestats");
        AreEqual("0", Outcome(simulation, counter));
    }

    [TestMethod]
    public void ModificationCounter_FollowsTheColumn()
    {
        const string counter = """
            select p.modification_counter from sys.stats s
            cross apply sys.dm_db_stats_properties(s.object_id, s.stats_id) p
            where s.object_id = object_id('t') and s.name = 'sc'
            """;
        static string Counted(string after)
        {
            var simulation = new Simulation();
            _ = simulation.ExecuteNonQuery($"create table t (x int, {Abc}); {AbcRows}; create statistics sc on t(c); update t set b = b + 1; update t set c = c + 'z' where a = 1");
            _ = simulation.ExecuteNonQuery(after);
            return Outcome(simulation, counter);
        }

        AreEqual("1", Counted("select 1"));
        AreEqual("1", Counted(DropX));
        AreEqual("1", Counted(RolledBackDropX));
    }

    // --- Rollbacks in their other shapes ---

    [TestMethod]
    public void SeveralDropsRolledBackToASavepoint()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t (x int, y int, z int, {AbcComputed}, primary key (a)); create unique index ix on t(c) include (b); create unique index ix2 on t(d); {AbcRows}");
        _ = simulation.ExecuteNonQuery("begin tran; alter table t drop column y; save tran s; alter table t drop column x; alter table t drop column z; rollback tran s; commit");
        AreEqual("ix:1:c:0:0;ix:0:b:1:0;ix2:1:d:0:0", Outcome(simulation, IndexColumns));
        AreEqual("Msg 2601", Outcome(simulation, "insert t (a, b, c) values (9, 99, 'q')"));
        AreEqual("Msg 2601", Outcome(simulation, "insert t (a, b, c) values (9, 20, 'z')"));
        AreEqual("Msg 2627", Outcome(simulation, "insert t (a, b, c) values (2, 99, 'z')"));
        AreEqual("2,20,q", Outcome(simulation, "insert t (a, b, c) values (9, 99, 'z'); select a, b, c from t where c = 'q'"));
    }

    [TestMethod]
    public void SelfReferencingForeignKey_RolledBack()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (x int, a int not null primary key, p int references t(a)); insert t values (0, 1, null), (0, 2, 1)");
        _ = simulation.ExecuteNonQuery(RolledBackDropX);
        AreEqual("3", Outcome(simulation, "insert t (a, p) values (3, 2); select count(*) from t"));
        AreEqual("Msg 547", Outcome(simulation, "insert t (a, p) values (4, 77)"));
        _ = simulation.ExecuteNonQuery(DropX);
        AreEqual("4", Outcome(simulation, "insert t (a, p) values (4, 3); select count(*) from t"));
        AreEqual("Msg 547", Outcome(simulation, "delete t where a = 1"));
    }

    [TestMethod]
    public void BothEndsOfAForeignKeyDropAColumn_RolledBack()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table p (x int, a int primary key); create table t (x int, n int, a int references p(a)); insert p values (0, 1)");
        _ = simulation.ExecuteNonQuery("begin tran; alter table p drop column x; alter table t drop column x; alter table t drop column n; rollback");
        AreEqual("1", Outcome(simulation, "insert t (a) values (1); select count(*) from t"));
        AreEqual("Msg 547", Outcome(simulation, "insert t (a) values (2)"));
        _ = simulation.ExecuteNonQuery("alter table p drop column x; alter table t drop column n");
        AreEqual("2", Outcome(simulation, "insert t (a) values (1); select count(*) from t"));
        AreEqual("Msg 547", Outcome(simulation, "delete p where a = 1"));
    }

    [TestMethod]
    public void TemporaryTable_RolledBack()
    {
        AreEqual("Msg 2601", Outcome(new Simulation(), """
            create table #t (x int, a int, b int not null);
            create unique index ix on #t(b);
            insert #t values (0, 1, 10);
            begin tran; alter table #t drop column x; rollback;
            insert #t values (0, 2, 10)
            """));
    }

    [TestMethod]
    [DataRow(DropX)]
    [DataRow(RolledBackDropX)]
    public void SystemVersionedTable_FollowsInBothTables(string statement)
    {
        const string create = """
            create table t (x int, a int not null primary key, b int not null,
                s datetime2 generated always as row start not null, e datetime2 generated always as row end not null,
                period for system_time (s, e)) with (system_versioning = on (history_table = dbo.th));
            create unique index ix on t(b);
            create index ixh on th(b);
            """;
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(create);
        _ = simulation.ExecuteNonQuery("insert t (a, b) values (1, 10)");
        _ = simulation.ExecuteNonQuery(statement);
        AreEqual("1;2", Outcome(simulation, "update t set b = 11 where b = 10; select count(*) from th where b = 10; select count(*) from t for system_time all"));
        AreEqual("Msg 2601", Outcome(simulation, "insert t (a, b) values (2, 11)"));
    }
}
