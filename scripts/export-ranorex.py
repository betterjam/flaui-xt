#!/usr/bin/env python3
"""
Deterministic Flow-Capture -> Ranorex exporter (no evidence / no screenshots).

Takes one of our recording maps (recording-<Name>.json) and emits a Ranorex test:
  <Name>.rxrep   - the object repository (one item per touched control, with an RxPath locator)
  <Name>.rxrec   - the recording (mouseitem/keyboarditem actions referencing the repo items)

Our step already has the pieces an RxPath needs:
  key (controlname == our id), controlType (-> Ranorex tag), parent (container/form), action, value.
We don't store the FULL ancestor chain, so we use Ranorex's descendant operator '//' - which Ranorex
resolves without every intermediate node:
  /form[@controlname='frmMain']//form[@controlname='frmMenu']//button[@controlname='btnItems']

Deliberately omits <helperscreenshot> (the evidence) - just the actions + locators.

Usage:  python export-ranorex.py <recording-map.json> [<out-dir>]
"""
import os, re, sys, json, uuid

CT_TO_TAG = {  # our UIA ControlType -> (Ranorex tag, capname)
    'Button': ('button', 'button'), 'Edit': ('text', 'text'), 'CheckBox': ('checkbox', 'checkbox'),
    'RadioButton': ('radiobutton', 'radiobutton'), 'MenuItem': ('menuitem', 'menuitem'),
    'ListItem': ('listitem', 'listitem'), 'DataItem': ('row', 'row'), 'ComboBox': ('combobox', 'combobox'),
    'TabItem': ('tabpage', 'tabpage'), 'Tab': ('tab', 'tab'), 'Text': ('text', 'text'),
    'Hyperlink': ('link', 'link'), 'Window': ('form', 'form'),
}
def guid(): return str(uuid.uuid4())

def seg(tag, by, key):
    """one RxPath segment, locating by controlname (our id) or accessiblename/text (our name)."""
    attr = "@controlname='%s'" % key if by == 'id' else "@accessiblename='%s'" % key
    return "%s[%s]" % (tag, attr)

def refpath(step):
    """Faithful RxPath: rooted at frmMain (like Ranorex), descending via '//' through the parent to the
    leaf. frmMain is the app frame; sub-forms (frmMenu/frmRegister) and containers (dgvCat) all live under
    it, so rooting at frmMain round-trips to the same control the Ranorex recording targeted."""
    tag, _ = CT_TO_TAG.get(step.get('controlType', ''), ('element', 'control'))
    parent = step.get('parent') or ''
    leaf = seg(tag, step.get('by', 'id'), step['key'])
    if parent and parent != 'frmMain':
        ptag = 'form' if parent.startswith('frm') else 'container'
        return "/form[@controlname='frmMain']//%s[@controlname='%s']//%s" % (ptag, parent, leaf)
    return "/form[@controlname='frmMain']//%s" % leaf

def xml_escape(s):     # for attribute values (controlname/text) where quotes matter
    return (s or '').replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('"', '&quot;').replace("'", '&apos;')

def path_escape(s):    # for RxPath text - quotes are PART of the syntax (@controlname='x'); keep them literal
    return (s or '').replace('&', '&amp;').replace('<', '&lt;')

