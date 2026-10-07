#!/usr/bin/env python3
"""Generate qa-ai/catalog/features/Integrations.yaml from the deterministic anchor
list in catalog/_baseline/evidence-Integrations.json.

One atomic case per code anchor (message/action/toggle/permission/calc); a composite
per form chaining that form's save-path atomics. Mirrors the established Settings.yaml
template exactly so validate-catalog.ps1 and coverage.ps1 both pass.

Rules honored:
  - evidence cites file:line verbatim from the anchor list (coverage matches file:line)
  - binding: needs-recording  -> no reference_recordings constraint beyond existing names
  - composes / reference_recordings use only names known to exist in recordings.json
  - matches_existing: null     (net-new coverage)
  - IDs are stable INT-NNNN, never reused
"""
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EV = os.path.join(ROOT, "catalog", "_baseline", "evidence-Integrations.json")
OUT = os.path.join(ROOT, "catalog", "features", "Integrations.yaml")

# Building blocks confirmed to exist in recordings.json
COMPOSES = "[StartAUT, LoginAsAdminUser, GoToModule, CloseAUT]"
REFREC = "[StartAUT, LoginAsAdminUser, GoToModule]"
COMPOSES_PERM = "[StartAUT, LoginAsRegularUser, GoToModule, CloseAUT]"
REFREC_PERM = "[StartAUT, LoginAsRegularUser, GoToModule]"


def yaml_str(s):
    """Quote a string for a YAML double-quoted scalar (used in title/expected/text)."""
    if s is None:
        s = ""
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def fwd(path):
    return path.replace("\\", "/")


def entity_for(subdir):
    return {
        "FloorPlan": "FloorPlan",
        "Kiosk": "Loyalty",
        "LCB": "LCB",
        "LMP": "LMP",
        "Messages": "Message",
        "ScanData": "ScanData",
    }.get(subdir, "Integration")


def subdir_of(filepath):
    # 2.0.x\Integrations\<Subdir>\file.cs
    parts = filepath.replace("/", "\\").split("\\")
    try:
        i = parts.index("Integrations")
        return parts[i + 1]
    except (ValueError, IndexError):
        return "Integration"


def priority_for(anchor):
    t = anchor["type"]
    if t == "message":
        return "P1" if anchor.get("kind") == "Success" else "P2"
    if t == "permission":
        return "P2"
    if t == "toggle":
        return "P2"
    # action
    ctrl = (anchor.get("control") or "").lower()
    if ctrl in ("btnok", "btnsave"):
        return "P1"
    if ctrl in ("btncancel",):
        return "P3"
    return "P3"


def test_kind_for(anchor):
    t = anchor["type"]
    if t == "message":
        return "happy-path" if anchor.get("kind") == "Success" else "validation"
    if t == "permission":
        return "permission"
    if t == "toggle":
        return "setting-behavior"
    return "happy-path"


def emit_atomic(cid, form, filefwd, subdir, anchor):
    t = anchor["type"]
    ui = form
    entity = entity_for(subdir)
    tk = test_kind_for(anchor)
    prio = priority_for(anchor)
    line = anchor["line"]
    label = anchor.get("label")
    label_sfx = f" ({label})" if label and label != "null" else ""

    lines = []
    A = lines.append
    A(f"  - id: {cid}")

    if t == "message":
        kind = anchor.get("kind")
        text = anchor["text"]
        A(f'    title: "{form}: {kind} - {one_line(text)}"')
        ev_text = text
    elif t == "permission":
        right = anchor["right"]
        ctrl = anchor["control"]
        A(f'    title: "{form}: permission {right} gates {ctrl}"')
        ev_text = f"perm:{right}"
    elif t == "toggle":
        ctrl = anchor["control"]
        A(f'    title: "{form}: toggle {ctrl}{one_line(label_sfx)}"')
        ev_text = anchor["handler"]
    else:  # action
        ctrl = anchor["control"]
        A(f'    title: "{form}: action {ctrl}{one_line(label_sfx)}"')
        ev_text = anchor["handler"]

    A("    kind: atomic")
    A(f"    priority: {prio}")
    A("    type: e2e")
    A("    flavors: []")
    A(f"    categories: {{ test_kind: {tk}, ui_area: {ui}, entity: {entity} }}")

    if t == "permission":
        right = anchor["right"]
        ctrl = anchor["control"]
        A("    preconditions: [Logged in as a user lacking the required access right]")
        A("    steps:")
        A(f'      - "Log in as a user lacking the {right} right"')
        A(f'      - "Open {form} under Integrations"')
        A(f'      - "Attempt to use {ctrl}{label_clause(label)}"')
        A("    expected:")
        A(f'      - "Control {ctrl} is disabled/blocked without the {right} right"')
    elif t == "toggle":
        ctrl = anchor["control"]
        A("    preconditions: [Logged in as admin]")
        A("    steps:")
        A(f'      - "Open {form} under Integrations"')
        A(f'      - "Toggle {ctrl}{label_clause(label)}"')
        A('      - "Observe the dependent behavior the toggle drives"')
        A("    expected:")
        A(f'      - "Toggling {ctrl} changes the dependent state ({anchor["handler"]})"')
    elif t == "message":
        kind = anchor.get("kind")
        text = anchor["text"]
        A("    preconditions: [Logged in as admin]")
        A("    steps:")
        A(f'      - "Open {form} under Integrations"')
        if kind == "Success":
            A('      - "Perform the action that succeeds"')
            A('      - "Observe the confirmation message"')
            A("    expected:")
            A(f'      - "Message shown: {one_line(text)}"')
        else:
            A('      - "Drive the input/state that trips this guard"')
            A('      - "Observe the dialog text"')
            A("    expected:")
            A(f'      - "Dialog text is: {one_line(text)}"')
    else:  # action
        ctrl = anchor["control"]
        A("    preconditions: [Logged in as admin]")
        A("    steps:")
        A(f'      - "Open {form} under Integrations"')
        A(f'      - "Activate {ctrl}{label_clause(label)}"')
        A('      - "Observe the resulting action"')
        A("    expected:")
        A(f'      - "Handler {anchor["handler"]} runs and the expected screen/state change occurs"')

    A("    runner: ranorex")
    if t == "permission":
        A(f"    composes: {COMPOSES_PERM}")
        A(f"    reference_recordings: {REFREC_PERM}")
    else:
        A(f"    composes: {COMPOSES}")
        A(f"    reference_recordings: {REFREC}")
    A("    binding: needs-recording")
    A("    matches_existing: null")
    A(f"    new_controls: [{form} {t} at line {line}]")
    A(f"    source_files: [{filefwd}]")
    A("    evidence:")
    A(f"      - {{ file: {filefwd}, line: {line}, text: {yaml_str(ev_text)} }}")
    A('    flow_hash: ""')
    A("    source_refs: { tickets: [], prs: [] }")
    A("    origin: discovery")
    A("")
    return "\n".join(lines)


