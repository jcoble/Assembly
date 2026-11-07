using Assembly.Application.Features.Chat.SourceControl.PullRequests;
namespace Assembly.Tests.Draft.SourceControl;

public sealed class GitHubTextTests
{
    [Fact]
    public void A_bots_html_reads_as_markdown_and_code_blocks_stay_as_written()
    {
        var body = "<!-- codex-review-state:v1 {\"sha\":\"6bd\"} -->\n<details> <summary><strong>ℹ About</strong> Codex</summary>\n<br/>\n"
            + "Done <relative-time datetime=\"2026\">today</relative-time>, see <a href=\"https://x.test/r\">the run</a>.\n</details>\n"
            + "```html\n<details>kept</details>\n```";

        Assert.Equal("**ℹ About Codex**\n\nDone today, see [the run](https://x.test/r).\n\n```html\n<details>kept</details>\n```", GitHubText.Tidy(body));
    }
}
