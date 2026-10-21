using System.Collections.Generic;
using Sandbox.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame;

namespace IngameScript
{
    public sealed partial class CategoryOverrides
    {
        public int Count => map.Count;
    }

    public sealed partial class ConfigStore
    {
        public bool DirtySince(string signature) => Signature() != signature;
    }

    public sealed partial class SortJob
    {
        public SortJob(List<ISortSource> sources, RoutingIndex routing, int examineBudgetPerStep)
            : this(sources, routing, examineBudgetPerStep, 0, new List<string>(), new List<UnreachableRoute>()) { }
    }

    public sealed partial class ClassifyCursor
    {
        public bool HasMore => blocks != null && index < blocks.Count;
    }

    public sealed partial class MessageLog
    {
        public int Count => lines.Count;
    }

    public sealed partial class ReservePool
    {
        public void SetCount(string key, double count) => counts[key] = count;
    }

    public static partial class Scan
    {
        public static ScanResult Classify(IEnumerable<IMyTerminalBlock> blocks, ManagedScope scope)
        {
            var result = new ScanResult();
            if (blocks != null)
                foreach (var block in blocks)
                    ClassifyOne(block, scope, result);
            return result;
        }
    }

    public sealed partial class ManagedScope
    {
        // A scope of the home construct alone, owned by `owner`.
        public static ManagedScope Home(IMyCubeGrid home, long owner = 0)
        {
            var scope = new ManagedScope();
            scope.Build(new List<IMyShipConnector>(), home, owner, false, false, false);
            return scope;
        }
    }
}
