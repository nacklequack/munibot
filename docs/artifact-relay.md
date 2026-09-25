# Scanner raw-artifact relay

This transport ferries one exact raw Scanner HUD or Scanner Titler object from the
community drop box, through Munibot agent inventory, into one exact managed regional
scanner. It does not create wrappers, per-region stock, or a general agent-inventory
to object-copy API. Script Experience transport remains separate.

All routes require a configured token with `sl.inventory.artifact.relay`. They remain
closed when no tokens are configured, and request/response bodies are excluded from
API diagnostics because they contain private inventory and object identities.

```
PUT /api/inventory/artifact-offers/{requestId}
GET /api/inventory/artifact-offers/{requestId}
PUT /api/objects/{scannerUuid}/inventory/artifacts/{requestId}
```

## Evidence boundary

Second Life task-inventory offers do not carry the source object UUID or an
application request ID. The offer exposes that it came from a task, the task owner's
UUID, the task name/message, the asset type, and the newly created recipient inventory
item ID. Munibase must authenticate the enrolled community drop box and record its
exact object UUID, request ID, artifact identity, and send attempt before opening the
Munibot gate. `sourceObjectUuid` in the expectation and receipt is that authenticated
command binding; it is not a claim that the grid offer attested the source UUID.

Munibot independently proves the wire-visible owner, source object name, inventory
name, and object type before accepting. After acceptance it fetches the exact new bot
inventory item ID and requires the exact name, object asset/inventory types, asset UUID,
bot ownership, and all five permission masks. Only that completed receipt can be used
by the scanner route.

## Expected offer

Register the gate before commanding the drop box to call `llGiveInventory`:

```json
{
  "sourceObjectUuid": "<authenticated-drop-box-uuid>",
  "sourceOwnerUuid": "<wire-visible-owner-uuid>",
  "sourceObjectName": "Community Delivery Drop Box",
  "inventoryName": "Scanner HUD",
  "assetId": "<raw-object-asset-uuid>",
  "permissions": {
    "base": 2147483647,
    "owner": 2147483647,
    "group": 0,
    "everyone": 0,
    "nextOwner": 532480
  }
}
```

The gate is single-flight, one-shot, and expires after 60 seconds. Repeating the same
request ID and body is idempotent. Reusing it with a different contract fails.
Unsolicited, late, duplicate, agent-originated, wrong-owner, wrong-name, or wrong-type
offers are declined and cannot produce a receipt. An offer-level mismatch leaves the
valid pending expectation available. A post-accept asset or permission mismatch fails
the expectation and leaves the received item untouched in bot inventory for inspection.

Poll the GET route until `status` is `received`, `failed`, or `expired`. A successful
receipt returns the bot item and asset IDs, exact types/name/owner/group, all five integer
permission masks, and copy/modify/transfer convenience booleans. `receiving` means the
offer was accepted but readback is not yet verified; timeout or interruption in that
state has `outcomeUnknown: true`.

Both expectation routes return the same state shape. On success, object type strings are
the case-sensitive LibreMetaverse names `Object`:

```json
{
  "requestId": "<request-uuid>",
  "status": "received",
  "expiresAt": "2026-09-25T12:01:00+00:00",
  "receipt": {
    "requestId": "<request-uuid>",
    "sourceObjectUuid": "<authenticated-drop-box-uuid>",
    "receivedAt": "2026-09-25T12:00:04+00:00",
    "item": {
      "itemId": "<bot-inventory-item-uuid>",
      "assetId": "<raw-object-asset-uuid>",
      "name": "Scanner HUD",
      "assetType": "Object",
      "inventoryType": "Object",
      "ownerId": "<bot-agent-uuid>",
      "groupId": "00000000-0000-0000-0000-000000000000",
      "groupOwned": false,
      "permissions": {
        "base": 2147483647,
        "owner": 2147483647,
        "group": 0,
        "everyone": 0,
        "nextOwner": 581632
      },
      "canModify": true,
      "canCopy": true,
      "canTransfer": true
    }
  },
  "errorCode": null,
  "error": null,
  "retryable": false,
  "outcomeUnknown": false
}
```

## Managed scanner copy

