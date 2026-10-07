#!/usr/bin/env python3
"""
Deterministic Reports.yaml generator.

Reads catalog/_baseline/evidence-Reports.json and emits one atomic test case per
code anchor (message / action / toggle / permission / calc), PRESERVING every case
already present in catalog/features/Reports.yaml (matched by the file:line it cites
in evidence). New cases are appended after the highest existing RPT-NNNN id, so the
file is resumable / idempotent. A trailing set of composite open->act->save flows
(one per major form) chains the atomics for realistic end-to-end coverage.

Output is authored to satisfy validate-catalog.ps1 BY CONSTRUCTION:
  H1/H2  reference_recordings & composes use only names known to exist in
         recordings.json, emitted as INLINE arrays.
  H3     matches_existing: null on every generated case.
  H4     binding: needs-recording (locators-known never asserted).
  H5     never writes loc="x;y" / ranoreXPath / searchpropertyid.
  H6     ids are RPT-<zero-padded-4>, unique, ^[A-Z][A-Z0-9]*-\\d{3,}$.
  H7     source_files / source_paths point at real 2.0.x/Reports files.

and coverage.ps1 BY CONSTRUCTION: every anchor gets exactly one case whose evidence
cites `- { file: <forms[].file with / >, line: <anchor.line>, text: "..." }`.

There is no GoToReports recording in the 135-recording inventory; the Reports module
is reached via the generic GoToModule recording.

Run from the qa-ai dir:  python scripts/gen-reports.py
Then verify:             pwsh scripts/coverage.ps1 -Feature Reports
                         pwsh scripts/validate-catalog.ps1 -Feature Reports
"""
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                       # qa-ai/
EVID = os.path.join(ROOT, "catalog", "_baseline", "evidence-Reports.json")
CATALOG = os.path.join(ROOT, "catalog", "features", "Reports.yaml")

# Reference recordings verified present in recordings.json. The Reports module has no
# dedicated GoTo recording, so navigation leans on the generic GoToModule.
REC_BASE = ["StartAUT", "LoginAsAdminUser", "GoToModule"]
REC_SAVE = ["StartAUT", "LoginAsAdminUser", "GoToModule", "SaveRecord"]


def yq(s):
    """Quote a string for a double-quoted YAML scalar (escape \\ and ")."""
    if s is None:
        s = ""
    return s.replace("\\", "\\\\").replace('"', '\\"')


def short(s, n=60):
    s = (s or "").replace("\\r", " ").replace("\\n", " ").replace("\\", "")
    s = re.sub(r"\s+", " ", s).strip()
    return s[:n].strip()


def fwd(file):
    return file.replace("\\", "/")


def load_existing_cited(path):
    """Return (set of 'forwardslashfile:line' already cited, max numeric id suffix)."""
    cited = set()
    max_id = 0
    if not os.path.exists(path):
        return cited, max_id
    in_ev = False
    with open(path, "r", encoding="utf-8") as fh:
        for ln in fh:
            m = re.match(r"^\s*-?\s*id:\s*([A-Za-z0-9_-]+)", ln)
            if m:
                mi = re.match(r"^[A-Z]+-(\d+)$", m.group(1))
                if mi:
                    max_id = max(max_id, int(mi.group(1)))
                in_ev = False
                continue
            if re.match(r"^\s*evidence:\s*$", ln):
                in_ev = True
                continue
            if in_ev:
                em = re.search(r"\{\s*file:\s*(\S+?)\s*,\s*line:\s*(\d+)", ln)
                if em:
                    f = em.group(1).rstrip(",").replace("\\", "/")
                    cited.add("%s:%s" % (f, em.group(2)))
                elif re.match(r"^\s*\w+:", ln):
                    in_ev = False
    return cited, max_id


