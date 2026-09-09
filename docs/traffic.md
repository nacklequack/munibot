# Parcel traffic reads

`GET /api/parcels/{parcelId}/traffic` requires `sl.traffic.read` and an online bot.
It queries Second Life's parcel-info service without moving the bot. The response
contains `parcelId`, `regionName`, `parcelName`, `areaSquareMeters`, `dwell`,
`observedAt` and `source` (`ParcelInfoReply`). `dwell` preserves the received
floating-point value; `observedAt` is receipt time, not scoring day or publication
time. Invalid IDs return 400, invalid grid data 502, offline state 503, timeout 504.

`GET /api/regions/{regionName}/parcel-inventory` uses the same scope and requires
the bot to be connected to that simulator (current or neighboring). It refreshes
the parcel map, resolves parcel UUIDs and returns region/parcel areas, discovery
time and a completeness flag. It holds the bot's travel lock and never teleports.
An unconnected region returns 503. Discovery must be performed while connected
before hourly remote UUID reads can cover that region.

The Munibase hourly traffic collector is the intended consumer. Restrict tokens
to the scopes they need. An existing token without `sl.traffic.read` receives 403;
upgrading the binary alone does not alter token configuration.

The grid protocol has no request identifier for parcel-info replies beyond the
parcel UUID. Readers ignore other parcels and always unsubscribe on completion,
cancellation, send failure or timeout. A late reply for the same UUID cannot be
distinguished from a new reply; receipt time must never be represented as the
server's calculation time.

No endpoint returns an official region-wide score. A consumer may sum a complete
parcel set within an observation cycle, but must preserve incomplete coverage and
must not add repeated hourly snapshots as earned traffic.
