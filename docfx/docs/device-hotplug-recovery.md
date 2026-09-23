# Device hotplug, identity, and missing-device lifecycle

This document describes the device-lifecycle changes made to Artemis core and
the OpenRGB device plugin. The work prevents duplicate devices, preserves
profile bindings across rescans and hotplug, and gives users an explicit way to
remove hardware that is permanently gone.

## Why this was necessary

OpenRGB can rebuild its entire controller list while detecting hardware. During
that interval a real device may temporarily disappear, return with a new
RGB.NET object, or arrive in a different list position. Some controllers also
appear late in the scan. Treating each intermediate list as final caused four
related failures:

- a transient remove/add cycle destroyed or stranded layer bindings;
- replacement objects could create duplicate Artemis devices and duplicate
  `DeviceEntity` instances with the same database key;
- an unrelated device could stop rendering after another device was hotplugged;
- saved devices that were truly gone had no clear, safe removal path.

OpenRGB also represents one physical controller as multiple logical devices
when a controller is split into zones or segments. If OpenRGB later changes that
zone table, an old child identity can remain in storage even though the physical
motherboard is present. Labeling that child as missing hardware is misleading.

## Design goals

The lifecycle now follows these rules:

1. A provider identifier represents one logical Artemis device.
2. Temporary transport or discovery loss does not delete logical state.
3. A returning device reuses the existing device, LED objects, and bindings
   whenever its stable identity can be proven.
4. Missing-state presentation is delayed to ignore ordinary scan churn.
5. Permanently absent devices remain visible in **Settings > Devices** until the
   user explicitly removes them.
6. Split children are classified using their physical parent, so obsolete zone
   rows are not presented as missing physical hardware while a sibling is live.
7. Artemis never guesses identity from names, list positions, or geometry.

## Responsibility boundary

The device provider owns discovery, transport, and stable identifiers derived
from its upstream API. Artemis core owns:

- logical device lifetime;
- database identity;
- render-surface membership;
- profile and layer bindings;
- missing-device presentation; and
- rebinding a replacement RGB.NET object to retained state.

Providers should report additions and removals, not reload themselves or delete
Artemis state to recover a connection. Core decides whether an event represents
a new logical device, a replacement backing object, or a temporary absence.

## Persistent identity and deduplication

`DeviceProvider.GetDeviceIdentifier` supplies the primary provider identifier.
Core uses it for startup discovery, database lookup, runtime add events,
reconnects, and provider reloads. The database entity `Id` remains stable even
when a provider later supplies a different runtime identifier.

For OpenRGB, the key contains the server identity, stable controller identity,
and, when split, the exact zone or segment index. It does not depend on the
controller's current position in OpenRGB's list.

Before adding or updating stored devices, Artemis reconciles each identifier to
the single entity instance already tracked by Entity Framework. This avoids the
`DeviceEntity` tracking exception that occurs when two different objects with
the same `Id` are attached to one context. Runtime add-before-remove ordering is
also handled: the old backing object is disconnected and the existing logical
device is rebound instead of creating a duplicate.

Legacy RGB.NET identifiers may be retained as aliases so old profile records can
resolve after an identity migration. An alias is a compatibility lookup; it is
not permission to merge two simultaneously reported physical devices.

When the exact identifier changes, a provider may also supply an opaque
`GetReconnectionSignature`. Artemis persists this signature and identifier
aliases with the device row. It looks for a unique matching logical device
among disconnected devices, including those still in the removal grace period,
or among unclaimed saved rows after an application restart. A match rebinds the
existing Artemis device and LEDs instead of creating a new row. If multiple
missing devices have the same signature, Artemis does not guess. A currently
connected device is not matched by signature, so two simultaneously connected
devices of the same model remain separate.

The OpenRGB signature uses SDK-reported server, manufacturer, model, device
type, split-part identity, and LED IDs. It does not parse or hardcode USB
VID/PID values. If those properties cannot distinguish two missing devices,
manual recovery may still be necessary.

## Device states

| State | Physical output | Artemis state | User presentation |
| --- | --- | --- | --- |
| Present | Active | Bound to a live RGB.NET object | Normal device list |
| Grace period | Temporarily paused | Existing logical objects retained | Treated as transient |
| Missing | Inactive | Entity, layout, LEDs, and bindings retained | Settings > Devices |

When an exact identifier or unique reconnection signature returns, Artemis
cancels any pending removal and rebinds the replacement RGB.NET object. If the device had reached Missing,
Artemis also refreshes the render surface so deferred layer bindings become
active immediately; no Artemis or OpenRGB restart is required.

## Eight-second removal grace period

Runtime removals wait eight seconds before becoming Missing. A matching add
during that window cancels the removal. The delay is intentionally generous:
OpenRGB detection may briefly omit devices, particularly SDK devices late in
the detection order. The observed Vulcan II Max return time is now around two
seconds, but the grace still covers slower transient scans.

The grace period only delays presentation as Missing; it does not control
identity matching. A device returning with a changed runtime path can reclaim
its logical identity during the grace period. The OpenRGB plugin separately
refuses to publish intermediate scan results as final.

## OpenRGB detection handling

The OpenRGB plugin negotiates protocol v6 and uses the SDK's detection status
instead of assuming a request has completed when it was merely accepted.
During detection, the plugin keeps the last trusted controller snapshot.

