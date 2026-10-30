using System;
using System.Collections.Generic;
using System.Text;

namespace IngameScript
{
    // Full minify renames these to a letter but ships framework member names verbatim, so a call made often enough
    // packs smaller through one of them.
    public static class Shorthand
    {
        public static void Empty<T>(this List<T> l) => l.Clear();
        public static void Empty<K, V>(this Dictionary<K, V> d) => d.Clear();
        public static void Empty<T>(this HashSet<T> s) => s.Clear();
        public static StringBuilder Empty(this StringBuilder b) => b.Clear();
        public static bool Get<K, V>(this Dictionary<K, V> d, K k, out V v) => d.TryGetValue(k, out v);
        public static StringBuilder Put(this StringBuilder b, string s) => b.Append(s);
        public static StringBuilder Put(this StringBuilder b, char c) => b.Append(c);
        public static StringBuilder Put(this StringBuilder b, int i) => b.Append(i);
        public static StringBuilder Line(this StringBuilder b, string s = null) => b.AppendLine(s);
        public static bool Is(this string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        public static bool Starts(this string s, string p) => s.StartsWith(p, StringComparison.Ordinal);
        public static bool StartsAnyCase(this string s, string p) => s.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        public static bool Ends(this string s, string p) => s.EndsWith(p, StringComparison.Ordinal);
        public static void SortOrdinal(this List<string> l) => l.Sort(StringComparer.Ordinal);
        public static void Push<T>(this List<T> l, T v) => l.Add(v);
        public static bool Push<T>(this HashSet<T> l, T v) => l.Add(v);
        public static string Cut(this string s, int i) => s.Substring(i);
        public static string Cut(this string s, int i, int n) => s.Substring(i, n);
    }
}
