# Stargates - design notes

Addressable portals. Every gate has a fixed six-symbol address. Dial another gate's address to
link the two; the link holds until either end disconnects, or until a third gate dials one of
them. **An incoming connection always wins** and drops whatever that gate was linked to before.

Everything below is verified against the decompiled `assembly_valheim.dll` from **Valheim 1.0.7**
(the 2026-09-09 build, via ILSpy). Class and method names are the durable part; re-check them
after a game update. This replaces the pre-1.0 sketch that lived in
`CraiginsValheimMod/Stargate/DESIGN_NOTES.md`, which was never verified and assumed a timed
connection.

**Status:** first playable cut is built. It has not been tested in-game.

## No timeout

The first sketch closed a connection after N seconds. That's been dropped. A timer means someone
has to run the clock, and the gate at the far end is almost never loaded on anyone's machine. So
the server would have to track open links on its own and remember them across restarts, and all
of that exists only to close them.

Replacing the timeout with "an incoming connection displaces the old one" makes a link pure
state. A link is two ZDO writes, and breaking one is two more. The server can make those writes
whether or not either gate is loaded anywhere, and none of it needs a clock.

## How vanilla portals actually work (1.0.7)

- **`TeleportWorld`** is the portal's MonoBehaviour. Its destination is **not the tag**. It's the
  ZDO's **Portal connection**, `zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal)`.
  `Teleport`, `TargetFound`, `HaveTarget` and the connected glow and sound all read only that.
  `TeleportWorldTrigger.OnTriggerEnter` calls `Teleport` for the local player. The tag (`s_tag`,
  plus `s_tagauthor`) is an input to pairing and nothing else.
- **Pairing is server-only.** `Game.Start` starts `ConnectPortalsCoroutine` only on the server.
  It runs `Game.ConnectPortals` every 5 s, and each run does two things:
  1. For every connected portal, if the partner is gone, has a different tag, or no longer points
     back, it disconnects it.
  2. For every unconnected portal, it picks a random unconnected portal with the same tag and
     links the two with `SetConnection`. That RPCs the owner if the owner is online; otherwise the
     server takes ownership, sets the connection and calls `ForceSendZDO`. The next tick applies
     it again with `ForceSetConnection` in case the RPC was lost.
- **Portal ZDOs are special-cased.** `Game.PortalPrefabHash` is built in `Game.Awake` from the
  serialized `m_portalPrefabs` list, which holds `portal_wood`, `portal_stone` and the old
  `portal`. Any ZDO with one of those prefabs goes into `ZDOMan.m_portalObjects` (keyed by sector)
  instead of the ordinary sector lists (`AddIfPortal`, and `ZDO.SetSector` returns early). It's
  saved in its own chunk (`ZoneSystem.ChunkPortal`) and exposed through `GetPortals()` /
  `GetPortalList()`. Clients still receive them by sector, since `FindObjects` adds the sector's
  portals. So on the server this is a complete, always-current list of every portal in the world.
- **ZDOIDs are not stable across restarts.** `ZDO.Load` does `m_uid.SetID(++ZDOID.m_loadID)` on
  every load. Connections survive through `ZDOConnectionHashData`, and `ZDOMan`'s *private*
  `ConnectPortals()` re-links them at load by matching hashes, not tags. So anything stored as a
  Portal connection persists like a vanilla portal link does, and anything keyed on a ZDOID does
  not.
- **The item check** is `Humanoid.IsTeleportable(m_allowAllItems)` →
  `Inventory.IsTeleportable`. It runs on the client and is bypassed by the `TeleportAll` global
  key.

## Design

**A gate is a `portal_wood` clone** (Jotunn `CustomPiece`, `CVM_Stargate`). It keeps
`TeleportWorld`, so walking through, the item rules, the glow and the exit placement are all
vanilla. The recipe and hammer category are inherited unchanged: Jotunn's `PieceConfig.Apply`
leaves `m_resources` and `m_category` alone when they aren't set.

**Links are stored as vanilla Portal connections.** Vanilla then teleports along them, draws them
and persists them across restarts, with no extra code.

