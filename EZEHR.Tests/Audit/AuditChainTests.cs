using Dapper;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Audit;

/// <summary>The audit log's hash chain: edits, deletions and insertions made behind the app's back are found.</summary>
public class AuditChainTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    private static readonly Party Rivera = new(
        PartyKind.Clinician,
        "urn:entra:tid:rivera",
        "Dr. Rivera",
        "Riverside Primary Care",
        "urn:provider:riverside"
    );

    private readonly FakeTimeProvider _clock = new(Start);
    private Guid _sam;
    private Guid _alex;
    private TestDatabase _database = null!;
    private TestServices _services = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
        _services = new TestServices(_database.Db, _clock);
        _sam = await _services.Records.CreatePatientAsync("Sam Taylor");
        _alex = await _services.Records.CreatePatientAsync("Alex Kim");
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private async Task<AuditEvent> LogAsync(Guid patientId, bool granted = true)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        return (
            await _services.Audit.AppendAsync(
                patientId,
                granted ? Rivera : null,
                "ReadFullRecord",
                granted ? null : "treatment",
                granted,
                granted ? null : DenyReason.NoCertificate
            )
        )!;
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(sql, parameters);
    }

    [Fact]
    public async Task An_empty_log_is_intact_with_no_head()
    {
        AuditChainCheck check = await _services.Audit.VerifyChainAsync(_sam);

        Assert.Equal(new AuditChainCheck(true, 0, 0, null, null), check);
    }

    [Fact]
    public async Task Each_event_is_chained_to_the_patients_previous_one()
    {
        AuditEvent first = await LogAsync(_sam);
        AuditEvent other = await LogAsync(_alex);
        AuditEvent second = await LogAsync(_sam, granted: false);

        AuditChainCheck check = await _services.Audit.VerifyChainAsync(_sam);

        Assert.Equal(64, first.ChainHash!.Length);
        Assert.NotEqual(first.ChainHash, second.ChainHash);
        Assert.Equal(
            AuditChain.ComputeHash(
                first.ChainHash,
                _sam,
                second.At,
                null,
                "ReadFullRecord",
                "treatment",
                false,
                DenyReason.NoCertificate
            ),
            second.ChainHash
        );
        Assert.Equal(new AuditChainCheck(true, 2, 0, second.ChainHash, null), check);
        // Another patient's events are a separate chain.
        Assert.Equal(other.ChainHash, (await _services.Audit.VerifyChainAsync(_alex)).HeadHash);
    }

    [Fact]
    public async Task An_edited_event_breaks_the_chain_at_that_event()
    {
        await LogAsync(_sam);
        AuditEvent denied = await LogAsync(_sam, granted: false);
        await LogAsync(_sam);

        await ExecuteAsync(
            "UPDATE audit_events SET granted = 1, reason = NULL WHERE id = @Id",
            new { denied.Id }
        );
        AuditChainCheck check = await _services.Audit.VerifyChainAsync(_sam);

        Assert.False(check.Intact);
        Assert.Equal(denied.Id, check.FirstBrokenId);
    }

    [Fact]
    public async Task A_deleted_event_breaks_the_chain_at_the_next_one()
    {
        AuditEvent first = await LogAsync(_sam);
        AuditEvent second = await LogAsync(_sam);
        AuditEvent third = await LogAsync(_sam);

        await ExecuteAsync("DELETE FROM audit_events WHERE id = @Id", new { second.Id });
        AuditChainCheck middleGone = await _services.Audit.VerifyChainAsync(_sam);
        await ExecuteAsync("DELETE FROM audit_events WHERE id = @Id", new { first.Id });
        AuditChainCheck firstGoneToo = await _services.Audit.VerifyChainAsync(_sam);

        Assert.False(middleGone.Intact);
        Assert.Equal(third.Id, middleGone.FirstBrokenId);
        Assert.False(firstGoneToo.Intact);
    }

    [Fact]
    public async Task An_event_inserted_without_a_fingerprint_breaks_the_chain()
    {
        await LogAsync(_sam);

        await ExecuteAsync(
            """
            INSERT INTO audit_events(patient_id, at, action, granted)
            VALUES (@patientId, SYSDATETIMEOFFSET(), 'ReadFullRecord', 1)
            """,
            new { patientId = _sam }
        );

        Assert.False((await _services.Audit.VerifyChainAsync(_sam)).Intact);
    }

    [Fact]
    public async Task Events_from_before_fingerprints_are_counted_and_the_chain_starts_after_them()
    {
        await ExecuteAsync(
            """
            INSERT INTO audit_events(patient_id, at, action, granted)
            VALUES (@patientId, @at, 'ReadFullRecord', 1), (@patientId, @at, 'ReadFullRecord', 1)
            """,
            new { patientId = _sam, at = Start }
        );
        AuditEvent chained = await LogAsync(_sam);

        AuditChainCheck check = await _services.Audit.VerifyChainAsync(_sam);

        Assert.Equal(new AuditChainCheck(true, 3, 2, chained.ChainHash, null), check);
    }

    [Fact]
    public async Task Appends_at_the_same_moment_still_form_one_chain()
    {
        await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(_ =>
                    _services.Audit.AppendAsync(_sam, Rivera, "ReadFullRecord", null, true, null)
                )
        );

        AuditChainCheck check = await _services.Audit.VerifyChainAsync(_sam);

        Assert.True(check.Intact);
        Assert.Equal(20, check.EventCount);
    }

    [Fact]
    public async Task An_event_for_an_unknown_patient_is_still_not_stored()
    {
        AuditEvent? stored = await _services.Audit.AppendAsync(
            Guid.NewGuid(),
            Rivera,
            "ReadFullRecord",
            null,
            true,
            null
        );

        Assert.Null(stored);
    }

    [Fact]
    public async Task Activity_says_the_log_is_verified_and_warns_when_it_was_changed()
    {
        ActivityManager activityManager = new(_services.Audit, Options.Create(new AppOptions()));
        Assert.Null((await activityManager.ListActivityAsync(_sam)).ChainStatus);
        await LogAsync(_sam);
        AuditEvent newest = await LogAsync(_sam);

        PatientActivity verified = await activityManager.ListActivityAsync(_sam);
        await ExecuteAsync(
            "UPDATE audit_events SET actor_name = N'Somebody Else' WHERE id = @Id",
            new { newest.Id }
        );
        PatientActivity changed = await activityManager.ListActivityAsync(_sam);

        Assert.True(verified.ChainIntact);
        Assert.Equal(
            $"Log verified ✓ · 2 events · fingerprint {AuditChain.Fingerprint(newest.ChainHash!)}",
            verified.ChainStatus
        );
        Assert.Matches("^[0-9a-f]{4}( [0-9a-f]{4}){3}$", AuditChain.Fingerprint(newest.ChainHash!));
        Assert.False(changed.ChainIntact);
        Assert.Equal(Copy.AuditLogChanged, changed.ChainStatus);
        Assert.Equal(2, changed.Lines.Count);
    }
}
