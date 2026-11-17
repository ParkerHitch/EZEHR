using System.ComponentModel;
using System.Reflection;
using EZEHR.Tools;
using Spectre.Console.Cli;

namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class HelpTests
{
    public static readonly string[] Groups =
    [
        "env",
        "org",
        "patient",
        "db",
        "certs",
        "platform",
        "provider",
    ];

    public static TheoryData<string[]> LeafCommands
    {
        get
        {
            return new TheoryData<string[]>
            {
                new[] { "env" },
                new[] { "org", "list" },
                new[] { "patient", "find" },
                new[] { "patient", "list" },
                new[] { "patient", "status" },
                new[] { "db", "query" },
                new[] { "certs", "issue" },
                new[] { "certs", "platform" },
                new[] { "certs", "provider" },
                new[] { "certs", "app" },
                new[] { "certs", "broker" },
                new[] { "platform", "register-provider" },
                new[] { "platform", "register-app" },
                new[] { "platform", "onboard-provider" },
                new[] { "platform", "onboard-app" },
                new[] { "provider", "invite" },
                new[] { "provider", "push" },
                new[] { "provider", "launch" },
                new[] { "provider", "audit" },
                new[] { "provider", "clinicians" },
            };
        }
    }

    [Fact]
    public async Task RootHelpListsEveryGroup()
    {
        ToolRun run = await ToolRunner.RunAsync("--help");

        Assert.Equal(0, run.ExitCode);
        foreach (string group in Groups)
        {
            Assert.Contains(group, run.Output);
        }
    }

    [Theory]
    [MemberData(nameof(LeafCommands))]
    public async Task LeafHelpHasDescriptionAndExample(string[] path)
    {
        ToolRun run = await ToolRunner.RunAsync([.. path, "--help"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("EXAMPLES:", run.Output);
    }

    [Fact]
    public void EveryOptionAndArgumentIsDescribed()
    {
        IEnumerable<Type> settingsTypes = typeof(ToolApp)
            .Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CommandSettings).IsAssignableFrom(type));
        List<string> undescribed = [];
        foreach (Type settingsType in settingsTypes)
        {
            foreach (PropertyInfo property in settingsType.GetProperties())
            {
                bool isParameter =
                    property.GetCustomAttribute<CommandArgumentAttribute>() is not null
                    || property.GetCustomAttribute<CommandOptionAttribute>() is not null;
                string? description = property
                    .GetCustomAttribute<DescriptionAttribute>()
                    ?.Description;
                if (isParameter && string.IsNullOrWhiteSpace(description))
                {
                    undescribed.Add($"{settingsType.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(undescribed);
    }

    [Fact]
    public async Task UnknownOptionIsAUsageError()
    {
        ToolRun run = await ToolRunner.RunAsync("env", "--bogus");

        Assert.Equal(2, run.ExitCode);
    }
}
