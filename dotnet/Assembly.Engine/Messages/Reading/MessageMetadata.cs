namespace Assembly.Engine.Messages.Reading;

/// <summary>The one SQL projection over local messages and committed mirror descriptors. No body enters a mirror.</summary>
public static class MessageMetadata
{
    internal const string Cte = """
        WITH session_names AS (
          SELECT machine,id,substr(title,1,256) AS name FROM engine_session WHERE machine=$local
          UNION ALL
          SELECT mr.machine,mr.entity_id,substr(json_extract(mr.row_json,'$.title'),1,256)
          FROM engine_mirror_row mr JOIN engine_mirror mm ON mm.machine=mr.machine AND mm.generation=mr.generation
          WHERE mr.entity_kind='session' AND mm.state='live'
        ), physical AS (
          SELECT m.machine AS storage_machine,m.id,m.origin_machine,m.created_ms,m.version,
            COALESCE(s.machine,m.origin_machine) AS sender_machine,m.from_session_id,
            substr(m.sender_label,1,256) AS sender_name,r.machine AS recipient_machine,m.to_session_id,
            substr(COALESCE(r.title,m.to_session_id),1,256) AS receiver_name,wr.machine AS run_machine,m.run_id,
            owner.machine AS run_owner_machine,wr.owner_session_id AS run_owner_id,
            m.sender_kind AS kind,m.state,m.delivery,m.reason,
            substr(replace(replace(m.text,char(13),' '),char(10),' '),1,256) AS preview,m.message_created_seq
          FROM engine_message m
          LEFT JOIN engine_session s ON s.id=m.from_session_id
          LEFT JOIN engine_session r ON r.id=m.to_session_id
          LEFT JOIN engine_run wr ON wr.id=m.run_id
          LEFT JOIN engine_session owner ON owner.id=wr.owner_session_id
          WHERE m.machine=$local
          UNION ALL
          SELECT mr.machine,mr.entity_id,json_extract(mr.row_json,'$.originMachine'),json_extract(mr.row_json,'$.createdMs'),mr.version,
            json_extract(mr.row_json,'$.fromMachine'),json_extract(mr.row_json,'$.fromSessionId'),
            json_extract(mr.row_json,'$.senderName'),json_extract(mr.row_json,'$.toMachine'),json_extract(mr.row_json,'$.toSessionId'),
            json_extract(mr.row_json,'$.receiverName'),json_extract(mr.row_json,'$.runMachine'),json_extract(mr.row_json,'$.runId'),
            json_extract(mr.row_json,'$.runOwnerMachine'),json_extract(mr.row_json,'$.runOwnerSessionId'),
            json_extract(mr.row_json,'$.senderKind'),json_extract(mr.row_json,'$.state'),json_extract(mr.row_json,'$.delivery'),
            json_extract(mr.row_json,'$.reason'),substr(replace(replace(json_extract(mr.row_json,'$.text'),char(13),' '),char(10),' '),1,256),
            json_extract(mr.row_json,'$.messageCreatedSeq')
          FROM engine_mirror_row mr JOIN engine_mirror mm ON mm.machine=mr.machine AND mm.generation=mr.generation
          WHERE mr.entity_kind='message' AND mm.state='live'
        ), origin AS (
          SELECT storage_machine,id,origin_machine,created_ms,version,sender_machine,from_session_id,sender_name,
            recipient_machine,to_session_id,receiver_name,run_machine,run_id,run_owner_machine,run_owner_id,
            kind,state,delivery,reason,preview,message_created_seq
          FROM physical WHERE storage_machine=origin_machine AND sender_machine IS NOT NULL AND recipient_machine IS NOT NULL AND to_session_id IS NOT NULL
        ), observed AS (
          SELECT p.storage_machine,p.id,p.origin_machine,p.created_ms,p.version,p.sender_machine,p.from_session_id,p.sender_name,
            p.recipient_machine,p.to_session_id,p.receiver_name,p.run_machine,p.run_id,p.run_owner_machine,p.run_owner_id,
            p.kind,p.state,p.delivery,p.reason,p.preview,p.message_created_seq,ROW_NUMBER() OVER(PARTITION BY origin_machine,id ORDER BY
            CASE WHEN storage_machine=recipient_machine THEN 0 ELSE 1 END,version DESC,storage_machine) AS copy_rank
          FROM physical p WHERE origin_machine IS NOT NULL
        ), canonical AS (
          SELECT o.origin_machine,o.id,o.created_ms AS origin_created_ms,o.sender_machine,o.from_session_id,
            o.sender_name,o.recipient_machine,o.to_session_id,COALESCE(recipient.name,o.receiver_name) AS receiver_name,o.run_machine,o.run_id,
            o.run_owner_machine,o.run_owner_id,o.kind,o.preview,o.message_created_seq,
            receipt.storage_machine,receipt.version,receipt.state,receipt.delivery,receipt.reason
          FROM origin o JOIN observed receipt ON receipt.origin_machine=o.origin_machine AND receipt.id=o.id AND receipt.copy_rank=1
          LEFT JOIN session_names recipient ON recipient.machine=o.recipient_machine AND recipient.id=o.to_session_id
          WHERE receipt.delivery IS NOT NULL
        )
        """;

    internal const string Scope = """
        ($storage IS NULL OR EXISTS(SELECT 1 FROM physical p WHERE p.origin_machine=c.origin_machine AND p.id=c.id AND p.storage_machine=$storage))
        AND ($session IS NULL OR (c.sender_machine=$sessionMachine AND c.from_session_id=$session)
          OR (c.recipient_machine=$sessionMachine AND c.to_session_id=$session)
          OR (c.run_owner_machine=$sessionMachine AND c.run_owner_id=$session))
        AND ($run IS NULL OR c.run_machine=$runMachine AND c.run_id=$run)
        AND ($state IS NULL OR c.state=$state)
        """;

    internal static string Name(string sort) => sort switch
    {
        "time" => "''",
        "sender" => "COALESCE(c.sender_name,'') COLLATE NOCASE",
        "receiver" => "COALESCE(c.receiver_name,'') COLLATE NOCASE",
        _ => throw new ArgumentException("bad_args")
    };
    internal static string Order(string sort) => $"{Name(sort)} ASC,c.origin_created_ms DESC,c.origin_machine COLLATE BINARY,c.id COLLATE BINARY";
    internal const string After = """
        ($afterId IS NULL OR ordering_name COLLATE NOCASE>$afterName COLLATE NOCASE
          OR ordering_name COLLATE NOCASE=$afterName COLLATE NOCASE AND
            (origin_created_ms<$afterTime OR origin_created_ms=$afterTime AND
              (origin_machine COLLATE BINARY>$afterOrigin COLLATE BINARY OR origin_machine=$afterOrigin AND id COLLATE BINARY>$afterId COLLATE BINARY)))
        """;
}
