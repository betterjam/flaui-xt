using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FlaUiPoc
{
    /// <summary>
    /// Streams test progress to a JSON file in real time so a web page can render steps as
    /// they happen (running spinner -> steps appear one by one -> finish animation).
    /// Each AddStep rewrites live-run.json with all steps so far. Screenshots are referenced
    /// by filename (served by the dashboard at /report/&lt;file&gt;).
    /// </summary>
    public class LiveLog
    {
        // image = first/representative frame (back-compat for the current dashboard);
        // images = all frames when this step is a burst album (dashboard can render a group).
        public class LiveStep { public int n; public string text; public string image; public List<string> images = new List<string>(); }

        private readonly string _path;
        private readonly List<LiveStep> _steps = new List<LiveStep>();
        private string _title;
        private string _status = "running";   // running | PASS | FAIL
        public bool Debug;                     // true = a DEBUG run (the UI won't auto-follow it to Live)

        public LiveLog(string outDir, string title, string fileName = "live-run.json", bool debug = false)
        {
            Directory.CreateDirectory(outDir);
            // a dedicated file (e.g. flow-capture.json) keeps a long-lived studio session OUT of the
            // test-run channel, so the suite never mistakes it for a running test.
            _path = Path.Combine(outDir, fileName);
            _title = title;
            Debug = debug;        // set BEFORE the first flush so the very first write already carries it
            Flush();
        }

        public int AddStep(string text, string imageFileName)
        {
            var st = new LiveStep { n = _steps.Count + 1, text = text, image = imageFileName };
            if (imageFileName != null) st.images.Add(imageFileName);
            _steps.Add(st);
            Flush();
            return _steps.Count - 1;
        }

        /// <summary>Replace a step's text + image in place (used by retry so the failed image is replaced
        /// by the retry result instead of appending a new step).</summary>
        public void UpdateStep(int index, string text, string imageFileName)
        {
            if (index < 0 || index >= _steps.Count) return;
            var st = _steps[index];
            st.text = text;
            st.image = imageFileName;
            st.images.Clear();
            if (imageFileName != null) st.images.Add(imageFileName);
            Flush();
        }

        /// <summary>A burst album: one step backed by many frames. image = first frame (back-compat),
        /// images = the whole group so the dashboard renders it Messenger-style.</summary>
        public void AddAlbum(string text, IList<string> imageFileNames)
        {
            var st = new LiveStep { n = _steps.Count + 1, text = text };
            if (imageFileNames != null) st.images.AddRange(imageFileNames);
            st.image = st.images.Count > 0 ? st.images[0] : null;
            _steps.Add(st);
            Flush();
        }

        /// <summary>Start a streaming album step and return its index. Use UpdateAlbum(index, ...) to
        /// grow it frame-by-frame so the live dashboard FOLLOWS the burst as it happens (instead of one
        /// grouped step appearing only after the whole flurry finishes).</summary>
        public int BeginAlbum(string text)
        {
            _steps.Add(new LiveStep { n = _steps.Count + 1, text = text });
            Flush();
            return _steps.Count - 1;
        }

        /// <summary>Update an in-progress album step's text + frames in place (keeps its step number).</summary>
        public void UpdateAlbum(int index, string text, IList<string> imageFileNames)
        {
            if (index < 0 || index >= _steps.Count) return;
            var st = _steps[index];
            st.text = text;
            st.images.Clear();
            if (imageFileNames != null) st.images.AddRange(imageFileNames);
            st.image = st.images.Count > 0 ? st.images[0] : null;
            Flush();
        }

        public void Finish(string status) { _status = status; Flush(); }

        /// <summary>Wipe the stream back to empty (for a "clear / start fresh" in the live studio).</summary>
        public void Reset() { _steps.Clear(); _status = "running"; Flush(); }

        /// <summary>Swap a step's image filename (used when a picture is re-saved with a new highlight, so
        /// the browser fetches the NEW url instead of showing the cached old one).</summary>
        public void ReplaceImage(string oldFile, string newFile)
        {
            bool changed = false;
            foreach (var s in _steps)
            {
                if (s.image == oldFile) { s.image = newFile; changed = true; }
                for (int i = 0; i < s.images.Count; i++) if (s.images[i] == oldFile) { s.images[i] = newFile; changed = true; }
            }
            if (changed) Flush();
        }

        private void Flush()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"title\":\"{J(_title)}\",");
            sb.Append($"\"status\":\"{J(_status)}\",");
            sb.Append($"\"debug\":{(Debug ? "true" : "false")},");
            sb.Append($"\"updated\":\"{DateTime.Now:HH:mm:ss}\",");
            sb.Append("\"steps\":[");
            for (int i = 0; i < _steps.Count; i++)
            {
                var s = _steps[i];
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"n\":{s.n},\"text\":\"{J(s.text)}\",\"image\":\"{J(s.image)}\",\"images\":[");
                for (int k = 0; k < s.images.Count; k++)
                {
                    if (k > 0) sb.Append(",");
                    sb.Append($"\"{J(s.images[k])}\"");
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            // best-effort atomic-ish write; the page tolerates a transient partial read
            try { File.WriteAllText(_path, sb.ToString(), Encoding.UTF8); } catch { }
        }

        private static string J(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }
}
