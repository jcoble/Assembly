using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Assembly.Engine.Workflows.Receipts;

namespace Assembly.Tests.Engine.Workflows.Receipts;

public sealed class WorkflowReceiptTests
{
    private const string Head = "0123456789abcdef0123456789abcdef01234567";
    private const string Document = "https://app.notion.com/p/40a394b0689d81299136fb94fcf3ccf1";
    private const string Edge = """{"id":"e","text":"Restart","edge":true,"restart":true,"proved_by":"app"}""";

    private static JsonObject Example(string kind) => JsonNode.Parse(kind switch
    {
        "build" => """{"commits":[],"tests_added":[],"summary":"No code change"}""",
        "recon" => """{"summary":"Inspected","existing":["src/a.cs:3"],"touches":[]}""",
        "spec" => $$"""{"url":"{{Document}}","summary":"Required behavior","requirements":["r"],"acceptance":[{{Edge}}],"reuse":[],"budget":"small","assumptions":[]}""",
        "plan" => $$"""{"url":"{{Document}}","slices":[{"number":1,"title":"Change","needs":[],"budget":"small","files":["src/a.cs"],"acceptance":[{"id":"e","text":"Error","edge":true,"fixture":"failure-case","proved_by":"test:tests/a.cs"}]}]}""",
        "review" => $$"""{"verdict":"fail","findings":[{"severity":"blocking","late":false,"path_line":"src/a.cs:3","text":"Race","fix":"Fence it","grades_of_earlier":[]}],"accepted":[],"new_names":[],"head":"{{Head}}"}""",
        "proof" => $$"""{"summary":"Verified","files":["proof.png"],"proofUrls":["https://evidence.example/proof"],"head":"{{Head}}","checks":[{"name":"native","result":"passed","receiptUrl":"https://evidence.example/receipt"}]}""",
        _ => throw new ArgumentException(kind)
    })!.AsObject();
    private static JsonElement Value(JsonNode value) => JsonSerializer.SerializeToElement(value);
    private static string? Shape(string kind, JsonNode value) => ReceiptValidation.Shape(kind, Value(value));

    [Fact]
    public void Duplicate_members_cannot_silently_overwrite_a_receipt_field()
    {
        using var json = JsonDocument.Parse("""{"commits":[],"tests_added":[],"summary":"one","summary":"two"}""");
        Assert.Equal("summary", ReceiptValidation.Shape("build", json.RootElement));
    }

    [Fact]
    public void Allowed_large_private_summary_is_not_limited_by_the_command_journal_envelope()
    {
        var json = Example("build"); var summary = new string('x', 40_000) + "終"; json["summary"] = summary;
        Assert.Null(Shape("build", json));
        Assert.Equal(summary, JsonSerializer.Deserialize(Value(json), ReceiptJson.Default.BuildReceipt)!.Summary);
    }
}
