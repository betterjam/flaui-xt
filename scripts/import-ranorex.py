#!/usr/bin/env python3
"""
Deterministic Ranorex -> Flow-Capture importer.

Each Ranorex .rxrec recording embeds, per action, the full RxPath of the target control, e.g.
    /form[@controlname='frmMain']/.../form[@controlname='frmMenu']/button[@controlname='btnItems']
Ranorex's @controlname is the WinForms control name == our AutomationId, the RxPath chain gives the
parent container, and the tag gives the control type. So the conversion is 1:1 and deterministic.

For each .rxrec we emit one recording map (recording-<Name>.json) in our flow format:
    [{ n, action, by, key, value, controlType, label, file, index, parent, keyboard }, ...]
Those maps drop straight into the dashboard (Steps & Actions) and regenerate into runnable FlaUI tests
via /api/regenerate - inheriting all the engine resilience (retry, all-windows resolution, etc.).

Usage:  python import-ranorex.py [<ranorex-root>] [<out-dir>]
        defaults: ranorex-root = %EXATOUCH_ROOT%/MyFirstExatouchTest (default C:/Dev/Exatouch), out-dir = ../flaui-poc/TestResults
"""
import os, re, sys, json, glob

# Ranorex RxPath tag -> our UIA ControlType (best-effort; id is the primary selector so type is secondary)
TAG_TO_CT = {
    'button': 'Button', 'text': 'Edit', 'checkbox': 'CheckBox', 'radiobutton': 'RadioButton',
    'menuitem': 'MenuItem', 'list': 'List', 'listitem': 'ListItem', 'table': 'Table',
    'row': 'DataItem', 'cell': 'DataItem', 'combobox': 'ComboBox', 'tab': 'Tab', 'tabpage': 'TabItem',
    'form': 'Window', 'titlebar': 'TitleBar', 'container': 'Pane', 'element': 'Custom', 'link': 'Hyperlink',
}
# containers we treat as a "parent scope" when walking up the RxPath
CONTAINER_TAGS = {'form', 'container', 'tabpage', 'table', 'list', 'group'}

SEG = re.compile(r"/(?P<tag>\w+)(?:\[(?P<pred>[^\]]*)\])?")
CN  = re.compile(r"@controlname='([^']*)'")
CTN = re.compile(r"@controltypename='([^']*)'")

def parse_rxpath(path):
    """-> (leaf_controlname, leaf_tag, parent_controlname). parent = nearest ancestor container w/ controlname."""
    segs = []
    for m in SEG.finditer(path):
        tag = m.group('tag'); pred = m.group('pred') or ''
        cn = CN.search(pred); cn = cn.group(1) if cn else ''
        segs.append((tag, cn))
    # leaf = last segment that has a controlname (skip trailing '?' / element wildcards)
    leaf_i = next((i for i in range(len(segs) - 1, -1, -1) if segs[i][1]), None)
    if leaf_i is None:
        return '', '', ''
    leaf_tag, leaf_cn = segs[leaf_i]
    parent = ''
    for j in range(leaf_i - 1, -1, -1):
        tag, cn = segs[j]
        if cn and tag in CONTAINER_TAGS:
            parent = cn
            break
    return leaf_cn, leaf_tag, parent

PATHTAG = re.compile(r"<path>\s*(.*?)\s*</path>", re.S)
def find_rxpath(block):
    """the DEEPEST <path> RxPath in a recorditem (most @controlname segments). A typing item carries both
    the form snapshot (/form[@controlname='frmMain']) and the real target (.../combobox[@controlname='cboCity'])
    - we want the deepest, which is the actual control, not the container."""
    paths = [re.sub(r"\s+", "", p) for p in PATHTAG.findall(block)]
    paths = [p for p in paths if p.startswith('/form')]
    if not paths:
        return ''
    return max(paths, key=lambda p: p.count('@controlname=') + p.count('@accessiblename='))

def controltext(block):
    """the visible text of the target. Attributes look like:
         <attribute
         name="controltext"
         type="System.String">
             Items
         </attribute>
       so we match name=... then skip the rest of the open tag (incl. the type= line) to the value."""
    for attr in ('controltext', 'windowtext', 'caption', 'text'):
        m = re.search(r'name="%s"[^>]*>\s*([^<]+?)\s*</attribute>' % attr, block)
        if m:
            v = re.sub(r"\s+", " ", m.group(1).strip())
            if v and v not in ('True', 'False'):
                return v
    return ''