def case_for_anchor(cid, form, file_fwd, a):
    """Render one atomic YAML case (string) for an anchor dict `a`."""
    t = a["type"]
    line = a["line"]
    ui = form
    priority = "P2"
    test_kind = "validation"
    recs = REC_BASE
    runner = "ranorex"
    ctype = "e2e"
    new_controls = ['%s control at line %d' % (form, line)]

    if t == "message":
        kind = a.get("kind", "")
        text = a.get("text", "")
        ev_text = text
        if kind == "Success":
            test_kind, priority = "happy-path", "P1"
            recs = REC_SAVE
            title = '%s: Success - %s (%d)' % (form, short(text, 40), line)
            steps = ['Open %s and perform the action that succeeds' % form,
                     "Observe the dialog text"]
            expected = ['Dialog text is: %s' % short(text, 80)]
        else:
            test_kind = "error-guard"
            title = '%s: %s - %s (%d)' % (form, kind or "Message", short(text, 36), line)
            steps = ['Open %s and trigger the %s condition' % (form, kind or "message"),
                     "Observe the dialog text"]
            expected = ['Dialog text is: %s' % short(text, 80)]
    elif t == "permission":
        right = a.get("right", "")
        label = a.get("label") or a.get("control", "")
        ev_text = "perm:%s" % right
        test_kind, priority = "permission", "P2"
        title = '%s: Permission - %s (%s)' % (form, short(label, 30), right)
        steps = ['Sign in as a user WITHOUT the %s right' % right,
                 'Attempt the %s action in %s' % (short(label, 30), form),
                 'Then sign in WITH the right and retry']
        expected = ['Without %s the action is blocked' % right,
                    'With %s the action is permitted' % right]
        new_controls = ['%s %s (right %s) at line %d' % (form, a.get("control", ""), right, line)]
    elif t == "toggle":
        handler = a.get("handler", "")
        label = a.get("label") or a.get("control", "")
        ev_text = handler
        test_kind, priority = "setting-behavior", "P2"
        title = '%s: Toggle - %s (%s)' % (form, short(label, 30) or handler, line)
        steps = ['Open %s' % form,
                 'Toggle %s (%s)' % (short(label, 30) or a.get("control", ""), handler),
                 'Observe the dependent behavior change, then toggle back']
        expected = ['Toggling %s changes the dependent behavior; toggling back reverts it'
                    % (short(label, 30) or a.get("control", ""))]
        new_controls = ['%s %s at line %d' % (form, a.get("control", ""), line)]
    elif t == "calc":
        expr = a.get("expr", "")
        ev_text = expr
        test_kind, priority = "calculation", "P1"
        ctype = "integration"
        runner = "nunit"
        recs = []
        title = '%s: Calculation (%d)' % (form, line)
        steps = ['Exercise the code path that computes: %s' % short(expr, 70),
                 'Assert the computed value matches the formula']
        expected = ['The result equals: %s' % short(expr, 80)]
        new_controls = []
    else:  # action
        handler = a.get("handler", "")
        label = a.get("label") or a.get("control", "")
        ev_text = handler
        test_kind, priority = "happy-path", "P3"
        title = '%s: %s action (%s)' % (form, short(label, 30) or handler, handler)
        steps = ['Open %s' % form,
                 'Invoke %s (%s)' % (short(label, 30) or a.get("control", ""), handler)]
        expected = ['%s runs and the action completes' % handler]
        new_controls = ['%s %s at line %d' % (form, a.get("control", ""), line)]

    lines = []
    lines.append('  - id: %s' % cid)
    lines.append('    title: "%s"' % yq(title))
    lines.append('    kind: atomic')
    lines.append('    priority: %s' % priority)
    lines.append('    type: %s' % ctype)
    lines.append('    flavors: []')
    lines.append('    categories:')
    lines.append('      test_kind: %s' % test_kind)
    lines.append('      ui_area: %s' % ui)
    lines.append('      entity: Report')
    lines.append('    preconditions:')
    lines.append('      - Logged in as admin')
    lines.append('    steps:')
    for s in steps:
        lines.append('      - %s' % s)
    lines.append('    expected:')
    for e in expected:
        lines.append('      - "%s"' % yq(e))
    lines.append('    runner: %s' % runner)
    if recs:
        comp = recs + ["CloseAUT"]
        lines.append('    composes: [%s]' % ", ".join(comp))
        lines.append('    reference_recordings: [%s]' % ", ".join(recs))
    else:
        lines.append('    composes: []')
        lines.append('    reference_recordings: []')
    lines.append('    binding: needs-recording')
    lines.append('    matches_existing: null')
    if new_controls:
        lines.append('    new_controls:')
        for nc in new_controls:
            lines.append('      - %s' % nc)
    else:
        lines.append('    new_controls: []')
    lines.append('    source_files:')
    lines.append('      - %s' % file_fwd)
    lines.append('    evidence:')
    lines.append('      - { file: %s, line: %d, text: "%s" }'
                 % (file_fwd, line, yq(ev_text)))
    lines.append('    flow_hash: ""')
    lines.append('    source_refs: { tickets: [], prs: [] }')
    lines.append('    origin: discovery')
    lines.append('')
    return "\n".join(lines)


HEADER = """feature: Reports
description: >
  Reports module forms under 2.0.x/Reports: the report menu and its Customers, Delivery,
  Email/scheduling, Items, Purchasing, Sales, Schedule and Staff report builders, plus the
  shared report viewer (frmReport), printing pipeline (csPrint), and query engine (csReport).
  One atomic case per code anchor (message/action/toggle/permission/calc); composites chain a
  form's atomics into a realistic open->act->run/save flow. The Reports module has no dedicated
  GoTo recording, so navigation references the generic GoToModule recording. Locators are
  referenced from existing recordings, never fabricated.
source_paths:
  - 2.0.x/Reports
flavors: []

test_cases:
"""