The scanner request contains only target evidence; the source is fixed by the verified
receipt for `{requestId}`:

```json
{
  "region": "Example Region",
  "position": { "x": 128, "y": 128, "z": 25 },
  "targetName": "HUD Scanner",
  "targetOwnerId": "<scanner-owner-uuid>",
  "targetGroupId": "<scanner-group-uuid-or-zero>",
  "expectedBundleKey": "hud-runtime-scanner",
  "bundleMarkers": [
    {
      "name": "<managed-runtime-marker-name>",
      "assetId": "<exact-marker-asset-uuid>",
      "assetType": "LSLText"
    }
  ],
  "expectedTargetPermissions": {
    "base": 2147483647,
    "owner": 532480,
    "group": 0,
    "everyone": 0,
    "nextOwner": 532480
  }
}
```

Munibase supplies the existing stocking landmark's region/position and keeps its
existing distributed bot executor lease for the entire trip. Munibot holds its own
teleport and inventory locks, waits for movement completion, resolves the exact visible
scanner UUID, excludes attachments, and requires object modify access. It then reads
object properties and requires exact target UUID, name, owner, and group plus every
exact case-sensitive bundle marker name/type/asset ID.

The bot receipt must still match the live source item. The source must be an object with
copy and transfer permissions, so delivery cannot move or consume it. Another-owner
managed targets must use the bot's active group and the source must already carry the
required group-sharing mask; this endpoint never rewrites raw-artifact permissions.

Before mutation, an existing exact name is either an exact identity/permission match
(idempotent success) or a conflict. Munibot never removes or replaces a scanner item.
After `UpdateTaskInventory`, it reads the complete task inventory again and returns the
actual task item ID, exact asset/name/type, and all five masks. A failed read after the
copy reports `outcomeUnknown: true`; inspect or retry the same request. An exact prior
copy makes that retry idempotently successful. The bot source and all prior scanner
items remain untouched on every failure path.

The successful scanner response is:

```json
{
  "requestId": "<request-uuid>",
  "objectUuid": "<scanner-uuid>",
  "expectedBundleKey": "hud-runtime-scanner",
  "copied": true,
  "idempotent": false,
  "source": { "itemId": "<bot-item-uuid>", "assetType": "Object", "inventoryType": "Object" },
  "target": { "itemId": "<task-item-uuid>", "assetType": "Object", "inventoryType": "Object" }
}
```

`source` and `target` are full item objects with every field shown in the receipt example;
they are abbreviated above only to keep the example readable. Structured errors use
`errorCode`, `error`, `retryable`, and `outcomeUnknown`. Input validation errors contain
`error`. `bundleMarkers[].assetType` accepts case-insensitive LibreMetaverse asset-type
names such as `LSLText`, `Notecard`, or `Object`; successful raw-artifact item types remain
exactly `Object`.

## Controlled canary

No automated test in this repository performs in-world mutations. After review, merge,
and deployment, the control-plane operator can run one explicit canary:

1. Use a disposable enrolled community drop box and one disposable managed scanner near
   an existing regional stocking landmark. Record the deployed Munibot commit/image.
2. Snapshot the raw object identity and five masks in the drop box; snapshot the exact
   scanner UUID/name/owner/group, bundle markers, and full task inventory.
3. Have Munibase authenticate and record the drop-box command, create the 60-second
   expectation, then issue one `llGiveInventory` send attempt.
4. Require a `received` bot receipt with the same raw name/type/asset and expected masks.
   Separately confirm unsolicited and deliberately mismatched offers are declined.
5. Run the scoped scanner PUT through the existing executor lease and stocking landmark.
   Require exact fresh target readback and retain the bot source item.
6. Retry the same request and require `idempotent: true`, `copied: false`, and the same
   target task item ID. Confirm unrelated and prior scanner inventory is unchanged.
7. Have the scanner independently report the same object identity through the #967
   authenticated scanner protocol. Keep its manual Experience/rez/attach acceptance gate
   in place; do not retire manually installed copies from this transport result alone.

Keep credentials, raw payloads, resident data, object/item/asset UUIDs, and permission
evidence private. Publish only sanitized outcomes and the deployed commit/image identity.
