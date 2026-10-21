using System;
using System.Collections.Generic;

namespace IngameScript
{
    // The minimal MyIni surface the persistence merge needs, behind an interface so the merge algorithm
    // (ConfigStore) stays game-free and offline-testable. The live implementation wraps MyIni (Program).
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

    // The one component allowed to write Me.CustomData, made game-free by delegating all ini I/O to an
    // IIniDocument. It owns the set of SHEPHERD-managed section names; a flush rewrites only those sections from the
    // in-memory model (in-memory authoritative) and never touches the user's own sections, keys, or comments
    // (merge-not-clobber). Reads (reserve quotas, category overrides) go through ReadSection.
    public sealed partial class ConfigStore
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

        // The key->value pairs of a section (empty when the section is absent). Called at load only, so a fresh
        // list is fine — never on a per-cycle path.
        public List<KeyValuePair<string, string>> ReadSection(string section)
        {
            var into = new List<KeyValuePair<string, string>>();
            keyScratch.Clear();
            doc.KeyNames(section, keyScratch);
            foreach (var key in keyScratch)
                into.Add(new KeyValuePair<string, string>(key, Value(section, key)));
            return into;
        }

        // MyIni keeps a trailing inline comment (key=value ; note) as part of the value, so strip it before a reader
        // parses it — no SHEPHERD value (quota numbers, category tokens) contains ';'. The user's comment survives in
        // the file untouched: it is only cleaned out of the value we parse, never rewritten (reserve is unmanaged; a
        // comment inside a managed section is dropped on the next rewrite, per in-memory-authoritative merge).
        string Value(string section, string key) => StripInlineComment(doc.Get(section, key));

        public static string StripInlineComment(string value)
        {
            if (value == null) return null;
            var semi = value.IndexOf(';');
            return (semi >= 0 ? value.Substring(0, semi) : value).Trim();
        }

        // Rewrite one managed section wholesale from the model: set every entry, then delete any managed-section key
        // no longer present (in-memory authoritative — a user edit to a managed value takes effect via configReload
        // re-seeding the model, not by surviving a flush). Only this section is touched, so unmanaged content lives.
        public void MergeSection(string section, IEnumerable<KeyValuePair<string, string>> entries)
        {
            var wanted = new HashSet<string>();
            foreach (var e in entries) { doc.Set(section, e.Key, e.Value); wanted.Add(e.Key); }

            keyScratch.Clear();
            doc.KeyNames(section, keyScratch);
            foreach (var key in keyScratch)
                if (!wanted.Contains(key)) doc.DeleteKey(section, key);
        }

        // Write one item's value into a user-owned section: onto the line already holding that item, even one written
        // with the MyObjectBuilder_ prefix, else as a new line — so learning never leaves two lines for one item.
        public void SetItem(string section, string key, string value)
        {
            keyScratch.Clear();
            doc.KeyNames(section, keyScratch);
            foreach (var k in keyScratch)
                if (Category.ShortTypeId(k) == key) { doc.Set(section, k, value); return; }
            doc.Set(section, key, value);
        }

        // Add each entry whose key the section lacks, leaving every line already there as written — a bad value
        // included, since it is logged and the user's to fix. Reports whether anything was added.
        public bool AddMissing(string section, List<KeyValuePair<string, string>> entries)
        {
            keyScratch.Clear();
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
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string ToText() => doc.ToText();

        // A canonical string of every managed section's entries (sections in document order, keys sorted) so an
        // unchanged set re-flushes to the same signature and the write can be skipped. Built only on flush (rare).
        public string Signature()
        {
            sectionScratch.Clear();
            doc.SectionNames(sectionScratch);
            var sb = new System.Text.StringBuilder();
            foreach (var section in sectionScratch)
            {
                if (!managed.Contains(section)) continue;
                sb.Append('[').Append(section).Append(']');
                keyScratch.Clear();
                doc.KeyNames(section, keyScratch);
                keyScratch.Sort(System.StringComparer.Ordinal);
                foreach (var key in keyScratch)
                    sb.Append(key).Append('=').Append(Value(section, key)).Append(';');
            }
            return sb.ToString();
        }
    }
}
