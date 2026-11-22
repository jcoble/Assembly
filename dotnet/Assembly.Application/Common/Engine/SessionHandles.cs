using Assembly.Application.Common.Database;

namespace Assembly.Application.Common.Engine;

/// <summary>Permanent adjective–noun handles, assigned in the transaction that inserts a session.</summary>
public static class SessionHandles
{
    private const string Adjectives = """
        [
        "amber","bold","brisk","calm","clever","cosy","crisp","dapper",
        "eager","fancy","fleet","fond","gentle","glad","golden","grand",
        "happy","hardy","humble","jolly","keen","kind","lively","lucky",
        "merry","mighty","misty","modest","neat","nimble","noble","plucky",
        "polite","proud","quick","quiet","rapid","ready","rosy","rustic",
        "sage","shiny","silver","sleek","smart","snowy","solid","spry",
        "steady","stout","sunny","swift","tidy","true","vivid","warm",
        "wise","witty","young","zesty","breezy","cheery","dusky","frosty"
        ]
        """;

    private const string Nouns = """
        [
        "badger","beacon","bison","canyon","cedar","comet","condor","coral",
        "crane","delta","dolphin","ember","falcon","fern","finch","fjord",
        "fox","gecko","glacier","harbor","hare","hawk","heron","ibis",
        "jaguar","kestrel","koala","lagoon","lark","lemur","lynx","maple",
        "marmot","meadow","mesa","moose","newt","orca","osprey","otter",
        "owl","panda","pebble","pine","puffin","quail","raven","reef",
        "ridge","robin","salmon","sparrow","spruce","stoat","summit","swan",
        "tern","thistle","tiger","trout","tundra","walrus","willow","wren"
        ]
        """;

    public static void Upgrade(IWriteScope scope, string table, string id)
    {
        if (scope.QueryFirst("SELECT COUNT(*) FROM pragma_table_info($table) WHERE name='handle'",
                row => row.Long(0), new SqlArg("$table", table)) != 0)
            return;

        // The seed is staged outside the unique index, so backfill and insertion use the exact same allocator.
        var pick = $"json_extract('{Adjectives}', '$[' || (random() & 63) || ']') || '-' || json_extract('{Nouns}', '$[' || (random() & 63) || ']')";
        scope.Execute($"""
            ALTER TABLE {table} ADD COLUMN handle TEXT;
            UPDATE {table} SET handle={pick};
            WITH ranked AS (
              SELECT {id},row_number() OVER (PARTITION BY ifnull(machine,''),handle ORDER BY {id}) AS n
              FROM {table})
            UPDATE {table} SET handle=handle || '-' || hex({id})
            WHERE {id} IN (SELECT {id} FROM ranked WHERE n>1);
            CREATE UNIQUE INDEX {table}_handle ON {table}(ifnull(machine,''),handle);
            CREATE TRIGGER {table}_assign_handle AFTER INSERT ON {table}
            WHEN new.handle IS NULL BEGIN
              UPDATE {table} SET handle=(
                WITH candidate(name) AS MATERIALIZED (SELECT {pick})
                SELECT CASE
                  WHEN NOT EXISTS (SELECT 1 FROM {table} s WHERE ifnull(s.machine,'')=ifnull(new.machine,'') AND s.handle=c.name)
                    THEN c.name
                  ELSE c.name || '-' || hex(new.{id}) END
                FROM candidate c)
              WHERE {id}=new.{id};
            END;
            """);
    }
}
