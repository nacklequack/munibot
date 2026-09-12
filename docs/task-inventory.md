# Rezzed task inventory transport

These endpoints support Munibase distributor replenishment. Munibot accepts an
exact target and content; repository validation and bundle routing belong to
Munibase. Production acceptance requires the real simulator checks below.

Both endpoints require a configured token with `sl.inventory.task.write` (or the
existing administrator wildcard). Unlike ordinary local development APIs, task
inventory does not allow anonymous access when the token list is empty.

```
POST /api/objects/{objectUuid}/inventory/inspect
PUT  /api/objects/{objectUuid}/inventory/items/{inventoryName}
```

Inspection accepts `region`, `position: {x,y,z}`, and `names` (1–64 exact names).
It returns `objectUuid` and `items`, including task item and asset IDs, type,
modify/copy/transfer permissions, normalized source SHA-256, and script running
state. A failed inspection is an error, never an empty inventory result.
An empty complete task-inventory snapshot is also unconfirmed; it cannot authorize
item creation. A successful snapshot may return no matches for the requested names
while still containing other inventory, such as the distributor runtime.

A write accepts:

```json
{
  "region": "Example Region",
  "position": { "x": 128, "y": 128, "z": 25 },
  "contentType": "script",
  "sourceDataBase64": "<UTF-8-source-as-base64>",
  "expectedSha256": "<SHA-256-of-normalized-source>",
  "requestId": "<correlation-UUID>"
}
```

`contentType` is `script` or `notecard`. Source is limited to 256 KiB, normalized
to UTF-8 without BOM, LF line endings, and a final newline. Notecard text is
encoded into a Linden notecard asset internally; do not send the Linden wrapper
as source. Names are exact inventory names without path separators.

The bot holds its teleport and inventory locks throughout each operation. It
teleports near the hint, resolves the exact visible UUID, excludes attachments,
and requires object modify permission. Existing item names must be unambiguous
and have the expected type. Existing permissions are preserved; payload scripts
must remain copyable and transferable for downstream distribution.

Existing scripts update in place for Mono with their distributor copies stopped.
Missing items are first uploaded to temporary bot inventory, copied to the object,
and rediscovered by their actual task item ID. Only temporary items created by
that operation are eligible for cleanup. After an interrupted operation, inspect
its outcome before retrying; unconfirmed temporary inventory may need inspection
instead of automatic deletion.

The two CAPS stages are both awaited. Only the final upload/compilation response,
followed by fresh source and running-state readback, can produce `success: true`.
The result separately reports `uploadSucceeded`, `compiled`,
`compilationMessages`, `item`, `errorCode`, `error`, `retryable`, and
`outcomeUnknown`. HTTP 200 can contain a compile failure, so callers must check
the structured result. HTTP 503 with `outcomeUnknown: true` requires inspection
before retry; it never authorizes creating another same-name item.

An explicit simulator source-permission rejection returns HTTP 422 with
`errorCode: "source_permission_denied"` and `retryable: false`. Retrying the same
permission arrangement is not a connectivity repair. If upload completed before
readback failed, `outcomeUnknown` is true: an item may already have been created or
updated even though its source could not be verified. Do not delete or create a
replacement based on this error.

Source-transfer failures include `sourceDiagnostics` in the authenticated error
response. The existing inspection request can collect these details without an
inventory write. The snapshot contains the bot and active-group IDs, target and
inventory-item IDs, the requested asset ID, item owner/group IDs and group-owned
flag, asset type, five permission masks, and simulator transfer status. Masks are
eight-digit hexadecimal values reported by inventory; the owner mask does not
establish a different avatar's effective access. An all-zero ID records what the
client reported and must not be treated as confirmed ownership or group membership.

Compare the returned identities and masks with the actual item properties before
changing access. Group sharing on the outer object does not establish sharing on
its inventory items. A successful insertion also does not prove readable source.
These diagnostics contain private identifiers; keep responses in private test
evidence and remove identifiers before sharing a report. They exclude source,
compiler output, credentials, and simulator session IDs, and remain excluded from
HTTP request/response body logging.

Set `api.task_inventory_timeout_seconds` (default 120; allowed 30–600), and give
the caller a longer dedicated timeout. Source and compiler responses are excluded
from request diagnostics even when body logging is enabled.

## Disposable-object verification

Provide the scoped token as `MUNIBOT_TASK_TOKEN` through private configuration.
Run PowerShell 7 with `scripts/test-task-inventory.ps1`, supplying `-BotUrl`,
`-ObjectUuid`, `-Region`, `-X`, `-Y`, and `-Z` for the authorized disposable object.
Put an unrelated seed notecard in the disposable object first so its complete
inventory is readable and nonempty. The harness leaves that seed untouched.
The harness refuses existing probe names, then tests creation, same-item update,
stopped state, compilation failure/repair, notecard replacement, and readback.
It leaves its two named probe items for manual inspection and cleanup.

Also test the actual ownership and modify-permission arrangement and interrupted
connectivity. Munibase's canary acceptance separately verifies manifest reload,
GitHub-to-distributor delivery, unchanged downstream objects before rollout, and
operator installation on a door, rental meter, and timeclock. Passing unit tests
or the probe harness alone does not satisfy those downstream gates.