def composite(cid, title, form, chains):
    lines = []
    lines.append('  - id: %s' % cid)
    lines.append('    title: "%s"' % yq(title))
    lines.append('    kind: composite')
    lines.append('    priority: P2')
    lines.append('    type: e2e')
    lines.append('    flavors: []')
    lines.append('    categories:')
    lines.append('      test_kind: happy-path')
    lines.append('      ui_area: %s' % form)
    lines.append('      entity: Report')
    lines.append('    chains: [%s]' % ", ".join(chains))
    lines.append('    preconditions:')
    lines.append('      - Logged in as admin')
    lines.append('    steps:')
    lines.append('      - Run the chained atomic cases in order')
    lines.append('    expected:')
    lines.append('      - "Each chained step passes and the end-to-end flow completes"')
    lines.append('    runner: ranorex')
    lines.append('    composes: [StartAUT, LoginAsAdminUser, GoToModule, CloseAUT]')
    lines.append('    reference_recordings: [StartAUT, LoginAsAdminUser, GoToModule]')
    lines.append('    binding: needs-recording')
    lines.append('    matches_existing: null')
    lines.append('    new_controls: []')
    lines.append('    source_files:')
    lines.append('      - 2.0.x/Reports/%s.cs' % form)
    lines.append('    evidence: []')
    lines.append('    flow_hash: ""')
    lines.append('    source_refs: { tickets: [], prs: [] }')
    lines.append('    origin: discovery')
    lines.append('')
    return "\n".join(lines)


def main():
    ev = json.load(open(EVID, encoding="utf-8"))
    fresh = not os.path.exists(CATALOG)
    cited, max_id = load_existing_cited(CATALOG)
    next_id = max_id + 1

    # form -> list of (cid, anchor) so we can build per-form composites afterward
    per_form = {}
    new_blocks = []
    added = 0
    for frm in ev["forms"]:
        form = frm["form"]
        file_fwd = fwd(frm["file"])
        header_emitted = False
        for a in frm["anchors"]:
            key = "%s:%s" % (file_fwd, a["line"])
            if key in cited:
                continue
            if not header_emitted:
                new_blocks.append(
                    "  # ----------------------------------------------------------------------\n"
                    "  # %s  (%s)\n"
                    "  # ----------------------------------------------------------------------"
                    % (form, file_fwd))
                header_emitted = True
            cid = "RPT-%04d" % next_id
            next_id += 1
            new_blocks.append(case_for_anchor(cid, form, file_fwd, a))
            per_form.setdefault(form, []).append((cid, a))
            cited.add(key)
            added += 1

    if added == 0 and not fresh:
        print("Nothing to add: every anchor is already cited.")
        return

    body = "\n".join(new_blocks)

    # Build composites (only when authoring fresh; resumable runs keep existing ids).
    comp_blocks = []
    if fresh:
        comp_id = next_id
        # one composite per form that has both an action and at least one other anchor
        for frm in ev["forms"]:
            form = frm["form"]
            entries = per_form.get(form, [])
            if len(entries) < 2:
                continue
            # chain: prefer an "open/run" action first, then up to 5 representative atomics
            actions = [c for c, a in entries if a["type"] == "action"]
            others = [c for c, a in entries if a["type"] != "action"]
            chain = (actions[:1] + others[:3] + actions[1:3])
            chain = [c for c in chain if c][:5]
            if len(chain) < 2:
                chain = [c for c, _ in entries][:3]
            cid = "RPT-%04d" % comp_id
            comp_id += 1
            comp_blocks.append(composite(
                cid, "%s: end-to-end open->act flow" % form, form, chain))

    if fresh:
        content = HEADER + body + "\n"
        if comp_blocks:
            content += (
                "  # ======================================================================\n"
                "  # Composite end-to-end flows (chain atomic ids; add no new anchors)\n"
                "  # ======================================================================\n")
            content += "\n".join(comp_blocks) + "\n"
        with open(CATALOG, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(content)
        print("Wrote %s: %d atomic cases (RPT-0001 .. RPT-%04d) + %d composites."
              % (os.path.basename(CATALOG), added, next_id - 1, len(comp_blocks)))
        return

    # resumable append before the composite section
    with open(CATALOG, "r", encoding="utf-8") as fh:
        content = fh.read()
    m = re.search(r"^(\s*#[^\n]*\n)?\s*#\s*Composite end-to-end flows",
                  content, re.MULTILINE)
    if m:
        idx = m.start()
        content = content[:idx] + body + "\n" + content[idx:]
    else:
        if not content.endswith("\n"):
            content += "\n"
        content += body + "\n"
    with open(CATALOG, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(content)
    print("Added %d atomic cases (RPT-%04d .. RPT-%04d)."
          % (added, max_id + 1, next_id - 1))


if __name__ == "__main__":
    main()
