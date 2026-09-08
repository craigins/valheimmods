using System.Runtime.CompilerServices;

// The instanced-dungeon plugin ships as its own DLL but is built from this repo and released in
// lockstep with this one, and it is built on the dungeon primitives in Dungeons/ - DungeonInterior
// and DungeonReset. Sharing them this way rather than making those types public is deliberate:
// they are internals shared with a known sibling, not a public API other mods should build on and
// expect to stay put.
[assembly: InternalsVisibleTo("CraiginsValheimInstances")]
