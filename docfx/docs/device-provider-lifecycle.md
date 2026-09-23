# Device provider lifecycle and identity

Artemis keeps profile and surface state attached to logical devices while a
provider temporarily loses its physical backing objects. This is required for
providers such as OpenRGB whose SDK connection and controller objects are
recreated during service restarts, rescans, and hardware hotplug.

## Ownership boundary

A device provider owns discovery, transport, and the identifier it can prove
from its upstream API. Artemis core owns:

- logical device lifetime;
- surface and layer bindings;
- database identity;
- render-surface membership; and
- rebinding a replacement RGB.NET object to retained state.

Providers must not reload themselves, replay rendered colors, manufacture
identity metadata, or remove and recreate Artemis state to recover a connection.

## Persistent identity

`DeviceProvider.GetDeviceIdentifier` supplies the primary provider identifier.
Core uses it consistently for database lookup, runtime add events, and provider
reloads. A provider may expose an old RGB.NET identifier as a migration alias.
An optional provider-owned `GetReconnectionSignature` can reconcile a uniquely
matching disconnected or stored-missing device when that primary identifier
changes. Core persists the signature and identifier aliases while keeping the
original database entity key stable. It never merges two simultaneously
connected devices by signature or guesses among ambiguous missing candidates.

Core enforces one logical device per provider identifier. If a provider sends an
add for a replacement RGB.NET object before removing the old object, Artemis
disconnects the old backing object and rebinds the retained logical device. It
does not create a second `DeviceEntity` with the same key.

## Disconnect and reconnect

Removing a provider or receiving a runtime remove event marks its logical device
disconnected. The `ArtemisDevice`, `DeviceEntity`, and `ArtemisLed` objects remain
available to profiles, so a transient provider outage does not destroy bindings.
Disconnected devices are omitted from output updates until a matching add event
rebinds them.

If the replacement has the same LED IDs, every `ArtemisLed` is rebound in place.
If its topology changed, core reconciles by `LedId`: surviving wrappers are kept,
removed IDs are dropped, and only genuinely new IDs receive new wrappers. Layers
therefore retain object references for every LED the provider can identify.

Layer persistence stores the provider device identifier and `LedId`. Missing LED
records remain in the layer entity and may resolve again when that exact identity
returns.

## Limits

Core cannot prove identity when a provider changes every useful identifier or
when several missing devices share the same signature. Matching by display name
or list position alone risks silently binding an effect to the wrong hardware.
The removal grace controls only when a disconnected device appears as Missing;
signature reconciliation also works before that timer expires.

## Regression coverage

`Artemis.Core.Tests/DeviceHotplugTests.cs` covers:

- unplug/replug while retaining device and LED objects;
- add-before-remove replacement ordering without duplicate database entities;
- identical devices returning in reverse order;
- legacy identifier aliases;
- provider reloads; and
- partial topology changes retaining all surviving LED objects.
