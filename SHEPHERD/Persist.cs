using System;
using System.Collections.Generic;

namespace IngameScript
{
    // Keeps ConfigStore's merge game-free and offline-testable.
    public interface IIniDocument
    {
        bool TryParse(string text, out string error);
        void SectionNames(List<string> into);
        void KeyNames(string section, List<string> into);
        string Get(string section, string key); // null when the key is absent
        void Set(string section, string key, string value);
        void DeleteKey(string section, string key);
        string ToText();
    }

    // The only writer of Me.CustomData. A flush rewrites only the managed sections from the in-memory model; the
    // user's own sections, keys and comments stay untouched.
    public partial class ConfigStore
    {
        readonly IIniDocument doc;
        readonly HashSet<string> managed;
        readonly List<string> sectionScratch = new List<string>();
        readonly List<string> keyScratch = new List<string>();

        public ConfigStore(IIniDocument document, params string[] managedSections)
        {
            doc = document;
            managed = new HashSet<string>(managedSections);
        }

        public void SectionNames(List<string> into) => doc.SectionNames(into);

        // Called at load only, so a fresh list is fine.
        public List<KeyValuePair<string, string>> ReadSection(string section)
        {
            var into = new List<KeyValuePair<string, string>>();
            keyScratch.Empty();
            doc.KeyNames(section, keyScratch);
            foreach (var key in keyScratch)
                into.Push(new KeyValuePair<string, string>(key, Value(section, key)));
            return into;
        }

        // MyIni keeps an inline comment (key=value ; note) as part of the value; no SHEPHERD value contains ';'.
        string Value(string section, string key) => StripInlineComment(doc.Get(section, key));

        public static string StripInlineComment(string value)
        {
            if (value == null) return null;
            var semi = value.IndexOf(';');
            return (semi >= 0 ? value.Cut(0, semi) : value).Trim();
        }

        // The model is authoritative: a hand edit to a managed value takes effect by configReload re-seeding the
        // model, not by surviving a flush.
        public void MergeSection(string section, IEnumerable<KeyValuePair<string, string>> entries)
        {
            var wanted = new HashSet<string>();
            foreach (var e in entries) { doc.Set(section, e.Key, e.Value); wanted.Push(e.Key); }

            keyScratch.Empty();
            doc.KeyNames(section, keyScratch);
            foreach (var key in keyScratch)
                if (!wanted.Contains(key)) doc.DeleteKey(section, key);
        }

        // Reuses a line written with the MyObjectBuilder_ prefix, so learning never leaves two lines for one item. Two
        // lookups, not a section walk: a learn save writes every line of a pool.
        public void SetItem(string section, string key, string value)
        {
            var prefixed = Category.BuilderPrefix + key;
            doc.Set(section, doc.Get(section, key) == null && doc.Get(section, prefixed) != null ? prefixed : key, value);
        }

        // Existing lines stay as written, a bad value included: it is logged and the user's to fix.
        public bool AddMissing(string section, List<KeyValuePair<string, string>> entries)
        {
            keyScratch.Empty();
            doc.KeyNames(section, keyScratch);
            var added = false;
            foreach (var e in entries)
                if (!HasKeyIgnoringCase(e.Key)) { doc.Set(section, e.Key, e.Value); added = true; }
            return added;
        }

        // MyIni matches keys case-insensitively, so a Set for a key the user wrote in other casing would overwrite
        // their line, not add one.
        bool HasKeyIgnoringCase(string key)
        {
            foreach (var k in keyScratch)
                if (k.Is(key)) return true;
            return false;
        }

        public string ToText() => doc.ToText();

        // Keys sorted so an unchanged set gives the same signature and the write is skipped.
        public string Signature()
        {
            sectionScratch.Empty();
            doc.SectionNames(sectionScratch);
            var sb = new System.Text.StringBuilder();
            foreach (var section in sectionScratch)
            {
                if (!managed.Contains(section)) continue;
                sb.Put('[').Put(section).Put(']');
                keyScratch.Empty();
                doc.KeyNames(section, keyScratch);
                keyScratch.SortOrdinal();
                foreach (var key in keyScratch)
                    sb.Put(key).Put('=').Put(Value(section, key)).Put(';');
            }
            return sb.ToString();
        }
    }
}
