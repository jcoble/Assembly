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
        UpgradeAllocator(scope);
        var trigger = $"{table}_register_handle";
        if (scope.QueryFirst("SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name=$trigger",
                row => row.Long(0), new SqlArg("$trigger", trigger)) != 0)
            return;

        scope.Execute($"""
            INSERT INTO session_handle(session_id)
            SELECT s.{id} FROM {table} s
            WHERE NOT EXISTS (SELECT 1 FROM session_handle h WHERE h.session_id=s.{id})
            ORDER BY s.{id};
            CREATE TRIGGER {trigger} AFTER INSERT ON {table} BEGIN
              INSERT INTO session_handle(session_id)
              SELECT new.{id} WHERE NOT EXISTS (SELECT 1 FROM session_handle WHERE session_id=new.{id});
            END;
            """);
    }

    private static void UpgradeAllocator(IWriteScope scope)
    {
        if (scope.QueryFirst("SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name='session_handle_allocate_prefix'",
                row => row.Long(0)) != 0)
            return;

        // Engine and facade rows share the host registry. The same ID picks the same pair in repeatable seeds.
        scope.Execute($"""
            CREATE TABLE IF NOT EXISTS session_handle (
              session_id TEXT PRIMARY KEY NOT NULL,
              handle TEXT UNIQUE
            );
            DROP TRIGGER IF EXISTS session_handle_allocate;
            CREATE TRIGGER session_handle_allocate_prefix AFTER INSERT ON session_handle WHEN new.handle IS NULL BEGIN
              UPDATE session_handle SET handle=(
                WITH RECURSIVE hash(position, value) AS (
                  VALUES(1, 0)
                  UNION ALL
                  SELECT position+1, (value*131+unicode(substr(new.session_id,position,1))) & 4095
                  FROM hash WHERE position<=length(new.session_id)
                ), pair(name) AS MATERIALIZED (
                  SELECT json_extract('{Adjectives}', '$[' || (value & 63) || ']') || '-' ||
                         json_extract('{Nouns}', '$[' || (value >> 6) || ']')
                  FROM hash WHERE position=length(new.session_id)+1
                ), candidates(prefix, name) AS (
                  SELECT min(8, length(new.session_id))-1, name FROM pair
                  UNION ALL
                  SELECT c.prefix+1, pair.name || '-' || substr(new.session_id,1,c.prefix+1) ||
                         CASE WHEN c.prefix>=length(new.session_id)
                              THEN '-' || (c.prefix-length(new.session_id)+2) ELSE '' END
                  FROM candidates c CROSS JOIN pair
                  WHERE EXISTS (SELECT 1 FROM session_handle h WHERE h.handle=c.name)
                )
                SELECT c.name FROM candidates c
                WHERE NOT EXISTS (SELECT 1 FROM session_handle h WHERE h.handle=c.name) LIMIT 1)
              WHERE session_id=new.session_id;
            END;

            -- Release only the old hex suffixes before assigning replacements in a stable order.
            UPDATE session_handle SET handle=NULL
            WHERE handle GLOB '*-*-*' AND substr(handle,-length(hex(session_id))-1)='-' || lower(hex(session_id));
            INSERT OR REPLACE INTO session_handle(session_id)
            SELECT session_id FROM session_handle WHERE handle IS NULL ORDER BY session_id;
            """);
    }
}
