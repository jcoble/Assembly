using Assembly.Application.Common.Database;

namespace Assembly.Engine.Persistence;

internal static class ComposerSchemaUpgrade
{
    internal static void Apply(IWriteScope scope)
    {
        scope.Execute("""
            ALTER TABLE engine_message ADD COLUMN framed INTEGER NOT NULL DEFAULT 1 CHECK(framed IN (0,1));
            ALTER TABLE engine_message ADD COLUMN attachment_ids_json TEXT NOT NULL DEFAULT '[]'
             CHECK(json_valid(attachment_ids_json) AND json_type(attachment_ids_json)='array');
            ALTER TABLE engine_message ADD COLUMN held_epoch INTEGER NULL;
            ALTER TABLE engine_message ADD COLUMN target_epoch INTEGER NULL CHECK(target_epoch IS NULL OR target_epoch>=0);
            ALTER TABLE engine_message ADD COLUMN owner_content_sha256 TEXT NULL CHECK(owner_content_sha256 IS NULL OR
             (length(owner_content_sha256)=64 AND owner_content_sha256 NOT GLOB '*[^0-9a-f]*'));
            """);
        if (scope.Query("PRAGMA foreign_key_check", r => r.Text(0)).Count != 0)
            throw new InvalidOperationException("Engine composer migration found invalid foreign keys.");
        scope.Execute("UPDATE engine_meta SET schema_version=5 WHERE singleton=1");
    }
}
