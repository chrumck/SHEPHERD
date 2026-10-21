using System;
using System.Globalization;

// net48 lacks ModuleInitializerAttribute; the C# compiler recognises it by full name, so we declare our own.
// This lets the culture pin run exactly once, before any test type's code executes.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace IngameScript.Tests
{
    // Space Engineers runs PB scripts under InvariantCulture, so in-game `double.TryParse`/`ToString` use '.' as
    // the decimal separator regardless of the server's locale — the script relies on that (Quota.TryParse, the
    // "0.000" ms formats). The offline test host uses the OS culture instead (this dev box is pl-PL, ',' decimal),
    // which parses "1.5" as a failure and formats 1.5 as "1,500" — diverging from the game and failing the
    // fractional-quota and profiler-format tests. Pin invariant so the suite matches the in-game runtime on any box.
    internal static class InvariantCultureBootstrap
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Init()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        }
    }
}
