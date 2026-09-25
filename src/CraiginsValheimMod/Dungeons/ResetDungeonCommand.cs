using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Entities;
using UnityEngine;

namespace CraiginsValheimMod.Dungeons
{
    /// <summary>
    /// Console command: "resetdungeon". Server-only (see OnlyServer) - the world's ZDOs live
    /// there, and DungeonGenerator.Save() writes to the generator's ZDO, so this is meaningless
    /// anywhere else.
    ///
    /// Wipes a generated dungeon interior and rebuilds it with a fresh layout. Vanilla bakes a
    /// dungeon's rooms into its ZDO the first time its zone generates and never revisits them, so
    /// this is the only way to change a dungeon that already exists - including making the
    /// MinDungeonRooms setting apply retroactively to one.
    ///
    /// Two steps, and the first is the one vanilla doesn't do for you:
    ///
    ///   1. Destroy the interior's contents. DungeonGenerator.Clear() only removes room shells;
    ///      the chests/spawners/doors are unparented ZDOs that would otherwise survive into the
    ///      new layout. See DungeonInterior for the details.
    ///   2. Call DungeonGenerator.Generate(seed, mode), which is public and already does the
    ///      whole reset internally - Clear, re-seed Random, GenerateRooms, Save back to the same
    ///      ZDO. Generation isn't reimplemented here, just re-invoked. See DungeonReset.Execute
    ///      for which spawn mode.
    ///
    /// TARGETING. Any generated dungeon in the world can be named by zone= or name=, whether or
    /// not it's instantiated here: DungeonReset.Acquire builds a transient generator from the
    /// ZDO when it isn't, which on a dedicated server is nearly always, since the server only
    /// instantiates objects around the world origin. With no arguments the nearest instantiated
    /// dungeon is used, which only means something where there's a local player to be near.
    /// 'resetdungeon list all' gives every dungeon's zone for the explicit forms.
    ///
    /// NOT TESTED IN-GAME.
    /// </summary>
    internal class ResetDungeonCommand : ConsoleCommand
    {
        public override string Name => "resetdungeon";

        public override string Help =>
            "Wipes a generated dungeon's interior and rebuilds it with a new layout - the only way to change " +
            "a dungeon that already exists, since vanilla bakes the layout into the world when the zone first " +
            "generates. Usage: resetdungeon [list [all]] [name=<Location>] [zone=<x>,<z>] [seed=<n>] [dry] " +
            "[force] [wipebuilt]. With no arguments it targets the nearest dungeon to the local player; on a " +
            "dedicated server give zone= or name= ('list all' shows every dungeon's zone). Any generated " +
            "dungeon can be targeted, loaded or not. " +
            "Player-built pieces inside are kept unless 'wipebuilt'; dropped items are always swept. " +
            "Refuses while anyone is inside, or while the biome's boss is alive per Dungeons.ResetBossGate, " +
            "unless 'force'. Clients with this mod redraw the rooms at once; clients without it keep the old " +
            "rooms until they leave and re-enter the zone.";

        public override bool OnlyServer => true;

        private sealed class Options
        {
            public bool List;
            public bool ListAll;
            public bool DryRun;
            public bool Force;
            public bool WipePlayerBuilt;
            public string Name;
            public bool HaveZone;
            public Vector2s Zone;
            public bool HaveSeed;
            public int Seed;
        }

