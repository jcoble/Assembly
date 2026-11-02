namespace Assembly.Engine.Mcp.Tools;

/// <summary>The definition selects this union's expected member; receipt bodies never supply identity or a kind override.</summary>
internal static class WorkflowReceiptSchema
{
    private const string Text = """{"type":"string","minLength":1}""";
    private const string Strings = """{"type":"array","items":{"type":"string","minLength":1}}""";
    private const string Boolean = """{"type":"boolean"}""";
    private const string Head = """{"type":"string","pattern":"^([a-fA-F0-9]{40}|[a-fA-F0-9]{64})$"}""";
    private const string Url = """{"type":"string","format":"uri","pattern":"^https://"}""";
    private const string Number = """{"type":"integer","minimum":0,"maximum":4294967295}""";
    private static string Array(string item) => "{\"type\":\"array\",\"items\":" + item + "}";
    private static string Property(string name, string schema) => "\"" + name + "\":" + schema;
    private static string Object(string required, params string[] properties) =>
        "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[" + required + "],\"properties\":{" + string.Join(',', properties) + "}}";
    private static readonly string Acceptance = Array(Object("\"id\",\"text\",\"proved_by\"",
        Property("id", Text), Property("text", Text), Property("edge", Boolean),
        Property("fixture", """{"type":["string","null"]}"""), Property("restart", """{"type":["boolean","null"]}"""),
        Property("proved_by", """{"type":"string","pattern":"^(app|owner-check|test:.+)$"}""")));
    private static readonly string Finding = Object("\"severity\",\"late\",\"path_line\",\"text\",\"fix\"",
        Property("severity", """{"type":"string","enum":["blocking","minor"]}"""), Property("late", Boolean),
        Property("why_late", """{"type":["string","null"]}"""), Property("path_line", Text), Property("text", Text), Property("fix", Text),
        Property("grades_of_earlier", Array(Object("\"text\",\"grade\"", Property("text", Text),
            Property("grade", """{"type":"string","enum":["fixed","open"]}""")))));

    internal static readonly string Json = "{\"oneOf\":[" + string.Join(',',
        Object("\"commits\",\"tests_added\",\"summary\"", Property("commits", Strings), Property("tests_added", Strings), Property("summary", Text)),
        Object("\"summary\",\"touches\"", Property("summary", Text), Property("existing", Strings),
            Property("none_found_because", """{"type":["string","null"]}"""), Property("touches", Strings)),
        Object("\"url\",\"summary\",\"requirements\",\"acceptance\",\"reuse\",\"budget\",\"assumptions\"",
            Property("url", Url), Property("summary", Text), Property("requirements", Strings), Property("acceptance", Acceptance),
            Property("reuse", Strings), Property("budget", Text), Property("assumptions", Strings)),
        Object("\"url\",\"slices\"", Property("url", Url), Property("slices", Array(Object(
            "\"number\",\"title\",\"needs\",\"budget\",\"files\",\"acceptance\"", Property("number", Number), Property("title", Text),
            Property("needs", Array(Number)), Property("budget", Text), Property("files", Strings), Property("acceptance", Acceptance))))),
        Object("\"verdict\",\"findings\",\"head\"", Property("verdict", """{"type":"string","enum":["pass","fail"]}"""),
            Property("findings", Array(Finding)), Property("head", Head), Property("accepted", Strings),
            Property("new_names", Array(Object("\"name\",\"verdict\"", Property("name", Text), Property("verdict", """{"type":"string","enum":["pass","fail"]}"""))))),
        Object("\"summary\",\"files\",\"proofUrls\",\"head\",\"checks\"", Property("summary", Text), Property("files", Strings),
            Property("proofUrls", Array(Url)), Property("head", Head), Property("checks", Array(Object("\"name\",\"result\",\"receiptUrl\"",
                Property("name", Text), Property("result", Text), Property("receiptUrl", Url)))))) + "]}";
}
