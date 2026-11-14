namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Text the server makes for players, in a form each guest can show in its own language.
    ///
    /// A status line is the item's template ("Producing {ElementTypes}: {FlowRate}", in the game's language) with the
    /// {tokens} filled in from its data. When the data can't be sent, the server used to send its finished text: in
    /// the server's language. Now it sends the value of each token instead, and a guest fills its own template.
    /// Values that are names of things (an element, a building or item, a chore type) go as references and a guest
    /// writes its own name for them; numbers and anything else stay as the server wrote them.
    ///
    /// The server checks the split first: its template with the values filled in must give exactly its text (callbacks
    /// that don't work token by token keep sending the whole text).</summary>
    internal static class TextParts
    {
        private const char Ref = '\u0001';
        private const char ListSep = '\u0002';
        private static readonly Regex Token = new Regex(@"\{[^{}\s]+\}", RegexOptions.Compiled);
        private static readonly Regex Positional = new Regex(@"\{\d+(,[^{}]*)?(:[^{}]*)?\}", RegexOptions.Compiled);
        public static long Split, Whole, Refs, Composed, ComposeFallback;

        // ================================================================== server

        /// <summary>The token values of a text made from this template, or null if it wasn't made token by token.</summary>
        public static List<KeyValuePair<string, string>> Decompose(string template, string full, Func<string, string> resolve)
        {
            if (template == null || full == null) return null;
            // "{0}", "{1}": a string.Format template, filled all at once (and the game's callbacks log a warning when
            // handed one such item alone)
            if (Positional.IsMatch(template)) { Whole++; return null; }
            var parts = new List<KeyValuePair<string, string>>();
            string rebuilt = template;
            foreach (Match m in Token.Matches(template))
            {
                string tok = m.Value;
                bool dup = false;
                foreach (var kv in parts) if (kv.Key == tok) dup = true;
                if (dup) continue;
                string v;
                try { v = resolve(tok); }
                catch (Exception) { Whole++; return null; }
                if (v == null) { Whole++; return null; }
                parts.Add(new KeyValuePair<string, string>(tok, v));
                rebuilt = rebuilt.Replace(tok, v);
            }
            if (rebuilt != full) { Whole++; return null; }
            Split++;
            return parts;
        }

        public static void WriteParts(BinaryWriter w, List<KeyValuePair<string, string>> parts)
        {
            if (parts == null) { w.Write((short)-1); return; }
            w.Write((short)parts.Count);
            foreach (var kv in parts) { w.Write(kv.Key); w.Write(EncodeName(kv.Value)); }
        }

        public static List<KeyValuePair<string, string>> ReadParts(BinaryReader r)
        {
            int n = r.ReadInt16();
            if (n < 0) return null;
            var parts = new List<KeyValuePair<string, string>>(n);
            for (int i = 0; i < n; i++) parts.Add(new KeyValuePair<string, string>(Inflate.ReadId(r), DecodeName(Inflate.ReadId(r))));
            return parts;
        }

        private static Dictionary<string, string> nameIndex;

        /// <summary>Names of things in this game's language -> a reference any language can turn back into a name.</summary>
        private static Dictionary<string, string> NameIndex()
        {
            if (nameIndex != null) return nameIndex;
            var idx = new Dictionary<string, string>();
            void Add(string name, string reference)
            {
                if (string.IsNullOrEmpty(name) || idx.ContainsKey(name)) return;
                idx[name] = Ref + reference;
            }
            try
            {
                foreach (Element e in ElementLoader.elements)
                {
                    if (e == null) continue;
                    Add(e.name, "E" + e.tag.Name);
                    Add(e.tag.ProperName(), "e" + e.tag.Name);
                }
            }
            catch (Exception ex) { Log.Warn("text parts: elements: " + ex.Message); }
            try
            {
                foreach (ChoreType c in Db.Get().ChoreTypes.resources) if (c != null) Add(c.Name, "C" + c.Id);
            }
            catch (Exception ex) { Log.Warn("text parts: chore types: " + ex.Message); }
            try
            {
                foreach (KPrefabID p in Assets.Prefabs)
                {
                    if (p == null) continue;
                    Add(p.GetProperName(), "T" + p.PrefabTag.Name);
                }
            }
            catch (Exception ex) { Log.Warn("text parts: prefabs: " + ex.Message); }
            nameIndex = idx;
            Log.Info($"text parts: {idx.Count} names of things can go by reference");
            return idx;
        }

        /// <summary>A name of a thing (or a list of them, ", "-separated) as a reference; anything else as it is.</summary>
        public static string EncodeName(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] == Ref) return s ?? "";
            Dictionary<string, string> idx = NameIndex();
            if (idx.TryGetValue(s, out string r)) { Refs++; return r; }
            if (s.IndexOf(", ", StringComparison.Ordinal) > 0)
            {
                string[] items = s.Split(new[] { ", " }, StringSplitOptions.None);
                var outp = new string[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    if (!idx.TryGetValue(items[i], out string ri)) return s;
                    outp[i] = ri;
                }
                Refs++;
                return Ref + "L" + string.Join(ListSep.ToString(), outp);
            }
            return s;
        }

        // ================================================================== guest

        /// <summary>The reference turned back into this game's name for the thing.</summary>
        public static string DecodeName(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != Ref || s.Length < 2) return s ?? "";
            string id = s.Substring(2);
            try
            {
                switch (s[1])
                {
                    case 'E': { Element e = ElementLoader.GetElement(new Tag(id)); return e != null ? e.name : id; }
                    case 'e': return new Tag(id).ProperName();
                    case 'C': { ChoreType c = Db.Get().ChoreTypes.TryGet(id); return c != null ? c.Name : id; }
                    case 'T': { GameObject p = Assets.TryGetPrefab(new Tag(id)); return p != null ? p.GetProperName() : id; }
                    case 'L':
                    {
                        string[] items = id.Split(ListSep);
                        for (int i = 0; i < items.Length; i++) items[i] = DecodeName(items[i]);
                        return string.Join(", ", items);
                    }
                }
            }
            catch (Exception) { }
            return id;
        }

        /// <summary>This guest's template filled with the server's values; null if its template has a token the server
        /// didn't send (then the server's whole text is shown).</summary>
        public static string Compose(string template, List<KeyValuePair<string, string>> parts)
        {
            if (template == null || parts == null) return null;
            string s = template;
            foreach (Match m in Token.Matches(template))
            {
                bool found = false;
                foreach (var kv in parts) if (kv.Key == m.Value) found = true;
                if (!found) { ComposeFallback++; return null; }
            }
            foreach (var kv in parts) s = s.Replace(kv.Key, kv.Value);
            Composed++;
            return s;
        }

        public static long SameAsServer, DiffersFromServer;

        /// <summary>Test aid: with the same language on both sides, a composed text must equal the server's.</summary>
        public static void Check(string item, string serverName, string name, string serverTip, string tip)
        {
            if (serverName == name && serverTip == tip) { SameAsServer++; return; }
            if (DiffersFromServer++ < 10)
                Log.Info($"text parts: '{item}' composed differently: name '{name}' vs server '{serverName}'; tooltip '{Short(tip)}' vs '{Short(serverTip)}'");
        }

        private static string Short(string s) => s == null ? "" : s.Length > 160 ? s.Substring(0, 160) + "..." : s;

        public static string Report() => $"text: split {Split}, whole {Whole}, names by reference {Refs}, composed here {Composed} (fell back {ComposeFallback}), same as the server's text {SameAsServer}, different {DiffersFromServer}";
    }
}