def convert(rxrec_path):
    raw = open(rxrec_path, encoding='utf-8-sig').read()
    cls = re.search(r'classname="([^"]+)"', raw)
    cls = cls.group(1) if cls else os.path.splitext(os.path.basename(rxrec_path))[0]
    steps, n = [], 0
    # recorditem starts in document order, and all <path> tags with their positions
    starts = [(m.start(), m.group(1), m.group(2)) for m in
              re.finditer(r"<(mouseitem|keysequenceitem|keyboarditem)\b([^>]*)>", raw)]
    allpaths = [(m.start(), re.sub(r"\s+", "", m.group(1))) for m in PATHTAG.finditer(raw)]
    for i, (pos, kind, attrs) in enumerate(starts):
        end = starts[i + 1][0] if i + 1 < len(starts) else len(raw)
        body = raw[pos:end]
        # deepest <path> that falls inside THIS item's span = the real target control
        cand = [p for (pp, p) in allpaths if pos <= pp < end and p.startswith('/form')]
        if not cand:
            continue
        path = max(cand, key=lambda p: p.count('@controlname=') + p.count('@accessiblename='))
        key, tag, parent = parse_rxpath(path)
        if not key:
            continue
        ct = TAG_TO_CT.get(tag, '')
        name = controltext(body) or key
        if kind == 'mouseitem':
            dbl = 'DoubleClick' in attrs
            action = 'doubleclick' if dbl else 'click'
            label = ('Double-clicked ' if dbl else 'Clicked ') + (tag + ' ' if tag else '') + '"%s"' % name
            value = name
        else:  # keysequenceitem / keyboarditem -> a typed value (text is in KeySequence="...")
            txt = re.search(r'KeySequence="([^"]*)"', attrs) or re.search(r'text="([^"]*)"', attrs)
            value = txt.group(1) if txt else ''
            value = re.sub(r"\{[^}]*\}", "", value).strip()   # drop {Return}/{Tab}/etc.
            if not value:
                continue
            action = 'set'
            label = 'Set "%s" to "%s"' % (name, value)
        n += 1
        steps.append({'n': n, 'action': action, 'by': 'id', 'key': key, 'value': value,
                      'controlType': ct, 'label': label, 'file': '', 'index': -1,
                      'parent': parent, 'keyboard': False})
    return cls, steps

def collect(args, default_root):
    """args may be a mix of .rxrec files and/or directories; else fall back to the default root."""
    files = []
    for a in args:
        if a.lower().endswith('.rxrec') and os.path.isfile(a):
            files.append(a)
        elif os.path.isdir(a):
            files += glob.glob(os.path.join(a, '**', '*.rxrec'), recursive=True)
    if not files:
        files = glob.glob(os.path.join(default_root, '**', '*.rxrec'), recursive=True)
    return sorted(set(files))

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    default_root = os.path.join(os.environ.get('EXATOUCH_ROOT') or r'C:\Dev\Exatouch', 'MyFirstExatouchTest')
    out = os.environ.get('IMPORT_OUT') or os.path.join(here, '..', 'flaui-poc', 'TestResults')
    os.makedirs(out, exist_ok=True)
    files = collect(sys.argv[1:], default_root)
    total_steps, written = 0, 0
    for f in files:
        try:
            cls, steps = convert(f)
        except Exception as e:
            print('SKIP\t%s\t%s' % (os.path.basename(f), e)); continue
        if not steps:
            print('EMPTY\t%s' % os.path.splitext(os.path.basename(f))[0]); continue
        open(os.path.join(out, 'recording-%s.json' % cls), 'w', encoding='utf-8').write(
            json.dumps(steps, ensure_ascii=False))
        print('IMPORTED\t%s\t%d' % (cls, len(steps)))   # machine-readable: name + step count
        total_steps += len(steps); written += 1
    print('DONE\t%d maps\t%d steps' % (written, total_steps))

if __name__ == '__main__':
    main()
