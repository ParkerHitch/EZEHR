# Tamper-evident audit log

## Why

"The patient sees every access attempt" is the promise the whole design rests on, and the spec's honest limits
admit an insider could go around the access check. The audit log was a plain table: anyone with database access
could change a denied read to granted, or delete a line, and nothing would show. The spec listed a hash-chained log
as a nice-to-have (section 11). This builds it.

## Goals

- Changing, deleting or inserting an audit event behind the app's back is detected.
- The patient is told, on the Activity page, whether their log still checks out.
- No new key or service to operate.

## Non-goals

- Stopping someone who can rewrite the whole chain (see Honest limits).
- Signing the log, or anchoring it outside the database.
- Chaining across patients, or a chain for the provider's organization audit view.

## Approaches considered

1. **A SHA-256 hash chain per patient (chosen).** Each event stores a fingerprint of its own contents plus the
   previous event's fingerprint. Nothing to operate, verifiable by anyone with the rows, and one patient's chain
   never depends on another's.
2. **An HMAC chain, keyed from Key Vault.** A database administrator couldn't recompute it. Rejected for now: it
   needs a sign operation on `IKeyWrapService` (which only wraps and unwraps), a Key Vault call per audit event, and
   it makes the log unverifiable outside the app.
3. **One global chain.** Simpler ordering, but every append in the system would queue behind one lock, and a
   patient couldn't check their own log without everyone else's rows.
4. **SQL Server ledger tables.** Built for exactly this, but it is an Azure SQL feature to configure rather than
   something the app shows, and the local SQL Server container would behave differently.

## Design

- `audit_events.chain_hash CHAR(64)`: lowercase hex SHA-256 over a JSON array of the previous event's hash (null for
  the patient's first), patient id, time in UTC ticks, the actor's kind, URI, name, organization and organization
  URI, the action, claimed purpose, granted and reason. `AuditChain.ComputeHash` is the one place that builds it.
- **Appending.** `AuditManager.AppendAsync` takes an update lock on the patient's row, reads the patient's newest
  hash, computes the new one and inserts, all in one transaction (the caller's, or its own). The lock is what makes
  two appends at the same moment form one chain. It also replaces the old `WHERE EXISTS` check for unknown patients.
- **Verifying.** `AuditChain.Verify` recomputes the chain oldest first and reports the first event that doesn't
  match. Events written before this change have no hash; they may only come first, and are counted as not covered.
- **Showing it.** The Activity page reads the whole log anyway, so each load verifies it: "Log verified ✓ · 12
  events · fingerprint 3f9a 21bc 77d0 e1a4", or a red warning. `ezehr-tools demo status` prints the same check.

## Honest limits

- **It is evidence, not prevention.** Someone who can write to the database and knows the scheme can change an
  event and recompute every fingerprint after it. What they can't do is make the head fingerprint come out the
  same, so a patient (or anyone) who noted the fingerprint earlier can tell. Nothing makes a patient note it.
- **Removing the newest events is not detected** by the chain alone: what's left is still a valid chain, with an
  older head. Again, only a remembered fingerprint or count shows it.
- The stronger versions are approach 2 (a key the database doesn't hold) or publishing each head somewhere the
  database owner can't rewrite.
