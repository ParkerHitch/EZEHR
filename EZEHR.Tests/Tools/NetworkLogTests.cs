using System.Net;
using EZEHR.Tools.Net;
using Spectre.Console;
using Spectre.Console.Testing;

namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class NetworkLogTests
{
    [Fact]
    public async Task BodyWithBracketsPrintsLiterally()
    {
        TestConsole console = new();
        console.Profile.Width = 200;
        IAnsiConsole previous = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            using HttpResponseMessage response = new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"errors\":[\"[red]x\"]}"),
            };

            await NetworkLog.FailedAsync(response);
        }
        finally
        {
            AnsiConsole.Console = previous;
        }

        Assert.Contains("{\"errors\":[\"[red]x\"]}", console.Output);
    }
}
