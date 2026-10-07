using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FlaUiPoc
{
    /// <summary>
    /// Self-contained HTML report generator — no external dependencies (ExtentReports 5.x
    /// conflicts with net472). Screenshots are embedded as base64, producing a single
    /// stand-alone .html that opens in any browser.
    /// </summary>
    public class ReportBuilder
    {
        public class Shot { public string Caption; public string PngPath; }
        // A Step holds one OR MANY shots. One shot => a single thumbnail (as before). Many shots =>
        // a "burst album" rendered as a click-to-expand mosaic (see Write()).
        public class Step { public string Text; public List<Shot> Shots = new List<Shot>(); }
        public class Case { public string Name; public string Status = "PASS"; public List<Step> Steps = new List<Step>(); }

        private readonly List<Case> _cases = new List<Case>();
        private Case _current;

        public void NewCase(string name) { _current = new Case { Name = name }; _cases.Add(_current); }

        public void AddStep(string text, string png)
        {
            var st = new Step { Text = text };
            if (png != null) st.Shots.Add(new Shot { PngPath = png });
            _current?.Steps.Add(st);
        }

        /// <summary>Add a single step backed by MANY frames (a burst album). The report shows a small
        /// mosaic that expands to the full gallery on click. <paramref name="captions"/> is parallel to
        /// <paramref name="pngs"/> (a short label per frame); shorter/empty captions are tolerated.</summary>
        public void AddAlbum(string text, IList<string> captions, IList<string> pngs)
        {
            var st = new Step { Text = text };
            for (int i = 0; i < pngs.Count; i++)
                st.Shots.Add(new Shot { Caption = i < (captions?.Count ?? 0) ? captions[i] : "", PngPath = pngs[i] });
            _current?.Steps.Add(st);
        }

        public void SetStatus(string status) { if (_current != null) _current.Status = status; }

        public string Write(string outDir, string title, string fileName = "navigation-report.html")
        {
            int pass = 0, fail = 0;
            foreach (var c in _cases) { if (c.Status == "PASS") pass++; else fail++; }

            var sb = new StringBuilder();
            sb.Append($@"<!doctype html><html><head><meta charset='utf-8'><title>{title}</title>
<style>
 body{{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#0f172a;color:#e2e8f0}}
 header{{background:#1e293b;padding:24px 32px;border-bottom:3px solid #38bdf8}}
 h1{{margin:0;font-size:22px}} .sub{{color:#94a3b8;font-size:13px;margin-top:4px}}
 .cards{{display:flex;gap:16px;padding:24px 32px}}
 .card{{background:#1e293b;border-radius:10px;padding:16px 24px;min-width:90px;text-align:center}}
 .card .n{{font-size:28px;font-weight:700}} .pass{{color:#4ade80}} .fail{{color:#f87171}}
 .case{{margin:0 32px 24px;background:#1e293b;border-radius:10px;overflow:hidden}}
 .case h2{{margin:0;padding:14px 20px;font-size:16px;background:#334155;display:flex;justify-content:space-between}}
 .badge{{padding:2px 10px;border-radius:20px;font-size:12px}}
 .b-pass{{background:#166534;color:#bbf7d0}} .b-fail{{background:#991b1b;color:#fecaca}}
 .verdict{{padding:10px 20px;color:#cbd5e1;font-size:13px;font-style:italic;border-bottom:1px solid #334155}}
 .step{{padding:16px 20px;border-top:1px solid #334155}}
 .step .t{{font-size:15px;line-height:1.5;margin-bottom:10px}}
 .step .num{{display:inline-block;background:#38bdf8;color:#0f172a;font-weight:700;font-size:12px;padding:2px 10px;border-radius:6px;margin-right:10px}}
 .step img{{max-width:520px;border:1px solid #475569;border-radius:6px;cursor:zoom-in;display:block}}
 /* pure-CSS lightbox: click a thumbnail to expand it fullscreen, click overlay to close */
 .lb{{display:none}}
 .lb:target{{display:flex;position:fixed;inset:0;background:rgba(0,0,0,.9);z-index:99;align-items:center;justify-content:center;cursor:zoom-out}}
 .lb img{{max-width:95vw;max-height:95vh;border:2px solid #38bdf8;border-radius:8px}}
 .lb .x{{position:fixed;top:18px;right:28px;color:#fff;font-size:34px;text-decoration:none;z-index:101}}
 /* burst album: Messenger-style mosaic that expands to a scrollable gallery */
 .album{{display:inline-block;text-decoration:none;cursor:zoom-in}}
 .mosaic{{display:grid;grid-template-columns:1fr 1fr;gap:3px;width:264px;border-radius:8px;overflow:hidden;border:1px solid #475569}}
 .mosaic .cell{{position:relative;height:96px;background:#0b1220;overflow:hidden}}
 .mosaic .cell img{{width:100%;height:100%;object-fit:cover;border:0;border-radius:0;display:block;cursor:zoom-in}}
 .mosaic .cell.solo{{grid-column:1/3;height:150px}}
 .mosaic .more{{position:absolute;inset:0;background:rgba(2,6,23,.66);display:flex;align-items:center;justify-content:center;color:#e2e8f0;font-size:22px;font-weight:700}}
 .album-label{{font-size:12px;color:#7dd3fc;margin-top:6px}}
 .lb-bg{{position:absolute;inset:0;cursor:zoom-out;z-index:0}}
 .lb .gallery{{position:relative;z-index:1;display:flex;flex-direction:column;gap:18px;max-height:95vh;overflow-y:auto;padding:24px;align-items:center}}
 .lb .gallery figure{{margin:0;text-align:center}}
 .lb .gallery img{{max-width:90vw;max-height:80vh;border:2px solid #38bdf8;border-radius:8px}}
 .lb .gallery figcaption{{color:#cbd5e1;font-size:13px;margin-top:6px;font-family:Consolas,monospace}}
 .back{{display:inline-block;margin-bottom:10px;color:#38bdf8;text-decoration:none;font-size:14px}}
 .back:hover{{text-decoration:underline}}
</style></head><body>
<header><a href='/' class='back'>&larr; Back to all tests</a><h1>{title}</h1><div class='sub'>Exatouch POS &middot; FlaUI (open source) + NUnit &middot; {DateTime.Now:yyyy-MM-dd HH:mm}</div></header>
<div class='cards'>
 <div class='card'><div class='n'>{_cases.Count}</div>Total</div>
 <div class='card'><div class='n pass'>{pass}</div>Passed</div>
 <div class='card'><div class='n fail'>{fail}</div>Failed</div>
</div>");

            foreach (var c in _cases)
            {
                var badge = c.Status == "PASS" ? "b-pass" : "b-fail";
                var verdict = c.Status == "PASS" ? "All steps completed successfully." : "The test stopped at a failed step.";
                sb.Append($"<div class='case'><h2>{Esc(c.Name)}<span class='badge {badge}'>{c.Status}</span></h2>");
                sb.Append($"<div class='verdict'>{verdict}</div>");
                int n = 0;
                foreach (var s in c.Steps)
                {
                    n++;
                    sb.Append($"<div class='step'><div class='t'><span class='num'>Step {n}</span>{Esc(s.Text)}</div>");

                    var shots = new List<Shot>();
                    foreach (var sh in s.Shots) if (sh.PngPath != null && File.Exists(sh.PngPath)) shots.Add(sh);

                    if (shots.Count == 1)
                    {
                        // single frame: thumbnail -> fullscreen lightbox (closes by linking back to '#')
                        var id = "img" + Guid.NewGuid().ToString("N");
                        var src = DataUri(shots[0].PngPath);
                        sb.Append($"<a href='#{id}'><img src='{src}' title='Click to enlarge'></a>");
                        sb.Append($"<a href='#' class='lb' id='{id}'><span class='x'>&times;</span><img src='{src}'></a>");
                    }
                    else if (shots.Count > 1)
                    {
                        // burst album: Messenger-style mosaic that expands to a scrollable gallery
                        var albId = "alb" + Guid.NewGuid().ToString("N");
                        int count = shots.Count, visible = Math.Min(count, 4);
                        sb.Append($"<a href='#{albId}' class='album' title='Click to view all {count} frames'><div class='mosaic'>");
                        for (int i = 0; i < visible; i++)
                        {
                            bool solo = (count == 3 && i == 0);     // 3 frames: big top + two below
                            bool more = (i == visible - 1 && count > 4);
                            sb.Append($"<div class='cell{(solo ? " solo" : "")}'>");
                            sb.Append($"<img src='{DataUri(shots[i].PngPath)}'>");
                            if (more) sb.Append($"<div class='more'>+{count - 4}</div>");
                            sb.Append("</div>");
                        }
                        sb.Append("</div>");
                        sb.Append($"<div class='album-label'>&#128247; {count} frames &middot; click to view all</div></a>");
                        // gallery lightbox: backdrop/x close it; clicking a frame does NOT
                        sb.Append($"<div class='lb' id='{albId}'><a href='#' class='lb-bg'></a><a href='#' class='x'>&times;</a><div class='gallery'>");
                        foreach (var sh in shots)
                        {
                            sb.Append("<figure>");
                            sb.Append($"<img src='{DataUri(sh.PngPath)}'>");
                            if (!string.IsNullOrEmpty(sh.Caption)) sb.Append($"<figcaption>{Esc(sh.Caption)}</figcaption>");
                            sb.Append("</figure>");
                        }
                        sb.Append("</div></div>");
                    }
                    sb.Append("</div>");
                }
                sb.Append("</div>");
            }
            sb.Append("</body></html>");

            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, fileName);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // Embed a PNG as a base64 data URI so the report stays a single stand-alone .html.
        private static string DataUri(string pngPath)
        {
            try { return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(pngPath)); }
            catch { return null; }
        }
    }
}