**Gates stay in `PortalPrefabHash` but are kept out of pairing.** A `Game.Awake` postfix adds the
gate's hash. That runs before `ZNet.Start` loads the world, because every Awake runs before any
Start. This puts gates in `m_portalObjects`, which is the server-side list dialing needs; the
alternative is a sector-by-sector prefab scan on every dial. A `ZDOMan.GetPortalList` postfix
then strips gates out. Its only callers are `Game.ConnectPortals` and `ZDOMan.ConvertPortals`, and
both should ignore gates. Without that filter, vanilla would pair idle gates with untagged
portals (a gate has no tag) and tear down every dialed link within 5 s, because the two tags
"differ". Gates read their own list through `GetPortals()`, which isn't filtered.

**The address is derived from the gate's position, not stored.** It's FNV-1a over the position
quantized to 25 cm, then Murmur3's finalizer, taking 30 bits as 6 symbols from a 32-symbol
alphabet with no 0/O/1/I. The client computes it for hover text and the server computes it for
dialing. Nothing is written to the ZDO, so there's no assignment step and no ownership dance. It
works because a placed piece never moves and ZDO positions are synced and saved bit-exact. The
ZDOID can't be used, because it changes every load. It's a hash rather than an encoding of the
coordinates, so an address doesn't give away where the gate is. Collisions are about 1 in 2^30
per pair of gates. An ambiguous address is refused, not guessed.

**Dialing is server-authoritative.** Only the server holds every gate's ZDO.

- The client sends `CVM_StargateDial(gateZDOID, address)`.
- The server resolves the address, then drops the source's existing link and the target's
  existing link. Each old partner is unlinked too, so nothing points at a gate that doesn't point
  back. Then it links source and target.
- `CVM_StargateClose(gateZDOID)` unlinks both ends and can be sent from either end.
- Every write goes through vanilla's `Game.ForceSetConnection`. That's the same
  take-ownership, set, `ForceSendZDO` path the pairing loop settles connections with.

**The UI is vanilla's `TextInput`** panel, the one tags and signs use, driven by our own
`TextReceiver`. E dials and Shift+E disconnects, using the same `$KEY_AltPlace` / `$KEY_AltKeys`
hover split as `Tameable`. Both actions need ward access, the same as vanilla tag changes. Being
dialed does not.

## Files

- `StargatePlugin.cs`: entry point and config.
- `StargatePiece.cs`: piece registration and the two portal-list patches.
- `StargateAddress.cs`: address derivation and parsing.
- `StargateNetwork.cs`: RPCs and the server-side dial/close/list logic.
- `StargatePatches.cs`: hover text, interaction and the dial prompt.
- `StargateCommand.cs`: `stargate list`, a cheat that asks the server.

## Limits and known gaps

- **Untested in-game.** The first things to check are whether a dialed link still holds after
  10 s (i.e. whether the pairing filter works), whether it survives a server restart, whether a
  third gate dialing in drops the old link on both old ends, and whether a gate can be dialed
  while nobody is near it.
- **Removing the plugin leaves gates behind.** Gate ZDOs sit in the portal chunk, which
  `ZDOMan.Load` reads unconditionally. Without the filter, vanilla would pair them with untagged
  portals, and `ZNetScene` has no prefab to draw them with. Deconstruct gates before
  uninstalling.
- **`AllowAllItems` isn't synced.** It's applied to the prefab at startup on each machine and
  enforced client-side, exactly like vanilla's own item check.
- **A rebuilt gate gets a new address** unless it lands on the same spot, within 25 cm. That's by
  design, but worth knowing before you tell people an address.

## Not built yet

- A real dialer: a glyph panel, and an address book of gates this player has visited, which
  would live in `Player.m_customData` like the instance return point.
- Event-horizon visuals and dial and close sounds in place of the portal's glow.
- An iris: letting a gate refuse incoming connections, e.g. ward-gated.
- A cost to dial, if the free-travel balance turns out to need one.
- **Instances integration.** An address could resolve to an instanced dungeon rather than a gate,
  i.e. dial a quest key's address and the server opens the instance. The addressing is already
  derived on both sides (`InstanceRegion` derives zones from ids), so the two should compose.
