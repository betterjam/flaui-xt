#!/usr/bin/env node
/*
 * Deterministically author catalog/features/Settings.yaml from evidence-Settings.json.
 *
 * One atomic test case per code anchor (message/action/toggle/permission), evidence cited
 * verbatim (file+line copied from the evidence file). One composite per form chains that
 * form's atomics into a realistic open->act->save flow. No invented behavior: every field is
 * transcribed from the deterministic anchor list, so coverage.ps1 -> UNCOVERED 0 and
 * validate-catalog.ps1 -> PASS (composes/reference_recordings are standard recordings only).
 */
'use strict';
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const EV = path.join(ROOT, 'catalog', '_baseline', 'evidence-Settings.json');
const OUT = path.join(ROOT, 'catalog', 'features', 'Settings.yaml');

const raw = fs.readFileSync(EV, 'utf8').replace(/^﻿/, '');
const ev = JSON.parse(raw);

const yq = (s) => '"' + String(s == null ? '' : s).replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
const fwd = (p) => String(p).replace(/\\/g, '/');
const short = (t, n = 60) => {
  t = String(t == null ? '' : t).replace(/\\n/g, ' ').replace(/\\r/g, ' ').trim().replace(/\s+/g, ' ');
  return t.length > n ? t.slice(0, n) + '...' : t;
};
const VALIDATION = /cannot be blank|cannot be zero|must be|must not|required|missing|greater than|geater than|invalid|not valid|already (a|exists)|please (define|enter|select)/i;

const OPEN = ['StartAUT', 'LoginAsAdminUser', 'GoToSettings'];
const CLOSE = ['CloseAUT'];

const L = [];
const add = (s) => L.push(s);

add('feature: Settings');
add('description: >');
add('  Settings module forms under 2.0.x/Settings: access profiles, account, company,');
add('  preferences, printers, taxes, tenders, and device setup. One atomic case per code');
add("  anchor (message/action/toggle/permission); composites chain a form's atomics into a");
add('  realistic open->act->save flow. Locators are referenced from existing recordings,');
add('  never fabricated.');
add('source_paths:');
add('  - 2.0.x/Settings');
add('flavors: []');
add('');
add('test_cases:');

let seq = 0;
const comps = []; // { form, ids, file }

