using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;

namespace EZEHR.Tests.Services;

public class TimelineBuilderTests
{
    private static readonly Party Lakeside = OrgDirectory.Demo.FindByUri("urn:provider:lakeside")!;
    private static readonly Party Riverside = OrgDirectory.Demo.FindByUri(
        "urn:provider:riverside"
    )!;
    private static readonly Party KidneyCheck = OrgDirectory.Demo.FindByUri(
        "urn:app:kidney-check"
    )!;

    // Records are signed when pushed, long after the visit.
    private static readonly DateTimeOffset PushedAt = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private static SignedEntry Entry(Party signer, object resource, string? writtenBy = null)
    {
        JsonElement json = JsonSerializer.SerializeToElement(resource);
        return new SignedEntry(
            Guid.NewGuid(),
            json.GetProperty("resourceType").GetString()!,
            json,
            signer,
            PushedAt,
            writtenBy,
            "jws"
        );
    }

    [Fact]
    public void Groups_by_clinical_date_newest_first_not_signed_at()
    {
        Timeline timeline = TimelineBuilder.Build([
            Entry(
                Lakeside,
                new
                {
                    resourceType = "Observation",
                    effectiveDateTime = "2025-08-21",
                    code = new { text = "eGFR" },
                    valueQuantity = new { value = 81, unit = "mL/min/1.73m2" },
                }
            ),
            Entry(
                Lakeside,
                new
                {
                    resourceType = "Encounter",
                    period = new { start = "2026-09-15" },
                    type = new[] { new { text = "Back pain follow-up" } },
                }
            ),
        ]);

        Assert.Equal(
            [new DateOnly(2026, 9, 15), new DateOnly(2025, 8, 21)],
            timeline.Days.Select(day => day.Date)
        );
        TimelineItem lab = timeline.Days[1].Items[0];
        Assert.Equal("eGFR", lab.Title);
        Assert.Equal("81 mL/min/1.73m2", lab.Detail);
        Assert.Equal("Signed by Lakeside Urgent Care ✓", lab.Badge);
    }

    [Fact]
    public void Blood_pressure_shows_both_components()
    {
        Timeline timeline = TimelineBuilder.Build([
            Entry(
                Lakeside,
                new
                {
                    resourceType = "Observation",
                    effectiveDateTime = "2026-03-10",
                    code = new { text = "Blood pressure" },
                    component = new[]
                    {
                        new { valueQuantity = new { value = 146, unit = "mmHg" } },
                        new { valueQuantity = new { value = 92, unit = "mmHg" } },
                    },
                }
            ),
        ]);

        Assert.Equal("146/92 mmHg", timeline.Days[0].Items[0].Detail);
    }

    [Fact]
    public void Authored_note_is_written_by_and_app_note_goes_to_the_top()
    {
        Timeline timeline = TimelineBuilder.Build([
            Entry(
                Riverside,
                new
                {
                    resourceType = "Communication",
                    sent = "2026-11-14",
                    payload = new[] { new { contentString = "Stop regular ibuprofen." } },
                },
                "Dr. Rivera"
            ),
            Entry(
                KidneyCheck,
                new
                {
                    resourceType = "Communication",
                    sent = "2026-11-14",
                    payload = new[] { new { contentString = Copy.KidneyNote(34, 3) } },
                },
                "Kidney Check"
            ),
        ]);

        TimelineItem note = Assert.Single(timeline.AppNotes);
        Assert.Equal(Copy.KidneyNote(34, 3), note.Title);
        Assert.Equal("Signed by Kidney Check ✓", note.Badge);
        TimelineItem plan = Assert.Single(timeline.Days).Items.Single();
        Assert.Equal("Written by Dr. Rivera · signed by Riverside Primary Care ✓", plan.Badge);
    }
}