def export(map_path, out_dir):
    steps = json.load(open(map_path, encoding='utf-8'))
    name = re.sub(r'^recording-', '', os.path.splitext(os.path.basename(map_path))[0])
    ns = 'MyFirstExatouchTest'

    # ---- build repository items (dedupe identical controls) ----
    items, by_key = [], {}
    for s in steps:
        if s['action'] == 'picture':
            continue
        rp = refpath(s)
        if rp in by_key:
            s['_itemid'] = by_key[rp]['id']; continue
        tag, cap = CT_TO_TAG.get(s.get('controlType', ''), ('element', 'control'))
        itm = {'id': guid(), 'name': re.sub(r'\W', '', (s.get('value') or s['key']).title())[:40] or 'Item',
               'cap': cap, 'refpath': rp}
        by_key[rp] = itm; items.append(itm); s['_itemid'] = itm['id']

    rep_items = "\n".join(
        '\t\t\t<item addcaps="%s,control,nativewindow" capname="%s" id="%s" name="%s" refpath="%s" searchtimeout="30000ms">\n\t\t\t\t%s\n\t\t\t</item>'
        % (it['cap'], it['cap'], it['id'], xml_escape(it['name']), path_escape(it['refpath']), path_escape(it['refpath']))
        for it in items)

    rxrep = ('﻿<repository id="%s" name="%sRepository">\n'
             '\t<codegen classname="%sRepository" namespace="%s"></codegen>\n'
             '\t<rootfolder capname="unknown" id="%s" isrooted="True" name="Root" searchtimeout="0ms" usecache="True">\n'
             '\t\t<appfolder addcaps="form,control,nativewindow" capname="form" id="%s" isrooted="True" name="FrmMain" '
             'refpath="/form[@controlname=\'frmMain\']" searchtimeout="30000ms" usecache="True">\n%s\n\t\t</appfolder>\n'
             '\t</rootfolder>\n</repository>\n') % (guid(), name, name, ns, guid(), guid(), rep_items)

    # ---- build recorditems (no <helperscreenshot>) ----
    rec = []
    for s in steps:
        if s['action'] == 'picture':
            continue
        rp = refpath(s)
        if s['action'] in ('click', 'doubleclick'):
            act = 'DoubleClick' if s['action'] == 'doubleclick' else 'Click'
            rec.append(
                '\t\t<mouseitem action="%s" button="Left" duration="300ms" enabled="True" loc="50;50" '
                'movetime="300ms" optional="False" searchpropertyid="%s" warnoutofbounds="False">\n'
                '\t\t\t<comment></comment>\n\t\t\t<bindings></bindings>\n'
                '\t\t\t<info id="%s" sourcename="Recorder">\n\t\t\t\t<path>\n\t\t\t\t\t%s\n\t\t\t\t</path>\n\t\t\t</info>\n'
                '\t\t</mouseitem>' % (act, s['_itemid'], guid(), path_escape(rp)))
        elif s['action'] == 'set':
            rec.append(
                '\t\t<keysequenceitem KeySequence="%s" duration="20ms" enabled="True" optional="False" '
                'presstime="20ms" repoItemActivation="EnsureVisible" searchpropertyid="%s">\n'
                '\t\t\t<comment></comment>\n\t\t\t<bindings></bindings>\n'
                '\t\t\t<info id="%s" sourcename="Recorder">\n\t\t\t\t<path>\n\t\t\t\t\t%s\n\t\t\t\t</path>\n\t\t\t</info>\n'
                '\t\t</keysequenceitem>' % (xml_escape(s.get('value', '')), s['_itemid'], guid(), path_escape(rp)))

    rxrec = ('﻿<?xml version="1.0" encoding="utf-8"?>\n'
             '<recordtable embeddedrepository="False" id="%s" mode="Global">\n'
             '\t<repeatcount>1</repeatcount>\n\t<turbomode>False</turbomode>\n\t<genreport>True</genreport>\n'
             '\t<speedfactor>1</speedfactor>\n\t<useDefaultItemLogging>True</useDefaultItemLogging>\n'
             '\t<codegen classname="%s" namespace="%s"></codegen>\n\t<variables></variables>\n'
             '\t<recorditems>\n%s\n\t</recorditems>\n</recordtable>\n') % (guid(), name, ns, "\n".join(rec))

    os.makedirs(out_dir, exist_ok=True)
    open(os.path.join(out_dir, name + '.rxrep'), 'w', encoding='utf-8').write(rxrep)
    open(os.path.join(out_dir, name + '.rxrec'), 'w', encoding='utf-8').write(rxrec)
    print('exported %s: %d actions, %d repository items -> %s.rxrec + %s.rxrep' % (name, len(rec), len(items), name, name))

def main():
    if len(sys.argv) < 2:
        print('usage: python export-ranorex.py <recording-map.json> [out-dir]'); return
    here = os.path.dirname(os.path.abspath(__file__))
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, '..', 'generated', 'ranorex-export')
    export(sys.argv[1], out)

if __name__ == '__main__':
    main()
