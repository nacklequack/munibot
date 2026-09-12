# Sharing new task-inventory source

Task inventory delivery can create scripts and notecards in a group-shared object
owned by another resident. The bot must have modify access to the target, and its
active group must match the target's group. Membership alone is insufficient.
Existing source items must already permit the bot to read and modify them.

For a missing item, Munibot requests the target object's actual owner, group, and
permissions from the simulator. If another resident owns it, creation requires
group modify permission and that group active on the bot. A bot-owned object can
also accept new source without group sharing.

Before copying new source to a shared target, Munibot prepares group sharing on
its temporary bot-owned inventory item through the inventory API. Only the group
ID and group move/modify/copy mask are patched. The bot waits for acknowledgement,
fetches the item again, and verifies its source identity and other permissions.
Ownership, everyone access, and next-owner permissions remain unchanged.

The copied task item must retain the expected sharing before the final upload
and source readback. Scripts remain stopped. Existing-item updates preserve the
item's permissions and UUID; they never apply this creation policy to existing
inventory, including a copy left by an earlier failed attempt.

If a new copy loses sharing, `source_sharing_unconfirmed` reports an uncertain
outcome. Inspect that exact copy; a failed readback does not authorize another
creation. An owner can repair sharing on an existing copy so inspection and an
in-place update can resume. Rejected or unverified temporary-item preparation
stops before copying anything into the target.

The consumer remains the authenticated task-inventory PUT endpoint used by
Munibase replenishment. Automated tests cover preparation acknowledgement,
permission preservation, target selection, and failure handling. Live acceptance
must demonstrate both a new script and a new notecard arriving readable without
a manual sharing step, using the actual ownership and active-group arrangement.
