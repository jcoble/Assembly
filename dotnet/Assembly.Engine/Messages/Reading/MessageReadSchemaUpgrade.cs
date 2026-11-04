using Assembly.Application.Common.Database;

namespace Assembly.Engine.Messages.Reading;

/// <summary>Read provenance stays on its existing message; ambiguous historical origins remain unknown.</summary>
public static class MessageReadSchemaUpgrade
{
    public static void Apply(IWriteScope scope)
    {
        scope.Execute("""
            ALTER TABLE engine_message ADD COLUMN origin_machine TEXT NULL;
            ALTER TABLE engine_message ADD COLUMN message_created_seq INTEGER NULL CHECK(message_created_seq IS NULL OR message_created_seq>0);
            UPDATE engine_message SET origin_machine=CASE
                WHEN from_session_id IS NOT NULL THEN (SELECT machine FROM engine_session WHERE id=engine_message.from_session_id)
                WHEN sender_kind='window' THEN machine END;
            UPDATE engine_message SET message_created_seq=(SELECT MIN(seq) FROM engine_event
                WHERE engine_event.machine=engine_message.machine AND entity_kind='message'
                  AND entity_id=engine_message.id AND entity_version=1 AND kind='message.changed')
                WHERE origin_machine IS NOT NULL;
            CREATE INDEX engine_message_read_order ON engine_message(machine,created_ms DESC,id);
            UPDATE engine_meta SET schema_version=14 WHERE singleton=1;
            """);
    }
}
