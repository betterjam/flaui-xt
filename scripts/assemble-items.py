#!/usr/bin/env python3
"""Assemble per-form Items fragments into catalog/features/Items.yaml.
- normalizes `priority: Pn ...` -> `priority: Pn`
- validates ID uniqueness
- prepends the feature header
- appends composite cases (passed via a separate fragment composites.yaml if present)
"""
import os, re, glob, sys

HERE = os.path.dirname(os.path.abspath(__file__))
FRAGS = os.path.join(HERE, '..', 'catalog', '_frags')
OUT = os.path.join(HERE, '..', 'catalog', 'features', 'Items.yaml')

# deterministic order by ID block
ORDER = [
    'frmCategory','frmCoupon','frmDateRange','frmDRV','frmFreshSheet','frmGroupManager',
    'frmHistory','frmInventory','frmItems','frmItemsList','frmItemsListSearch','frmItemsMod',
    'frmLabel','frmMatrixList','frmMatrixListSearch','frmMember','frmMenu','frmPromo',
    'frmSearch','frmSearchServices','frmServices','frmServicesList','frmServicesListSearch',
    'frmServicesMod',
]

PRIO = re.compile(r'^(\s*priority:\s*P[123])\b.*$')
IDRE = re.compile(r'^\s*-\s*id:\s*([A-Za-z0-9_-]+)')

HEADER = """feature: Items
description: >
  Item, category, matrix, service, promotion, coupon, label, fresh-sheet, inventory-count
  and bulk modify-all flows for the Exatouch Items module. Net-new catalog grounded in the
  2.0.x/Items/*.cs forms (and Items/Services/*.cs). No existing Ranorex coverage targets these
  forms directly; cases reference the generic Items building-block recordings as locator ground.
source_paths:
  - 2.0.x/Items
flavors: []          # no #if (CFD|BO|DEMO|TRAINING) branch in Items/*.cs alters the user-facing flows enumerated here

test_cases:
"""

def main():
    seen = {}
    body = []
    counts = {}
    for form in ORDER:
        path = os.path.join(FRAGS, form + '.yaml')
        if not os.path.exists(path):
            print('MISSING fragment:', form, file=sys.stderr); sys.exit(2)
        n = 0
        with open(path, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.rstrip('\n')
                if not line.strip():
                    continue  # drop blank lines; we re-space below
                m = PRIO.match(line)
                if m:
                    line = m.group(1)
                idm = IDRE.match(line)
                if idm:
                    cid = idm.group(1)
                    if cid in seen:
                        print('DUPLICATE ID', cid, 'in', form, 'and', seen[cid], file=sys.stderr); sys.exit(3)
                    seen[cid] = form
                    body.append('')  # blank line between cases
                    n += 1
                body.append(line)
        counts[form] = n

    # composites (optional)
    comp_path = os.path.join(FRAGS, 'composites.yaml')
    if os.path.exists(comp_path):
        with open(comp_path, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.rstrip('\n')
                if not line.strip():
                    continue
                idm = IDRE.match(line)
                if idm:
                    cid = idm.group(1)
                    if cid in seen:
                        print('DUPLICATE ID', cid, file=sys.stderr); sys.exit(3)
                    seen[cid] = 'composites'
                    body.append('')
                body.append(line)

    text = HEADER + '\n'.join(body).lstrip('\n') + '\n'
    with open(OUT, 'w', encoding='utf-8') as f:
        f.write(text)
    print('Wrote', OUT)
    print('Total cases:', len(seen))
    for form in ORDER:
        print('  %-22s %d' % (form, counts[form]))

if __name__ == '__main__':
    main()
