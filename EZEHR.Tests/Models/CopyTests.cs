using EZEHR.Models;

namespace EZEHR.Tests.Models;

public class CopyTests
{
    [Fact]
    public void Exact_spec_strings()
    {
        Assert.Equal("Signed by Lakeside Urgent Care ✓", Copy.SignedBy("Lakeside Urgent Care"));
        Assert.Equal(
            "Give Dr. Rivera at Riverside Primary Care access until 5:00 PM?",
            Copy.ApprovePrompt("Dr. Rivera", "Riverside Primary Care", "5:00 PM")
        );
        Assert.Equal(
            "Blocked: unverified requester claimed treatment",
            Copy.BrokerBlocked("treatment")
        );
        Assert.Equal(
            "Written by Dr. Rivera, Riverside Primary Care · signed by EZEHR ✓",
            Copy.WrittenBy("Dr. Rivera", "Riverside Primary Care")
        );
        Assert.Equal(
            "Kidney function (eGFR) dropped 34% across 3 providers. No single result was flagged. Consider asking a doctor to review it.",
            Copy.KidneyNote(34, 3)
        );
        Assert.Equal(
            "Kidney Check · lab results · you allowed this Nov 13 · Revoke",
            Copy.AppGrantLine("Kidney Check", "lab results", "Nov 13")
        );
        Assert.Equal(
            "Your access to this record has ended. Ask the patient to share again.",
            Copy.AccessEnded
        );
    }
}
