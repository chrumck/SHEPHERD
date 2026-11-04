using System;
using System.Collections.Generic;

namespace IngameScript
{
    // Category tokens stay raw: validating them is Category/Routing's job. A RESERVE pool name stays verbatim, since
    // it must match its Custom Data section name exactly.
    public class TagInfo
    {
        public bool HasTag;
        public bool IsStatusLcd;
        public bool IsDebugLcd;
        public bool IsLogLcd;
        public bool IsOff;
        public bool IsSplit;
        public bool IsMain;
        public bool IsCoop;
        public bool IsDestination;
        public bool IsCatchAll;
        public bool IsReserve;
        public string ReservePool;
        public int Priority;
        public readonly List<string> Categories = new List<string>();
    }

    public static class Tags
    {
        const string Keyword = "SHEPHERD";

        // Shared by every untagged block so the scan allocates nothing for them; callers only read it.
        public static readonly TagInfo None = new TagInfo();

        public static TagInfo Parse(string blockName)
        {
            if (string.IsNullOrEmpty(blockName) || blockName.IndexOf('[') < 0) return None;

            var info = new TagInfo();
            var i = 0;
            while (i < blockName.Length)
            {
                var open = blockName.IndexOf('[', i);
                if (open < 0) break;
                var close = blockName.IndexOf(']', open + 1);
                if (close < 0) break;
                Apply(info, blockName.Cut(open + 1, close - open - 1));
                i = close + 1;
            }
            return info.HasTag ? info : None;
        }

        static void Apply(TagInfo info, string inner)
        {
            if (!inner.StartsAnyCase(Keyword)) return;

            var rest = inner.Cut(Keyword.Length);
            if (rest.Length == 0)
            {
                info.HasTag = true;
                info.IsStatusLcd = true;
                return;
            }

            var sep = rest[0];
            if (sep == ':')
            {
                info.HasTag = true;
                info.IsDestination = true;
                ParsePayload(info, rest.Cut(1));
            }
            else if (sep == '-')
            {
                var scope = rest.Cut(1);
                if (scope.Is("Off"))
                {
                    info.HasTag = true;
                    info.IsOff = true;
                }
                else if (scope.Is("Split"))
                {
                    info.HasTag = true;
                    info.IsSplit = true;
                }
                else if (scope.Is("Main"))
                {
                    info.HasTag = true;
                    info.IsMain = true;
                }
                else if (scope.Is("Coop"))
                {
                    info.HasTag = true;
                    info.IsCoop = true;
                }
                else if (scope.Is("DEBUG"))
                {
                    info.HasTag = true;
                    info.IsDebugLcd = true;
                }
                else if (scope.Is("LOG"))
                {
                    info.HasTag = true;
                    info.IsLogLcd = true;
                }
                else if (scope.StartsAnyCase("RESERVE"))
                {
                    // Any suffix but ':' (RESERVED, RESERVEX) is a prefix collision, not a reserve tag.
                    var after = scope.Cut("RESERVE".Length);
                    if (after.Length == 0)
                    {
                        info.HasTag = true;
                        info.IsReserve = true;
                        info.ReservePool = "";
                    }
                    else if (after[0] == ':')
                    {
                        info.HasTag = true;
                        info.IsReserve = true;
                        info.ReservePool = after.Cut(1);
                    }
                }
            }
        }

        static void ParsePayload(TagInfo info, string payload)
        {
            // Category tokens never contain ':', so a final ":#N" detaches cleanly; a final segment that isn't #N
            // stays a category token.
            var lastColon = payload.LastIndexOf(':');
            if (lastColon >= 0)
            {
                int priority;
                if (TryParsePriority(payload.Cut(lastColon + 1), out priority))
                {
                    info.Priority = priority;
                    payload = payload.Cut(0, lastColon);
                }
            }

            foreach (var raw in payload.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                if (token == "*") { info.IsCatchAll = true; continue; }
                info.Categories.Push(token);
            }
        }

        static bool TryParsePriority(string segment, out int value)
        {
            value = 0;
            var s = segment.Trim();
            if (s.Length < 2 || s[0] != '#') return false;
            return int.TryParse(s.Cut(1), out value);
        }
    }
}
