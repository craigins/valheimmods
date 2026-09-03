# Stargate Portals — design notes (not implemented)

Goal: replace paired portals with addressable "stargates" — dial an address, hold a timed
connection, either side can deactivate it.

This is a substantial feature (new networked behaviour + UI), so this repo only sets up the
project; there's no code here yet. Notes below are a starting point for when you're ready to
build it, not verified against the current decompiled game code — confirm class/method names
in ILSpy/dnSpy against your publicized `Assembly-CSharp.dll` before relying on any of this.

## What vanilla portals do (worth confirming first)
Valheim's portal piece is driven by a MonoBehaviour on the prefab (commonly referred to as
`TeleportWorld` in modding writeups) that:
- Reads/writes a **tag** string and a **target** into the object's `ZDO` (Valheim's networked
  object state — `ZDOID` + key/value store, synced by the `ZNet`/`ZDOMan` server).
- On player trigger, finds another portal instance whose tag matches and whose target field
  points back appropriately, then teleports the player.
- Pairing/matching happens by scanning all portal ZDOs for a tag match, not a direct link — so
  "addressable" gates aren't a huge structural departure from what already exists.

## Rough approach
1. **Prefab**: either reskin the existing portal piece or clone the piece to add a new prefab
   (Jotunn's `CustomPiece`/`PieceManager` is the easiest way to register a new piece without
   touching game assets directly).
2. **Address instead of tag pairing**: replace the free-text tag with a fixed-format address
   (e.g. 6-symbol code like the show). Store it in the `ZDO` same as vanilla does for the tag.
3. **Dialing UI**: a custom in-game panel (Jotunn has helpers for adding UI panels) where the
   player selects/enters an address instead of typing a tag into a sign.
4. **Timed, bidirectional connection**:
   - On successful dial, mark both gates' ZDOs as "active" with a shared connection id and a
     start timestamp.
   - A patched/replacement update loop checks elapsed time and auto-closes the wormhole after
     N seconds unless "kept open" (vanilla Stargate lore: it stays open while something/someone
     is actively travelling — decide your own rule).
   - Either side needs a way to send a "deactivate" request that both ends respect — this needs
     an RPC, not just local ZDO edits, since the two gates can be in different loaded zones/on
     different clients. **Jotunn's `NetworkManager`/custom RPC helpers** (or plain
     `ZRoutedRpc.Instance.Invoke(...)`) are the way to do cross-client signaling in Valheim.
   - Watch out for **zone loading**: the target gate's `GameObject` may not be loaded/simulated
     on a given client (Valheim only keeps nearby zones active), so state changes must go
     through `ZDO`/RPC, not direct `GameObject`/component references.
5. **Effects**: swap the portal's "open" visual/audio for a stargate-style event horizon when
   active, closed appearance when idle.

## Open questions to resolve before coding
- Exact current class name/fields for the portal script and its ZDO keys (confirm per game
  version — Valheim has renamed internals before).
- Whether to keep multiplayer server-authoritative logic in one place (e.g. only the ZDO owner
  runs the timer) to avoid desync between clients.
- Address format/UI: text entry vs. a symbol-selector.