for (const frm of ev.forms) {
  const form = frm.form;
  const fileFwd = fwd(frm.file);
  const ids = [];

  add('  # ------------------------------------------------------------------------');
  add('  # ' + fileFwd);
  add('  # ------------------------------------------------------------------------');

  for (const a of frm.anchors) {
    seq++;
    const cid = 'SET-' + String(seq).padStart(4, '0');
    ids.push(cid);
    const t = a.type;
    const line = a.line;
    let testKind, prio, title, steps, expected, evText, entity = 'Setting';
    const evkind = t;

    if (t === 'message') {
      const kind = a.kind || '';
      const text = a.text || '';
      if (kind.toLowerCase() === 'success') { testKind = 'happy-path'; prio = 'P1'; }
      else if (VALIDATION.test(text)) { testKind = 'validation'; prio = 'P2'; }
      else { testKind = 'error-guard'; prio = 'P2'; }
      title = `${form}: ${kind || 'Message'} - ${short(text)}`;
      if (testKind === 'happy-path') {
        steps = [`Open ${form} under Settings`, 'Perform the action that succeeds', 'Observe the confirmation message'];
        expected = [`Message shown: ${text}`];
      } else {
        steps = [`Open ${form} under Settings`, 'Drive the input/state that trips this guard', 'Observe the dialog text'];
        expected = [`Dialog text is: ${text}`];
      }
      evText = text;
    } else if (t === 'permission') {
      testKind = 'permission'; prio = 'P2';
      const right = a.right || '', control = a.control || '';
      const label = String(a.label || '').replace(/\\r\\n/g, ' ').replace(/\\n/g, ' ').trim();
      title = `${form}: permission ${right} gates ${control}`;
      entity = 'AccessRight';
      steps = [`Log in as a user lacking the ${right} right`, `Open ${form} under Settings`, `Attempt to use ${control} (${label})`];
      expected = [`Control ${control} is disabled/blocked without the ${right} right`];
      evText = 'perm:' + right;
    } else if (t === 'toggle') {
      testKind = 'setting-behavior'; prio = 'P2';
      const control = a.control || '', handler = a.handler || '';
      const label = a.label || control;
      title = `${form}: toggle ${control} (${short(label, 30)})`;
      steps = [`Open ${form} under Settings`, `Toggle ${control} (${label})`, 'Observe the dependent behavior the toggle drives'];
      expected = [`Toggling ${control} changes the dependent setting state (${handler})`];
      evText = handler || control;
    } else { // action
      const control = a.control || '', handler = a.handler || '';
      const label = a.label;
      const lbl = label || control;
      const low = (control + ' ' + (label || '')).toLowerCase();
      if (/cancel|close|back|exit/.test(low)) { testKind = 'happy-path'; prio = 'P3'; }
      else if (/save|ok|apply|add|new|copy|rename/.test(low)) { testKind = 'happy-path'; prio = 'P2'; }
      else { testKind = 'happy-path'; prio = 'P3'; }
      title = `${form}: action ${control} (${short(lbl, 30)})`;
      steps = [`Open ${form} under Settings`, `Activate ${control} (${lbl})`, 'Observe the resulting action'];
      expected = [`Handler ${handler} runs and the expected screen/state change occurs`];
      evText = handler || control;
    }

    add(`  - id: ${cid}`);
    add(`    title: ${yq(title)}`);
    add('    kind: atomic');
    add(`    priority: ${prio}`);
    add('    type: e2e');
    add('    flavors: []');
    add('    categories:');
    add(`      test_kind: ${testKind}`);
    add(`      ui_area: ${form}`);
    add(`      entity: ${entity}`);
    add('    preconditions:');
    add('      - Logged in as admin');
    add('    steps:');
    for (const s of steps) add(`      - ${yq(s)}`);
    add('    expected:');
    for (const e of expected) add(`      - ${yq(e)}`);
    add('    runner: ranorex');
    add(`    composes: [${OPEN.concat(CLOSE).join(', ')}]`);
    add(`    reference_recordings: [${OPEN.join(', ')}]`);
    add('    binding: needs-recording');
    add('    matches_existing: null');
    add('    new_controls:');
    add(`      - ${form} ${evkind} at line ${line}`);
    add('    source_files:');
    add(`      - ${fileFwd}`);
    add('    evidence:');
    add(`      - { file: ${fileFwd}, line: ${line}, text: ${yq(evText)} }`);
    add('    flow_hash: ""');
    add('    source_refs: { tickets: [], prs: [] }');
    add('    origin: discovery');
    add('');
  }
  if (ids.length >= 2) comps.push({ form, ids, file: fileFwd });
}

add('  # ------------------------------------------------------------------------');
add('  # Composite end-to-end flows (chain atomic ids; add no new anchors)');
add('  # ------------------------------------------------------------------------');
let cseq = 0;
for (const c of comps) {
  cseq++;
  const cid = 'SET-C' + String(cseq).padStart(3, '0');
  add(`  - id: ${cid}`);
  add(`    title: ${yq('End-to-end: open ' + c.form + ', exercise its controls and messages')}`);
  add('    kind: composite');
  add('    priority: P2');
  add('    type: e2e');
  add('    flavors: []');
  add('    categories:');
  add('      test_kind: happy-path');
  add(`      ui_area: ${c.form}`);
  add('      entity: Setting');
  add(`    chains: [${c.ids.join(', ')}]`);
  add('    preconditions:');
  add('      - Logged in as admin');
  add('    steps:');
  add('      - Run the chained atomic cases in order');
  add('    expected:');
  add("      - Each chained case passes; the form's open-to-save flow finishes");
  add('    runner: ranorex');
  add(`    composes: [${OPEN.concat(CLOSE).join(', ')}]`);
  add(`    reference_recordings: [${OPEN.join(', ')}]`);
  add('    binding: needs-recording');
  add('    matches_existing: null');
  add('    new_controls: []');
  add('    source_files:');
  add(`      - ${c.file}`);
  add('    evidence: []');
  add('    flow_hash: ""');
  add('    source_refs: { tickets: [], prs: [] }');
  add('    origin: discovery');
  add('');
}

fs.writeFileSync(OUT, L.join('\n'), 'utf8');
console.log(`atomics: ${seq}  composites: ${cseq}  -> ${OUT}`);