def one_line(s):
    """Collapse escaped newlines so titles/messages stay single-line and readable."""
    if s is None:
        return ""
    return (
        s.replace("\\r\\n", " ")
        .replace("\\n", " ")
        .replace("\r", " ")
        .replace("\n", " ")
        .replace('"', "'")
        .strip()
    )


def label_clause(label):
    if label and label != "null":
        return f" ({one_line(label)})"
    return ""


def main():
    ev = json.load(open(EV, encoding="utf-8-sig"))
    out = []
    A = out.append

    A("feature: Integrations")
    A("description: >")
    A("  Integrations module forms under 2.0.x/Integrations: FloorPlan (table/section")
    A("  layout and live status), Kiosk (loyalty check-in/join), LCB and LMP (state")
    A("  tobacco/liquor reporting + Master Service price sync), Messages (email/birthday/")
    A("  reminder campaigns and distribution lists), and ScanData (Altria/RJR scan upload).")
    A("  One atomic case per code anchor (message/action/toggle/permission); composites")
    A("  chain a form's atomics into a realistic open->act->save flow. Locators are")
    A("  referenced from existing recordings, never fabricated.")
    A("source_paths:")
    A("  - 2.0.x/Integrations")
    A("flavors: []")
    A("")
    A("test_cases:")

    n = 0
    composites = []  # (form, filefwd, subdir, [atomic ids that are save-path])
    last_subdir = None

    for frm in ev["forms"]:
        form = frm["form"]
        filefwd = fwd(frm["file"])
        subdir = subdir_of(frm["file"])
        if subdir != last_subdir:
            A("  # " + "=" * 70)
            A(f"  # {subdir}")
            A("  # " + "=" * 70)
            last_subdir = subdir
        A("  # ----------------------------------------------------------------------")
        A(f"  # {filefwd}  ({frm['anchorCount']} anchors)")
        A("  # ----------------------------------------------------------------------")

        save_chain = []
        for anchor in frm["anchors"]:
            n += 1
            cid = f"INT-{n:04d}"
            A(emit_atomic(cid, form, filefwd, subdir, anchor))
            # collect a realistic chain: the OK/Save action + the Success message
            ctrl = (anchor.get("control") or "").lower()
            if anchor["type"] == "action" and ctrl in ("btnok", "btnsave", "btnfinish"):
                save_chain.append(cid)
            elif anchor["type"] == "message" and anchor.get("kind") == "Success":
                save_chain.append(cid)
        if len(save_chain) >= 2:
            composites.append((form, filefwd, subdir, save_chain))

    # composites
    A("  # " + "=" * 70)
    A("  # Composite end-to-end flows (chain a form's save-path atomics)")
    A("  # " + "=" * 70)
    for form, filefwd, subdir, chain in composites:
        n += 1
        cid = f"INT-{n:04d}"
        entity = entity_for(subdir)
        A(f"  - id: {cid}")
        A(f'    title: "End-to-end: open {form}, perform action and confirm save"')
        A("    kind: composite")
        A("    priority: P2")
        A("    type: e2e")
        A("    flavors: []")
        A(f"    categories: {{ test_kind: happy-path, ui_area: {form}, entity: {entity} }}")
        A(f"    chains: [{', '.join(chain)}]")
        A("    preconditions: [Logged in as admin]")
        A('    steps: ["Run the chained atomic cases in order"]')
        A('    expected: ["Each chained case passes; the form\'s open-to-save flow finishes"]')
        A("    runner: ranorex")
        A(f"    composes: {COMPOSES}")
        A(f"    reference_recordings: {REFREC}")
        A("    binding: needs-recording")
        A("    matches_existing: null")
        A("    new_controls: []")
        A(f"    source_files: [{filefwd}]")
        A("    evidence: []")
        A('    flow_hash: ""')
        A("    source_refs: { tickets: [], prs: [] }")
        A("    origin: discovery")
        A("")

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out))
    print(f"Wrote {OUT}: {n} cases ({n - len(composites)} atomic, {len(composites)} composite)")


if __name__ == "__main__":
    main()