After OpenRGB reports detection complete, the plugin polls promptly and applies
these rules:

- additions are published immediately;
- a reduced controller list must remain stable before removals are published;
- reconnect attempts use the same stable identity rules; and
- a scan never deletes Artemis persistence directly.

This prevents a keyboard near the end of OpenRGB's detector sequence from being
removed based on a partial list. OpenRGB itself may still pause LED output while
detecting. Disabling unused detector backends improves scan time without
changing Artemis's safety behavior.

## Rebinding LEDs and profiles

An `ArtemisDevice` survives the loss of its backing `IRGBDevice`. On reconnect:

- if LED IDs are unchanged, existing `ArtemisLed` objects are rebound in place;
- if topology changed, LEDs are reconciled by exact `LedId`;
- surviving LED wrappers retain object identity;
- removed LED IDs remain represented by deferred persisted layer bindings; and
- genuinely new LED IDs receive new wrappers.

Layer persistence stores the provider device identifier and `LedId`. This lets a
binding resume when that exact device and LED return. Artemis deliberately does
not perform fuzzy LED matching because silently attaching an effect to the
wrong LED is worse than leaving a binding deferred.

## Split zones and physical-parent identity

Providers that split a physical controller can override
`DeviceProvider.GetParentDeviceIdentifier`. OpenRGB returns the controller part
of an identifier for `zone:` and zone-segment children.

When Artemis evaluates saved-but-unclaimed entities, it checks whether a live
sibling from the same provider has the same parent identity:

- if no sibling is present, the saved child remains eligible for Missing;
- if a sibling is present, the physical controller is known to be connected, so
  the absent child is hidden from the Missing list;
- the stored child row and deferred bindings are not deleted; and
- if the child identity returns later, it reclaims the same entity and bindings.

This is classification, not fuzzy deduplication. Exact child identities remain
distinct for rendering and persistence. The parent key is used only to avoid
misrepresenting obsolete or temporarily absent topology children as missing
physical devices.

## Missing-device UI and permanent removal

Confirmed missing devices appear under **Settings > Devices**, not in the main
device UI. Each missing device has its own **Remove** action.

Normal disappearance is non-destructive: Artemis keeps the database row,
layout, and profile references indefinitely so the device can resume when it
returns. Explicit removal is destructive by design. It removes the saved device
and cleanly removes its layer bindings so a permanently lost device does not
leave unusable references behind.

The action is appropriate only when the hardware is permanently gone or the
saved identity is intentionally being discarded.

## Important event flows

### Transient unplug or rescan

1. Provider reports remove.
2. Artemis starts the eight-second timer and retains the logical device.
3. Provider reports the same identifier or a uniquely matching signature before
   the timer expires.
4. Artemis cancels removal and rebinds the backing object.
5. Rendering continues with the existing profile bindings.

### Confirmed absence and reconnect

1. Provider reports remove and no matching add arrives during the grace period.
2. Artemis moves the retained device to Missing without deleting persistence.
3. The device later returns with the same identifier or a uniquely matching
   persisted signature.
4. Artemis reactivates the retained logical device and refreshes LEDs.
5. Existing bindings resume automatically.

### Obsolete OpenRGB zone

1. Storage contains `controller|zone:7` from an earlier OpenRGB topology.
2. Current discovery exposes only `controller|zone:2` through
   `controller|zone:6`.
3. Artemis sees a live sibling with the same physical parent.
4. `zone:7` stays stored but is not shown as missing hardware.
5. If OpenRGB exposes `zone:7` again, the saved entity and bindings are reused.

## Safety properties and limits

The implementation guarantees that a transient event does not intentionally
delete saved state and that one provider identifier maps to one logical device.
It cannot safely recover identity when an upstream provider changes every
useful identifier or when two missing devices have indistinguishable signatures.
Display names or discovery order alone are not considered proof of identity.

Parent-aware classification does not merge zones. A zone that returns with a
different exact identifier is a different logical child unless the provider
offers an explicit migration path.

## Regression coverage

`Artemis.Core.Tests/DeviceHotplugTests.cs` covers:

- unplug/replug while retaining device and LED objects;
- delayed removal and cancellation;
- changed runtime identifiers both during the grace period and after Missing;
- persisted signature and identifier-alias recovery after restart;
- ambiguous same-model signatures remaining separate;
- recovery after a device reaches Missing;
- add-before-remove replacement ordering without duplicate entities;
- identical devices returning in reverse order;
- legacy identifier aliases;
- provider reloads;
- partial topology changes retaining surviving LED objects;
- permanent removal and binding cleanup; and
- parent-aware filtering at startup and on the first runtime child add.

The OpenRGB test project covers protocol-v6 negotiation and payload parsing,
stable controller identity, detection state, and provider lifecycle behavior.

## Operational validation

The combined build was exercised through repeated OpenRGB rescans, service
restarts, and at least ten keyboard and mouse hotplug cycles. Devices recovered
without restarting Artemis, and profile bindings resumed after reconnect.

When diagnosing a future failure, record the time and collect the current
Artemis log before restarting either process. The most useful evidence is the
sequence of OpenRGB detection state, provider add/remove events, persistent
identifiers, and Artemis reconnect messages.
