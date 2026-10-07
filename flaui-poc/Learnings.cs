using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FlaUiPoc
{
    /// <summary>
    /// Self-improving layer: every validation modal the app shows is a RULE we can learn.
    /// This records each modal (message + the screen/context + a timestamp) to a JSON file
    /// that ACCUMULATES across runs (validation-learnings.json). Over time it becomes a map of
    /// the app's validation rules, so the test can pre-correct known-bad inputs instead of
    /// blindly tripping them.
    ///
    /// It also exposes a normalized "rule key" (the message text) so duplicates merge and we
    /// can see how often each rule fired.
    /// </summary>
    public class Learnings
    {
        public class Rule
        {
            public string message;     // the warning text the app showed
            public string context;     // what we were doing (e.g. "Save")
            public string field;       // best guess at the offending field (if known)
            public int seen;           // how many times this rule has fired across all runs
            public string firstSeen;
            public string lastSeen;
        }

        private readonly string _path;
        private List<Rule> _rules = new List<Rule>();

        public Learnings(string outDir)
        {
            _path = Path.Combine(outDir, "validation-learnings.json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var txt = File.ReadAllText(_path);
                // very small hand parser (avoid a JSON dependency); one object per rule
                foreach (var block in txt.Split(new[] { "},{" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var r = new Rule
                    {
                        message = Field(block, "message"),
                        context = Field(block, "context"),
                        field   = Field(block, "field"),
                        firstSeen = Field(block, "firstSeen"),
                        lastSeen  = Field(block, "lastSeen"),
                    };
                    int.TryParse(Field(block, "seen"), out r.seen);
                    if (!string.IsNullOrEmpty(r.message)) _rules.Add(r);
                }
            }
            catch { _rules = new List<Rule>(); }
        }

        /// <summary>Record a modal. Merges with an existing rule of the same message.</summary>
        public void Record(string message, string context, string field, string stamp)
        {
            var existing = _rules.FirstOrDefault(r => r.message == message);
            if (existing != null)
            {
                existing.seen++;
                existing.lastSeen = stamp;
                if (string.IsNullOrEmpty(existing.field) && !string.IsNullOrEmpty(field)) existing.field = field;
            }
            else
            {
                _rules.Add(new Rule { message = message, context = context, field = field, seen = 1, firstSeen = stamp, lastSeen = stamp });
            }
            Save();
        }

        /// <summary>Known field names the app rejects (so the test can pre-correct them).</summary>
        public HashSet<string> KnownBadFields() =>
            new HashSet<string>(_rules.Where(r => !string.IsNullOrEmpty(r.field)).Select(r => r.field));

        public int RuleCount => _rules.Count;

        private void Save()
        {
            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < _rules.Count; i++)
            {
                var r = _rules[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"message\":\"{J(r.message)}\",\"context\":\"{J(r.context)}\",\"field\":\"{J(r.field)}\",");
                sb.Append($"\"seen\":{r.seen},\"firstSeen\":\"{J(r.firstSeen)}\",\"lastSeen\":\"{J(r.lastSeen)}\"");
                sb.Append("}");
            }
            sb.Append("]");
            try { File.WriteAllText(_path, sb.ToString(), Encoding.UTF8); } catch { }
        }

        private static string Field(string block, string key)
        {
            var idx = block.IndexOf("\"" + key + "\":");
            if (idx < 0) return "";
            var rest = block.Substring(idx + key.Length + 3).TrimStart();
            if (rest.StartsWith("\""))
            {
                var end = rest.IndexOf('"', 1);
                return end > 0 ? rest.Substring(1, end - 1) : "";
            }
            var stop = rest.IndexOfAny(new[] { ',', '}' });
            return stop > 0 ? rest.Substring(0, stop).Trim() : "";
        }
        private static string J(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }
}
