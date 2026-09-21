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
Script inspection also returns `experienceId`: a UUID when metadata is readable,
the zero UUID only when the simulator explicitly confirms no association, and
`null` when unknown (including unavailable, rejected, or malformed metadata).
Notecards return `null`. No association is inferred from a name, source, or asset.
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

### Required Experience

Script writes may add `expectedExperienceId` with a nonzero UUID. It is optional
and defaults to `null`; omitted requests retain the existing upload behavior and
do not require Experience authority or metadata. Omission does not promise to
preserve an existing association. A zero UUID or an expectation on a notecard is
invalid; this API does not expose clearing an association.

Before inventory work, a required Experience write checks `GetCreatorExperiences`,
`GetMetadata`, and `UpdateScriptTask` capabilities and requires the expected UUID
in the bot's creator list. Accepting an Experience in the bot's preferences is
not creator authority. Missing capabilities return `capability_unavailable`;
unreadable authority returns `experience_authority_unconfirmed`; confirmed absence
from the list returns `experience_permission_denied`. These preflight failures
do not mutate inventory.

The final `UpdateScriptTask` sends the expected UUID in LLSD `experience`, with
`target: mono` and `is_script_running: false`. Missing-item creation still compiles
a temporary agent script and copies it stopped, then sets the association on the
rediscovered task item. The agent-stage upload does not send `experience`.

After the final compilation response, fresh source, stopped state, permissions,
asset ID, and exact uploaded item ID must all match. An independent, uncached
`GetMetadata` POST supplies `object-id`, `item-id`, and `fields: ["experience"]`.
`success: true` additionally requires its UUID to equal `expectedExperienceId`.
Unknown metadata returns `experience_unconfirmed` (retryable); a confirmed zero
or different association returns `experience_mismatch` (not retryable). Both are
unsuccessful write results with `uploadSucceeded: true` and `outcomeUnknown: true`:
inspect the existing item before any retry, never create a replacement on that
basis. Metadata reads have a five-second bound inside the overall operation budget.

Wire semantics are based on the official viewer's
[task-script upload body](https://github.com/secondlife/viewer/blob/0a60806f8973c05b68cf65a5a01c41d81ff2da7b/indra/newview/llviewerassetupload.cpp#L833),
[association metadata request](https://github.com/secondlife/viewer/blob/0a60806f8973c05b68cf65a5a01c41d81ff2da7b/indra/llmessage/llexperiencecache.cpp#L571),
and [creator list request](https://github.com/secondlife/viewer/blob/0a60806f8973c05b68cf65a5a01c41d81ff2da7b/indra/newview/llpreviewscript.cpp#L1575).
LibreMetaverse 2.6.7 exposes the capabilities/HTTP client, but its
[script update wrapper](https://github.com/cinderblocks/libremetaverse/blob/v2.6.7/LibreMetaverse/Inventory/InventoryManager.Task.cs#L124)
has no Experience parameter. Munibot uses direct awaited CAPS requests. Source
inspection and automated tests establish this protocol implementation, not actual
simulator acceptance or downstream `llRemoteLoadScriptPin` behavior.

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

Unanswered inventory-list, source, object-properties, and script-running-state
reads use up to three 15-second attempts within that overall operation budget.
The error identifies the stalled stage (`inventory_list_timeout`,
`source_read_timeout`, `object_properties_timeout`, or `script_state_timeout`).
Permission failures are returned immediately. These retries only repeat reads;
creation, copying, and uploads are never replayed by this policy. A read failure
after a copy or upload still reports an unknown outcome and requires inspection
before another write. An empty or cancelled inventory response never authorizes
creating another item.

## Disposable-object verification

Provide the scoped token as `MUNIBOT_TASK_TOKEN` through private configuration.
Run PowerShell 7 with `scripts/test-task-inventory.ps1`, supplying `-BotUrl`,
`-ObjectUuid`, `-Region`, `-X`, `-Y`, and `-Z` for the authorized disposable object.
Put an unrelated seed notecard in the disposable object first so its complete
inventory is readable and nonempty. The harness leaves that seed untouched.
The harness refuses existing probe names, then tests creation, same-item update,
stopped state, compilation failure/repair, notecard replacement, and readback.
It leaves its two named probe items for manual inspection and cleanup.

Pass `-ExpectedExperienceId` to additionally test required Experience on creation,
same-item update, compile failure/repair, and an independent final inspection.
Keep target IDs, Experience IDs, credentials, source responses, and configuration
private; publish only sanitized outcomes and the deployed commit/image identity.
The harness uses disposable probe source, not the scanner pair, and leaves scripts
stopped. It does not prove scanner attachment behavior.

For Munibase #1097, deploy the reviewed Munibot image with these contract fields,
confirm the bot has creator authority for the required Experience, then run the
harness against an explicitly identified disposable object containing a seed
notecard. After that, the #1097 owner must prove both actual scanner scripts on
a disposable scanner through distributor stock and managed target update, verify
their installed Experience and versions, retain the local HUD object/permissions
and unrelated inventory, and observe HUD detection/attachment recovery. Keep
catalog enrollment and production rollout disabled until those gates pass. An
unmerged PR or green tests cannot close this in-world gate.

Also test the actual ownership and modify-permission arrangement and interrupted
connectivity. Munibase's canary acceptance separately verifies manifest reload,
GitHub-to-distributor delivery, unchanged downstream objects before rollout, and
operator installation on a door, rental meter, and timeclock. Passing unit tests
or the probe harness alone does not satisfy those downstream gates.
