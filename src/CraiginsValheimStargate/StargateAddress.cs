using System.Text;
using UnityEngine;

namespace CraiginsValheimMod.Stargate
{
    /// <summary>
    /// A gate's address: six symbols, shown as ABC-DEF.
    ///
    /// DERIVED, NOT STORED. The address is a hash of the gate's position, computed on demand - on the
    /// client for hover text, on the server for dialing. Nothing is written to the ZDO, so there's no
    /// assignment step, no ownership to negotiate, and nothing to migrate. It works because a placed
    /// piece never moves, and a ZDO's position is carried as the same float bits everywhere: it's
    /// synced exactly and saved exactly. The ZDOID would have been the obvious key and is useless
    /// here - ZDO.Load hands every ZDO a new id on every world load.
    ///
    /// A hash, not an encoding of the coordinates, so an address says nothing about where the gate
    /// is. You learn one by visiting the gate or being told it.
    ///
    /// 32 symbols ^ 6 = 2^30 addresses. Two gates colliding is vanishingly unlikely at any realistic
    /// gate count, and when it happens the server refuses to dial the ambiguous address rather than
    /// picking one.
    /// </summary>
    internal static class StargateAddress
    {
        // Letters and digits minus the pairs people misread: 0/O and 1/I.
        private const string Symbols = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public const int Length = 6;

        public static string Of(ZDO gate)
        {
            return Of(gate.GetPosition());
        }

        public static string Of(Vector3 position)
        {
            // Quantized to 25cm before hashing. The bits are identical everywhere anyway; this just
            // means a future path that rewrites a gate's position with float noise can't change its
            // address. Gates can't be built within 25cm of each other, so nothing is lost.
            uint hash = 2166136261u;
            hash = Mix(hash, Mathf.RoundToInt(position.x * 4f));
            hash = Mix(hash, Mathf.RoundToInt(position.y * 4f));
            hash = Mix(hash, Mathf.RoundToInt(position.z * 4f));
            hash = Avalanche(hash);

            var code = new char[Length];
            for (int i = 0; i < Length; i++)
            {
                code[i] = Symbols[(int)(hash & 31u)];
                hash >>= 5;
            }
            return new string(code);
        }

        public static string Format(string code)
        {
            return code.Length == Length ? code.Substring(0, 3) + "-" + code.Substring(3) : code;
        }

        /// <summary>Accepts any case, with or without the dash or spaces.</summary>
        public static bool TryParse(string input, out string code)
        {
            code = null;
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            var text = new StringBuilder(Length);
            foreach (char c in input.ToUpperInvariant())
            {
                if (c == '-' || char.IsWhiteSpace(c))
                {
                    continue;
                }
                if (Symbols.IndexOf(c) < 0)
                {
                    return false;
                }
                text.Append(c);
            }

            if (text.Length != Length)
            {
                return false;
            }
            code = text.ToString();
            return true;
        }

        // FNV-1a over the value's four bytes. string.GetHashCode isn't used anywhere near this: it
        // differs between runtimes, and both ends of a dial have to agree.
        private static uint Mix(uint hash, int value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (uint)(value >> (i * 8)) & 0xFFu;
                hash *= 16777619u;
            }
            return hash;
        }

        // Murmur3's finalizer, so neighbouring gates don't get neighbouring addresses.
        private static uint Avalanche(uint hash)
        {
            hash ^= hash >> 16;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 13;
            hash *= 0xC2B2AE35u;
            hash ^= hash >> 16;
            return hash;
        }
    }
}