        public override void Run(string[] args)
        {
            var opts = new Options();
            if (!ParseArgs(args, opts, out string parseError))
            {
                Print("resetdungeon: " + parseError);
                return;
            }

            if (ZoneSystem.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
            {
                Print("resetdungeon: the world isn't loaded yet.");
                return;
            }

            List<DungeonGenerator> loaded = DungeonReset.FindLoaded();

            if (opts.List)
            {
                PrintList(loaded, opts.ListAll);
                return;
            }

            using (DungeonReset.Handle target = SelectTarget(loaded, opts, out string reason))
            {
                if (target == null)
                {
                    Print("resetdungeon: " + reason);
                    return;
                }

                Reset(target, opts);
            }
        }

        // ---- targeting -------------------------------------------------------------------

        private DungeonReset.Handle SelectTarget(List<DungeonGenerator> loaded, Options opts, out string reason)
        {
            reason = null;

            if (opts.HaveZone)
            {
                return DungeonReset.Acquire(opts.Zone, out reason);
            }

            if (opts.Name != null)
            {
                // Instantiated here first: on a host that's "the one I'm standing at".
                var matches = new List<DungeonGenerator>();
                foreach (DungeonGenerator dungeon in loaded)
                {
                    string name = LocationName(dungeon);
                    if (name != null && name.IndexOf(opts.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matches.Add(dungeon);
                    }
                }
                if (matches.Count == 1)
                {
                    return Live(matches[0]);
                }
                if (matches.Count > 1)
                {
                    DungeonGenerator nearest = Nearest(matches);
                    return nearest != null ? Live(nearest) : Ambiguous(matches, out reason);
                }

                // Otherwise anywhere in the world, from the location table.
                List<ZoneSystem.LocationInstance> placed = WorldDungeons(opts.Name);
                if (placed.Count == 0)
                {
                    reason = $"no generated dungeon matching '{opts.Name}'. " +
                             "Run 'resetdungeon list all' to see what's in this world.";
                    return null;
                }
                if (placed.Count > 1)
                {
                    Vector3 from = ReferencePoint(out bool haveRef);
                    if (!haveRef)
                    {
                        reason = $"{placed.Count} dungeons match '{opts.Name}' and there's no local player to measure " +
                                 "'nearest' from (dedicated server). Pick one explicitly with zone=<x>,<z>:";
                        foreach (ZoneSystem.LocationInstance instance in placed)
                        {
                            Vector2s zone = ZoneSystem.GetZone(instance.m_position);
                            reason += $"\n  zone={zone.x},{zone.y}  {DungeonReset.LocationName(instance.m_location)}  " +
                                      $"at ({instance.m_position.x:0}, {instance.m_position.z:0})";
                        }
                        return null;
                    }
                    placed.Sort((a, b) => Utils.DistanceXZ(from, a.m_position).CompareTo(Utils.DistanceXZ(from, b.m_position)));
                }
                return DungeonReset.Acquire(ZoneSystem.GetZone(placed[0].m_position), out reason);
            }

            if (loaded.Count == 0)
            {
                ReferencePoint(out bool haveLocal);
                reason = haveLocal
                    ? "no dungeon is loaded near you. Go stand at the one you want, or name it with " +
                      "zone=<x>,<z> or name=<Location> ('resetdungeon list all' shows them)."
                    : "on a dedicated server, say which dungeon: zone=<x>,<z> or name=<Location> " +
                      "('resetdungeon list all' shows them).";
                return null;
            }
            if (loaded.Count == 1)
            {
                return Live(loaded[0]);
            }
            DungeonGenerator best = Nearest(loaded);
            return best != null ? Live(best) : Ambiguous(loaded, out reason);
        }

        private static DungeonReset.Handle Live(DungeonGenerator dungeon)
        {
            return new DungeonReset.Handle { Dungeon = dungeon, Transient = false };
        }

        /// <summary>
        /// Every placed location with an interior whose name contains <paramref name="name"/>,
        /// straight from the location table - the same source 'list all' prints from.
        /// </summary>
        private static List<ZoneSystem.LocationInstance> WorldDungeons(string name)
        {
            var found = new List<ZoneSystem.LocationInstance>();
            foreach (ZoneSystem.LocationInstance instance in ZoneSystem.instance.m_locationInstances.Values)
            {
                if (instance.m_placed
                    && instance.m_location != null
                    && instance.m_location.m_interiorRadius > 0f
                    && DungeonReset.LocationName(instance.m_location).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found.Add(instance);
                }
            }
            return found;
        }

        private static DungeonGenerator Nearest(List<DungeonGenerator> candidates)
        {
            Vector3 from = ReferencePoint(out bool haveRef);
            if (!haveRef)
            {
                return null;
            }

            DungeonGenerator best = null;
            float bestDist = float.MaxValue;
            foreach (DungeonGenerator dungeon in candidates)
            {
                float dist = Utils.DistanceXZ(from, dungeon.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = dungeon;
                }
            }
            return best;
        }

        private DungeonReset.Handle Ambiguous(List<DungeonGenerator> candidates, out string reason)
        {
            reason = $"{candidates.Count} dungeons match and there's no local player to measure 'nearest' from " +
                     "(dedicated server). Pick one explicitly with zone=<x>,<z>:";
            foreach (DungeonGenerator dungeon in candidates)
            {
                Vector2s zone = DungeonInterior.ZoneOf(dungeon);
                reason += $"\n  zone={zone.x},{zone.y}  {LocationName(dungeon) ?? "?"}";
            }
            return null;
        }

        /// <summary>
        /// Where "nearest" is measured from. A dedicated server has no local player, and its
        /// ZNet reference position isn't anybody's - so say so rather than silently measuring
        /// from a meaningless point.
        /// </summary>
        private static Vector3 ReferencePoint(out bool haveRef)
        {
            if (Player.m_localPlayer != null)
            {
                haveRef = true;
                return Player.m_localPlayer.transform.position;
            }
            haveRef = false;
            return Vector3.zero;
        }

        private static string LocationName(DungeonGenerator dungeon)
        {
            return DungeonReset.LocationName(dungeon);
        }

        // ---- the actual reset ------------------------------------------------------------

        /// <summary>
        /// Argument handling and reporting only - the checks and the rebuild itself live in
        /// DungeonReset, shared with the dungeon-entrance interaction so the two can't drift.
        /// </summary>
        private void Reset(DungeonReset.Handle handle, Options opts)
        {
            DungeonGenerator target = handle.Dungeon;
            string label = DungeonReset.Describe(target);

            if (!DungeonReset.Validate(target, opts.Force, out string error))
            {
                Print($"resetdungeon: {error}" +
                      (opts.Force ? "" : " Pass 'force' if you mean it."));
                return;
            }

            bool preserveBuilt = !opts.WipePlayerBuilt;
            int seed = opts.HaveSeed ? opts.Seed : DungeonReset.NewSeed();

            if (opts.DryRun)
            {
                DungeonReset.Result preview = DungeonReset.Preview(target, seed, preserveBuilt);
                Print($"resetdungeon (dry run): would destroy {preview.Destroyed} object(s) in {label}" +
                      $"{PreservedNote(preview.Preserved)}, then regenerate its " +
                      $"{(preview.OldRooms >= 0 ? preview.OldRooms + " room(s)" : "layout")} with seed {seed}. " +
                      "Nothing changed.");
                return;
            }

            DungeonReset.Result result = DungeonReset.Execute(handle, seed, preserveBuilt);

            Print($"resetdungeon: rebuilt {label}{(handle.Transient ? " (not instantiated here - rebuilt from its ZDO)" : "")} " +
                  $"with seed {result.Seed} - " +
                  $"{Rooms(result.OldRooms)} rooms -> {Rooms(result.NewRooms)} rooms, " +
                  $"destroyed {result.Destroyed} old object(s){PreservedNote(result.Preserved)}. " +
                  $"Re-run with seed={result.Seed} to reproduce this exact layout.");

            if (ZNet.instance.GetPeers().Count > 0)
            {
                Print("resetdungeon: clients running this mod redraw the rooms within a second of the new layout " +
                      "reaching them. A client WITHOUT it keeps the old room geometry until it leaves and " +
                      "re-enters the zone, around the new contents.");
            }

            Print("resetdungeon: run 'save' if you want this on disk before the next autosave.");
        }

        private static string Rooms(int count)
        {
            return count >= 0 ? count.ToString() : "?";
        }

        private static string PreservedNote(int preserved)
        {
            return preserved > 0 ? $", keeping {preserved} player-built object(s)" : "";
        }

        // ---- listing ---------------------------------------------------------------------

        private void PrintList(List<DungeonGenerator> loaded, bool all)
        {
            Vector3 from = ReferencePoint(out bool haveRef);

            if (loaded.Count == 0)
            {
                Print("resetdungeon: no dungeons instantiated on this machine" +
                      (haveRef ? "." : " (a dedicated server only instantiates around the world origin) - " +
                                       "any generated one can still be reset by zone= or name=."));
            }
            else
            {
                Print($"resetdungeon: {loaded.Count} dungeon(s) instantiated here:");
                foreach (DungeonGenerator dungeon in loaded)
                {
                    Vector2s zone = DungeonInterior.ZoneOf(dungeon);
                    int rooms = DungeonInterior.GetSavedRoomCount(dungeon);
                    string dist = haveRef ? $"  {Utils.DistanceXZ(from, dungeon.transform.position):0}m away" : "";
                    Print($"  zone={zone.x},{zone.y}  {LocationName(dungeon) ?? dungeon.name}  " +
                          $"{(rooms >= 0 ? rooms + " rooms" : "not yet generated")}  seed {dungeon.m_generatedSeed}{dist}");
                }
            }

            if (!all)
            {
                return;
            }

            // Everything with an interior, loaded or not, straight from the location table -
            // m_interiorRadius is cached on ZoneLocation, so this needs no prefab loaded.
            var world = new List<ZoneSystem.LocationInstance>();
            foreach (ZoneSystem.LocationInstance instance in ZoneSystem.instance.m_locationInstances.Values)
            {
                if (instance.m_location != null && instance.m_location.m_interiorRadius > 0f)
                {
                    world.Add(instance);
                }
            }

            world.Sort((a, b) => Utils.DistanceXZ(from, a.m_position).CompareTo(Utils.DistanceXZ(from, b.m_position)));

            const int Limit = 40;
            Print($"resetdungeon: {world.Count} dungeon location(s) in this world" +
                  (haveRef ? ", nearest first" : ", ordered from the world origin") +
                  (world.Count > Limit ? $" (showing {Limit})" : "") + ":");

            for (int i = 0; i < world.Count && i < Limit; i++)
            {
                ZoneSystem.LocationInstance instance = world[i];
                Vector2s zone = ZoneSystem.GetZone(instance.m_position);
                Print($"  zone={zone.x},{zone.y}  {DungeonReset.LocationName(instance.m_location)}  " +
                      $"at ({instance.m_position.x:0}, {instance.m_position.z:0})  " +
                      $"{Utils.DistanceXZ(from, instance.m_position):0}m" +
                      (instance.m_placed ? "" : "  [not generated yet]"));
            }
        }

        // ---- args ------------------------------------------------------------------------

        private static bool ParseArgs(string[] args, Options opts, out string error)
        {
            error = null;

            // args[0] is the command name itself.
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg))
                {
                    continue;
                }

                if (arg.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    opts.List = true;
                }
                else if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    opts.ListAll = true;
                }
                else if (arg.Equals("dry", StringComparison.OrdinalIgnoreCase))
                {
                    opts.DryRun = true;
                }
                else if (arg.Equals("force", StringComparison.OrdinalIgnoreCase))
                {
                    opts.Force = true;
                }
                else if (arg.Equals("wipebuilt", StringComparison.OrdinalIgnoreCase))
                {
                    opts.WipePlayerBuilt = true;
                }
                else if (arg.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                {
                    opts.Name = arg.Substring("name=".Length);
                    if (opts.Name.Length == 0)
                    {
                        error = "name= needs a location name, e.g. name=SunkenCrypt4.";
                        return false;
                    }
                }
                else if (arg.StartsWith("seed=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(arg.Substring("seed=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                    {
                        error = $"couldn't read a seed from '{arg}'.";
                        return false;
                    }
                    opts.Seed = seed;
                    opts.HaveSeed = true;
                }
                else if (arg.StartsWith("zone=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = arg.Substring("zone=".Length).Split(',');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                    {
                        error = $"couldn't read zone coordinates from '{arg}' - expected zone=<x>,<z> with no spaces, e.g. zone=12,-34.";
                        return false;
                    }
                    opts.Zone = new Vector2s(x, y);
                    opts.HaveZone = true;
                }
                else
                {
                    error = $"don't know what '{arg}' means. Expected: list, all, dry, force, wipebuilt, " +
                            "name=<Location>, zone=<x>,<z>, seed=<n>.";
                    return false;
                }
            }

            if (opts.HaveZone && opts.Name != null)
            {
                error = "give either name= or zone=, not both.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// A dedicated server has no Console.instance, and this command says a lot worth keeping.
        /// Everything goes to the log either way.
        /// </summary>
        private static void Print(string message)
        {
            if (Console.instance != null)
            {
                Console.instance.Print(message);
            }
            Jotunn.Logger.LogInfo(message);
        }
    }
}
