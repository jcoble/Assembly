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
        var trigger = $"{table}_register_handle";
        if (scope.QueryFirst("SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name=$trigger",
                row => row.Long(0), new SqlArg("$trigger", trigger)) != 0)
            return;

        // The engine and the facade can refer to the same session. They share the host's handle registry.
        // A session ID gives the same word choice when a development fixture is seeded again.
        scope.Execute($"""
            CREATE TABLE IF NOT EXISTS session_handle (
              session_id TEXT PRIMARY KEY NOT NULL,
              handle TEXT UNIQUE
            );
            CREATE TRIGGER IF NOT EXISTS session_handle_allocate AFTER INSERT ON session_handle BEGIN
              UPDATE session_handle SET handle=(
                WITH RECURSIVE hash(position, value) AS (
                  VALUES(1, 0)
                  UNION ALL
                  SELECT position+1, (value*131+unicode(substr(new.session_id,position,1))) & 4095
                  FROM hash WHERE position<=length(new.session_id)
                ), candidate(name) AS MATERIALIZED (
                  SELECT json_extract('{Adjectives}', '$[' || (value & 63) || ']') || '-' ||
                         json_extract('{Nouns}', '$[' || (value >> 6) || ']')
                  FROM hash WHERE position=length(new.session_id)+1
                )
                SELECT CASE
                  WHEN NOT EXISTS (SELECT 1 FROM session_handle h WHERE h.handle=c.name)
                    THEN c.name
                  ELSE c.name || '-' || lower(hex(new.session_id)) END
                FROM candidate c)
              WHERE session_id=new.session_id;
            END;
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
}
