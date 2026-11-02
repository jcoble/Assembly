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

    [Theory]
    [InlineData("build")]
    [InlineData("recon")]
    [InlineData("spec")]
    [InlineData("plan")]
    [InlineData("review")]
    [InlineData("proof")]
    public void Complete_R2_specimens_are_shape_valid_without_claiming_external_verification(string kind)
    {
        var json = Example(kind); var before = json.ToJsonString();
        Assert.Null(Shape(kind, json)); Assert.Equal(before, json.ToJsonString());
        JsonTypeInfo metadata = kind switch
        {
            "build" => ReceiptJson.Default.BuildReceipt,
            "recon" => ReceiptJson.Default.ReconReceipt,
            "spec" => ReceiptJson.Default.SpecReceipt,
            "plan" => ReceiptJson.Default.PlanReceipt,
            "review" => ReceiptJson.Default.ReviewReceipt,
            _ => ReceiptJson.Default.ProofReceipt
        };
        var typed = Assert.IsAssignableFrom<WorkflowReceipt>(JsonSerializer.Deserialize(Value(json), metadata));
        Assert.Null(ReceiptValidation.Shape(kind, JsonSerializer.SerializeToElement(typed, metadata)));
    }

    [Theory]
    [InlineData("build", "commits[0]")]
    [InlineData("recon", "none_found_because")]
    [InlineData("spec", "acceptance[0]")]
    [InlineData("plan", "slices.needs")]
    [InlineData("review", "findings[0].why_late")]
    [InlineData("proof", "files")]
    public void Each_union_kind_reports_the_actual_failed_shape_field(string kind, string field)
    {
        var json = Example(kind);
        switch (kind)
        {
            case "build": json["commits"] = new JsonArray(3); break;
            case "recon": json["existing"] = new JsonArray(); break;
            case "spec": json["acceptance"]![0]!["restart"] = null; break;
            case "plan": json["slices"]![0]!["needs"] = new JsonArray(99); break;
            case "review": json["findings"]![0]!["late"] = true; break;
            case "proof": json.Remove("files"); break;
        }
        Assert.Equal(field, Shape(kind, json));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Review_verdict_requires_zero_findings_for_pass_and_actionable_findings_for_fail(bool pass)
    {
        var json = Example("review");
        if (pass) json["verdict"] = "pass"; else json["findings"] = new JsonArray();
        Assert.Equal("verdict", Shape("review", json));
    }

    [Fact]
    public void A_real_actionable_finding_requires_location_text_and_fix()
    {
        foreach (var field in new[] { "path_line", "text", "fix" })
        {
            var json = Example("review"); json["findings"]![0]![field] = " ";
            Assert.Equal("findings[0]." + field, Shape("review", json));
        }
    }

    [Theory]
    [InlineData("severity")]
    [InlineData("verdict")]
    [InlineData("named")]
    [InlineData("grade")]
    public void Unrecognized_review_enums_are_field_errors(string field)
    {
        var json = Example("review"); var finding = json["findings"]![0]!;
        switch (field)
        {
            case "severity": finding["severity"] = "critical"; break;
            case "verdict": json["verdict"] = "approved"; break;
            case "named": json["new_names"] = JsonNode.Parse("""[{"name":"stage","verdict":"approved"}]"""); break;
            default: finding["grades_of_earlier"] = JsonNode.Parse("""[{"text":"Earlier race","grade":"done"}]"""); break;
        }
        Assert.NotNull(Shape("review", json));
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("open")]
    public void Duplicate_or_contradictory_earlier_grades_are_rejected(string second)
    {
        var json = Example("review");
        json["findings"]![0]!["grades_of_earlier"] = JsonNode.Parse($$"""[{"text":"Earlier race","grade":"fixed"},{"text":"Earlier race","grade":"{{second}}"}]""");
        Assert.Equal("findings[0].grades_of_earlier[1]", Shape("review", json));
    }

    [Fact]
    public void Omitted_old_optional_arrays_have_real_typed_empty_defaults()
    {
        var json = Example("review"); json.Remove("accepted"); json.Remove("new_names"); json["findings"]![0]!.AsObject().Remove("grades_of_earlier");
        Assert.Null(Shape("review", json));
        var review = JsonSerializer.Deserialize(Value(json), ReceiptJson.Default.ReviewReceipt)!;
        Assert.Empty(review.Accepted); Assert.Empty(review.NewNames); Assert.Empty(review.Findings[0].GradesOfEarlier);
        var recon = Example("recon"); recon.Remove("existing"); recon["none_found_because"] = "The inspected feature is absent.";
        Assert.Null(Shape("recon", recon));
        Assert.Empty(JsonSerializer.Deserialize(Value(recon), ReceiptJson.Default.ReconReceipt)!.Existing);
    }

    [Fact]
    public void Nullable_optional_fields_and_omitted_edge_keep_their_actual_types()
    {
        var json = Example("spec"); json["acceptance"]![0]!["fixture"] = null;
        json["acceptance"]!.AsArray().Add(JsonNode.Parse("""{"id":"normal","text":"Normal path","proved_by":"owner-check"}"""));
        Assert.Null(Shape("spec", json));
        var spec = JsonSerializer.Deserialize(Value(json), ReceiptJson.Default.SpecReceipt)!;
        Assert.Null(spec.Acceptance[0].Fixture); Assert.True(spec.Acceptance[0].Restart); Assert.False(spec.Acceptance[1].Edge);
        Assert.Null(spec.Acceptance[1].Restart); Assert.Null(spec.Acceptance[1].Fixture);
    }

    [Theory]
    [InlineData("findings")]
    [InlineData("accepted")]
    [InlineData("grades_of_earlier")]
    public void Explicit_null_is_not_an_omitted_typed_array(string field)
    {
        var json = Example("review");
        if (field == "grades_of_earlier") json["findings"]![0]![field] = null; else json[field] = null;
        Assert.Contains(field, Shape("review", json)!);
    }

    [Fact]
    public void Identity_fields_are_absent_from_the_receipt_contract()
    {
        var json = Example("build"); json["sessionId"] = "someone-else";
        Assert.Equal("sessionId", Shape("build", json));
    }

    [Fact]
    public void Duplicate_members_cannot_silently_overwrite_a_receipt_field()
    {
        using var json = JsonDocument.Parse("""{"commits":[],"tests_added":[],"summary":"one","summary":"two"}""");
        Assert.Equal("summary", ReceiptValidation.Shape("build", json.RootElement));
    }

    [Fact]
    public void Complete_review_fields_roundtrip_through_real_generated_metadata_without_flattening()
    {
        var original = new ReviewReceipt("fail", [new("blocking", true, "Observed during restart", "src/a.cs:3", "Race α", "Fence it",
            [new("Earlier finding", "open")])], ["Accepted explanation"], [new("named stage", "pass")], Head);
        var wire = JsonSerializer.SerializeToElement(original, ReceiptJson.Default.ReviewReceipt);
        Assert.Null(ReceiptValidation.Shape("review", wire));
        Assert.Equal("src/a.cs:3", wire.GetProperty("findings")[0].GetProperty("path_line").GetString());
        Assert.Equal("open", wire.GetProperty("findings")[0].GetProperty("grades_of_earlier")[0].GetProperty("grade").GetString());
        var read = JsonSerializer.Deserialize(wire, ReceiptJson.Default.ReviewReceipt)!;
        Assert.Equal(original.Accepted, read.Accepted); Assert.Equal(original.NewNames, read.NewNames);
        Assert.Equal(original.Findings[0].GradesOfEarlier, read.Findings[0].GradesOfEarlier);
        Assert.Equal(original.Findings[0].WhyLate, read.Findings[0].WhyLate); Assert.Equal(original.Findings[0].Text, read.Findings[0].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_numbers_are_unique_and_dependency_graph_is_acyclic(bool duplicate)
    {
        var json = Example("plan"); var second = json["slices"]![0]!.DeepClone();
        if (!duplicate) { second["number"] = 2; second["needs"] = new JsonArray(1); json["slices"]![0]!["needs"] = new JsonArray(2); }
        json["slices"]!.AsArray().Add(second);
        Assert.Equal(duplicate ? "slices[1].number" : "slices.needs", Shape("plan", json));
    }

    [Fact]
    public void Acceptance_proof_names_are_the_existing_app_test_or_owner_contract()
    {
        var json = Example("spec"); json["acceptance"]![0]!["proved_by"] = "an-agent-said-so";
        Assert.Equal("acceptance[0].proved_by", Shape("spec", json));
    }

    [Fact]
    public void Allowed_large_private_summary_is_not_limited_by_the_command_journal_envelope()
    {
        var json = Example("build"); var summary = new string('x', 40_000) + "終"; json["summary"] = summary;
        Assert.Null(Shape("build", json));
        Assert.Equal(summary, JsonSerializer.Deserialize(Value(json), ReceiptJson.Default.BuildReceipt)!.Summary);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("review")]
    [InlineData("proof")]
    public void Contextual_R2_rejections_do_not_masquerade_as_pure_shape_verification(string kind)
    {
        var json = Example(kind);
        if (kind == "build") json["commits"] = new JsonArray(new string('b', 40));
        if (kind == "review") json["head"] = new string('b', 64);
        if (kind == "proof") json["files"] = new JsonArray("not-an-existing-managed-proof.png");
        Assert.Null(Shape(kind, json)); // No ancestry, frozen-head, filesystem or network authority exists in this pure slice.
    }
}
