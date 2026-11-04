using System.Text.Json;
using Assembly.Application.Common.Acp;

namespace Assembly.Tests.Acp;

public sealed class ConfigOptionIdTests
{
    private const string Choices = "\"category\":\"model\",\"type\":\"select\",\"currentValue\":\"a\",\"options\":[{\"value\":\"a\",\"name\":\"A\"}]";

    [Theory]
    [InlineData("configId")] // What ACP 2 adapters send.
    [InlineData("id")] // What sessions saved before them hold.
    public void An_option_is_found_by_the_id_it_arrived_with(string field)
    {
        using var options = JsonDocument.Parse("[{\"" + field + "\":\"model\"," + Choices + "}]");

        Assert.Equal("model", Assert.Single(ConfigOptions.Selects(options.RootElement)).Id);
        Assert.Equal("model", ConfigOptions.Model(options.RootElement)?.Id);
    }
}
