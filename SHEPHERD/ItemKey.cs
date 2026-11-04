using System.Collections.Generic;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // The live seam that keeps game-free code off MyItemType; matches the keys users write in [Reserve...] sections.
    public static class ItemKey
    {
        // Building a key allocates two strings, and the sort, the acceptance probes and the ledger ask per stack.
        // Static is safe: it holds strings, never a block, and a world has a bounded set of item types.
        static readonly Dictionary<MyItemType, string> keys = new Dictionary<MyItemType, string>();

        public static string Of(MyItemType type)
        {
            string key;
            if (!keys.Get(type, out key)) keys[type] = key = Category.Key(type.TypeId, type.SubtypeId);
            return key;
        }

        // The game-free planner side speaks only keys, so probing a live inventory needs the reverse trip.
        public static MyItemType ToType(string key)
        {
            var slash = key.IndexOf('/');
            return new MyItemType("MyObjectBuilder_" + key.Cut(0, slash), key.Cut(slash + 1));
        }
    }
}
