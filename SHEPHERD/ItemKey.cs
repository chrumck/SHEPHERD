using System.Collections.Generic;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    // The reserve-quota key of an item (ShortTypeId/SubtypeId) — the live seam so Reserve.cs never touches a
    // game type. Matches the raw TypeId/SubtypeId keys users write in the [Reserve...] Custom Data sections.
    public static class ItemKey
    {
        // Building a key allocates two strings, and the sort, the machine and reserve acceptance probes and the
        // ledger ask for one per stack they touch. Static is safe: it holds strings, never a block, and a world has a
        // bounded set of item types.
        static readonly Dictionary<MyItemType, string> keys = new Dictionary<MyItemType, string>();

        public static string Of(MyItemType type)
        {
            string key;
            if (!keys.TryGetValue(type, out key)) keys[type] = key = Category.Key(type.TypeId, type.SubtypeId);
            return key;
        }

        // The reverse trip, for probing a live inventory against a key held as a string (the planner side is
        // game-free and only speaks keys).
        public static MyItemType ToType(string key)
        {
            var slash = key.IndexOf('/');
            return new MyItemType("MyObjectBuilder_" + key.Substring(0, slash), key.Substring(slash + 1));
        }
    }
}
