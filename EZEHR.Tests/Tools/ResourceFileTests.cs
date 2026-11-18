using EZEHR.Tools;
using EZEHR.Tools.Calls;

namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class ResourceFileTests
{
    [Fact]
    public void AcceptsEachShape()
    {
        string single = Write("""{"resourceType":"Observation","id":"obs-1","status":"final"}""");
        string array = Write(
            """[{"resourceType":"Observation","id":"a"},{"resourceType":"Condition","id":"b"}]"""
        );
        string seed = Write(
            """
            {"provider":"lakeside","organization":"Lakeside","visits":[
              {"date":"2024-03-01","resources":[{"resourceType":"Observation","id":"a"}]},
              {"date":"2024-05-01","resources":[{"resourceType":"Observation","id":"b"}]}]}
            """
        );

        IReadOnlyList<ResourceVisit> fromSingle = ResourceFile.Load(single);
        IReadOnlyList<ResourceVisit> fromArray = ResourceFile.Load(array);
        IReadOnlyList<ResourceVisit> fromSeed = ResourceFile.Load(seed);

        ResourceVisit singleVisit = Assert.Single(fromSingle);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), singleVisit.Date);
        Assert.Single(singleVisit.Resources);
        Assert.Equal(2, Assert.Single(fromArray).Resources.Length);
        Assert.Equal(
            [new DateOnly(2024, 3, 1), new DateOnly(2024, 5, 1)],
            fromSeed.Select(visit => visit.Date)
        );
    }

    [Fact]
    public void ResourceWithoutTypeIsRefused()
    {
        string path = Write("""{"id":"obs-1"}""");

        ToolException error = Assert.Throws<ToolException>(() => ResourceFile.Load(path));

        Assert.Contains("resourceType", error.Message);
    }

    private static string Write(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ezehr-resource-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
