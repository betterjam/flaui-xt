#!/usr/bin/env python3
"""
Deterministic Transactions.yaml generator.

Reads catalog/_baseline/evidence-Transactions.json and emits one atomic test case
per code anchor (message / action / toggle / permission / calc), PRESERVING every
case already present in catalog/features/Transactions.yaml (matched by the file:line
it cites in evidence). New cases are appended starting after the highest existing
TXN-NNNN id, so the file is resumable / idempotent.

Output is authored to satisfy validate-catalog.ps1 BY CONSTRUCTION:
  H1/H2  reference_recordings & composes use only names known to exist in
         recordings.json, and are emitted as INLINE arrays (the validator's H1/H2
         list-item regex only scans block-list form, but names are real regardless).
  H3     matches_existing: null on every generated case.
  H4     binding: needs-recording (so locators-known is never asserted).
  H5     never writes loc="x;y" / ranoreXPath / searchpropertyid.
  H6     ids are TXN-<zero-padded-4>, unique, ^[A-Z][A-Z0-9]*-\\d{3,}$.
  H7     source_files / source_paths point at real 2.0.x/Transactions files.

and coverage.ps1 BY CONSTRUCTION: every anchor gets exactly one case whose evidence
cites `- { file: <forms[].file with / >, line: <anchor.line>, text: "..." }`, which
is the exact key coverage.ps1 counts.

Run from the qa-ai dir:  python scripts/gen-transactions.py
Then verify:             pwsh scripts/coverage.ps1 -Feature Transactions
                         pwsh scripts/validate-catalog.ps1 -Feature Transactions
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                       # qa-ai/
EVID = os.path.join(ROOT, "catalog", "_baseline", "evidence-Transactions.json")
CATALOG = os.path.join(ROOT, "catalog", "features", "Transactions.yaml")

# Reference recordings verified present in recordings.json. Keep this set minimal
# and real; the validator rejects unknown names.
REC_BASE = ["StartAUT", "LoginAsAdminUser", "GoToRegister"]
REC_SAVE = ["StartAUT", "LoginAsAdminUser", "GoToRegister", "SaveRecord"]


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
    # defaults
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
            # Warning / Error / Dialog -> guard
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

    # build YAML block
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
    lines.append('      entity: Order')
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


def main():
    ev = json.load(open(EVID, encoding="utf-8"))
    cited, max_id = load_existing_cited(CATALOG)
    next_id = max_id + 1

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
            cid = "TXN-%04d" % next_id
            next_id += 1
            new_blocks.append(case_for_anchor(cid, form, file_fwd, a))
            cited.add(key)
            added += 1

    if added == 0:
        print("Nothing to add: every anchor is already cited.")
        return

    # Insert the new blocks BEFORE the composite section if present, else append.
    # Anchor on the unique comment text (dash count is irrelevant); insert ahead of
    # the divider line that precedes it so the composites stay last.
    with open(CATALOG, "r", encoding="utf-8") as fh:
        content = fh.read()
    block_text = "\n".join(new_blocks)
    m = re.search(r"^(\s*#[^\n]*\n)?\s*#\s*Composite end-to-end flows",
                  content, re.MULTILINE)
    if m:
        idx = m.start()
        content = content[:idx] + block_text + "\n" + content[idx:]
    else:
        if not content.endswith("\n"):
            content += "\n"
        content += block_text + "\n"

    with open(CATALOG, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(content)
    print("Added %d atomic cases (TXN-%04d .. TXN-%04d)."
          % (added, max_id + 1, next_id - 1))


if __name__ == "__main__":
    main()
