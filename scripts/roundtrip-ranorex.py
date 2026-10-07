#!/usr/bin/env python3
"""
Acceptance test: import a Ranorex .rxrec into our format, export it back, and verify the TEST CORE
(the ordered sequence of action + target control + typed value) is identical.

  core(original .rxrec)  ==  core(exported .rxrec)

Uses the real import + export engines, so a pass means the round-trip is lossless on the essentials.

Usage:  python roundtrip-ranorex.py [<ranorex-root>]   (defaults to a representative sample)
"""
import os, sys, json, glob, tempfile, importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
def load(name, fn):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, fn))
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m); return m
imp = load('imp', 'import-ranorex.py')
exp = load('exp', 'export-ranorex.py')

def core(steps):
    """the FUNCTIONAL test core: ordered (action, target control, typed value). The typed value matters
    for 'set'; for clicks the 'value' field is just the visible display-name (cosmetic), not test logic."""
    return [(s['action'], s['key'], s.get('value', '') if s['action'] == 'set' else '')
            for s in steps if s['action'] != 'picture']

def roundtrip(rxrec):
    cls, m1 = imp.convert(rxrec)
    if not m1:
        return cls, None, None
    with tempfile.TemporaryDirectory() as td:
        mp = os.path.join(td, 'recording-%s.json' % cls)
        open(mp, 'w', encoding='utf-8').write(json.dumps(m1, ensure_ascii=False))
        exp.export(mp, td)                                    # -> <cls>.rxrec (+ .rxrep)
        _, m2 = imp.convert(os.path.join(td, cls + '.rxrec'))  # re-import the export
    return cls, core(m1), core(m2)

def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ.get('EXATOUCH_ROOT') or r'C:\Dev\Exatouch', 'MyFirstExatouchTest')
    files = sorted(glob.glob(os.path.join(root, '**', '*.rxrec'), recursive=True))
    passed = failed = empty = 0
    fails = []
    for f in files:
        cls, c1, c2 = roundtrip(f)
        if c1 is None:
            empty += 1; continue
        if c1 == c2:
            passed += 1
        else:
            failed += 1
            if len(fails) < 5:
                fails.append((cls, c1, c2))
    print('ROUND-TRIP ACCEPTANCE over %d recordings:' % len(files))
    print('  PASS (core identical): %d' % passed)
    print('  FAIL (core differs):   %d' % failed)
    print('  empty (no actions):    %d' % empty)
    for cls, c1, c2 in fails:
        print('\n  --- FAIL %s ---' % cls)
        print('    original :', c1[:6])
        print('    exported :', c2[:6])
    print('\n  ==> %s' % ('ACCEPTED - import/export preserve the test core'
                          if failed == 0 else 'differences remain (see above)'))

if __name__ == '__main__':
    main()
