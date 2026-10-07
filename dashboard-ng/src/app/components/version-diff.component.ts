import { Component, ChangeDetectionStrategy, signal, input, output, effect, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TestApiService, VersionDraftEnvelope } from '../services/test-api.service';

interface DiffLine { kind: 'add' | 'del' | 'same' | 'gap'; text: string; }

/**
 * VersionDiff - the modal that shows the agent's drafted NEXT version of a test (a side-by-side
 * line diff: old vN vs new vN+1), then lets the user APPLY (write it live + auto-run + auto-review)
 * or DISCARD. It polls the draft endpoint while the agent is generating, then renders the diff.
 */
@Component({
  selector: 'app-version-diff',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
  <div class="backdrop" (click)="onBackdrop()">
    <div class="modal" (click)="$event.stopPropagation()">
      <header>
        <div>
          <h3>New version draft</h3>
          <p *ngIf="manifest() as m" class="sub">
            <span class="vchip">v{{ m.fromVersion }}</span> &rarr; <span class="vchip new">v{{ m.toVersion }}</span>
            &nbsp; <code>{{ m.newMethod }}</code>
          </p>
        </div>
        <button class="x" (click)="close.emit()">&times;</button>
      </header>

      <!-- WHAT'S HAPPENING NOW: a clear step tracker so the user always knows the stage -->
      <div class="steps">
        <div class="stp" [class.on]="stage()==='drafting'" [class.done]="stageDone('drafting')">
          <span class="dot"></span> 1 · Agent writes new version</div>
        <div class="bar" [class.done]="stageDone('drafting')"></div>
        <div class="stp" [class.on]="stage()==='review'" [class.done]="stageDone('review')">
          <span class="dot"></span> 2 · You review the diff</div>
        <div class="bar" [class.done]="stageDone('review')"></div>
        <div class="stp" [class.on]="stage()==='applying'">
          <span class="dot"></span> 3 · Apply &rarr; auto-run &amp; re-review</div>
      </div>

      <div *ngIf="running()" class="state">
        <span class="spin"></span>
        <div class="prog">
          <div class="ptop"><span>{{ phaseLabel() }}</span><span class="elapsed">{{ elapsedLabel() }}</span></div>
          <div class="track"><div class="fill"></div></div>
          <div class="hint">The agent rewrites the whole test, so larger tests take longer — usually 30s–2min. It will appear here automatically when ready.</div>
        </div>
      </div>

      <div *ngIf="applying()" class="state apply-state">
        <span class="spin"></span><span>Writing v{{ manifest()?.toVersion }} into the suite, then running &amp; re-reviewing it…</span>
      </div>

      <ng-container *ngIf="!running() && !applying() && stagedManifest() as m">
        <section class="accepted">
          <b>Implementing {{ m.accepted.length }} accepted improvement(s):</b>
          <ul><li *ngFor="let a of m.accepted">{{ a }}</li></ul>
        </section>

        <div class="legend">
          <span class="lg add">+ added in v{{ m.toVersion }}</span>
          <span class="lg del">− removed</span>
          <span class="lg same">unchanged</span>
          <span class="added-count">{{ addedCount() }} line(s) added</span>
        </div>
        <div class="diff">
          <div *ngFor="let l of diff()" class="line" [ngClass]="l.kind">
            <span class="sign">{{ l.kind==='add' ? '+' : l.kind==='del' ? '-' : (l.kind==='gap' ? '' : ' ') }}</span>
            <code>{{ l.text }}</code>
          </div>
        </div>

        <footer>
          <button class="discard" (click)="discard()">Discard</button>
          <button class="apply" (click)="apply()">&#10003; Apply &amp; run v{{ m.toVersion }}</button>
        </footer>
      </ng-container>

      <div *ngIf="!running() && !manifest()" class="state err">
        No draft was produced. Close and try again.
      </div>
    </div>
  </div>
  `,
  styles: [`
    /* host is a viewport-level overlay so the modal is ALWAYS on top, never clipped by any
       scrolling/positioned ancestor (the review pane). */
    :host{position:fixed;inset:0;z-index:1000;display:block}
    .backdrop{position:fixed;inset:0;background:rgba(2,6,23,.72);display:flex;align-items:center;justify-content:center;z-index:1000}
    .modal{background:#0f172a;border:1px solid #334155;border-radius:14px;width:min(1080px,94vw);max-height:90vh;display:flex;flex-direction:column;box-shadow:0 24px 60px rgba(0,0,0,.5)}
    header{display:flex;justify-content:space-between;align-items:flex-start;padding:18px 22px;border-bottom:1px solid #1e293b}
    h3{margin:0;font-size:18px} .sub{margin:6px 0 0;color:#94a3b8;font-size:13px}
    .vchip{background:#334155;color:#cbd5e1;padding:2px 9px;border-radius:6px;font-weight:700}
    .vchip.new{background:#166534;color:#bbf7d0}
    code{font-family:Consolas,monospace}
    .x{background:none;border:none;color:#94a3b8;font-size:26px;line-height:1;cursor:pointer}
    .steps{display:flex;align-items:center;gap:8px;padding:14px 22px;border-bottom:1px solid #1e293b;flex-wrap:wrap}
    .stp{display:flex;align-items:center;gap:8px;font-size:12.5px;color:#64748b}
    .stp .dot{width:10px;height:10px;border-radius:50%;background:#334155;flex:none}
    .stp.on{color:#e2e8f0;font-weight:700} .stp.on .dot{background:#a78bfa;box-shadow:0 0 0 4px rgba(167,139,250,.22)}
    .stp.done{color:#86efac} .stp.done .dot{background:#22c55e}
    .bar{flex:1;min-width:18px;height:2px;background:#334155;border-radius:2px} .bar.done{background:#22c55e}
    .state{display:flex;align-items:flex-start;gap:12px;color:#94a3b8;padding:34px 22px}
    .prog{flex:1} .ptop{display:flex;justify-content:space-between;align-items:center;color:#e2e8f0;font-weight:600;margin-bottom:10px}
    .elapsed{color:#a78bfa;font-variant-numeric:tabular-nums;font-size:13px}
    .track{height:6px;background:#1e293b;border-radius:4px;overflow:hidden}
    .fill{height:100%;width:35%;background:linear-gradient(90deg,#7c3aed,#a78bfa);border-radius:4px;animation:slide 1.4s ease-in-out infinite}
    @keyframes slide{0%{margin-left:-35%}100%{margin-left:100%}}
    .hint{color:#64748b;font-size:12px;margin-top:10px;line-height:1.5}
    .apply-state{color:#bbf7d0;background:#16241c;border-top:1px solid #166534;border-bottom:1px solid #166534;padding:18px 22px;font-weight:600}
    .state.err{color:#fca5a5} .spin{width:18px;height:18px;border:3px solid #334155;border-top-color:#a78bfa;border-radius:50%;animation:rot .8s linear infinite}
    @keyframes rot{to{transform:rotate(360deg)}}
    .accepted{padding:14px 22px;color:#e2e8f0;font-size:13px;border-bottom:1px solid #1e293b}
    .accepted ul{margin:8px 0 0;padding-left:18px} .accepted li{margin:4px 0;color:#cbd5e1}
    .legend{display:flex;align-items:center;gap:14px;padding:8px 22px;font-size:12px;color:#94a3b8;border-bottom:1px solid #1e293b}
    .lg{padding:2px 8px;border-radius:5px} .lg.add{background:rgba(22,101,52,.4);color:#bbf7d0}
    .lg.del{background:rgba(153,27,27,.4);color:#fecaca} .lg.same{color:#64748b}
    .added-count{margin-left:auto;color:#86efac;font-weight:700}
    .diff{overflow:auto;flex:1;padding:8px 0;background:#0b1220;font-size:12.5px;line-height:1.55}
    .line{display:flex;gap:8px;padding:0 16px;white-space:pre-wrap}
    .line .sign{width:10px;flex:none;color:#64748b;text-align:center}
    .line.add{background:rgba(22,101,52,.28)} .line.add .sign,.line.add code{color:#bbf7d0}
    .line.del{background:rgba(153,27,27,.26)} .line.del .sign,.line.del code{color:#fecaca}
    .line.same code{color:#94a3b8}
    .line.gap{justify-content:center;color:#475569;padding:4px 0;background:#0b1220}
    footer{display:flex;justify-content:flex-end;gap:10px;padding:14px 22px;border-top:1px solid #1e293b}
    .discard{background:#334155;color:#e2e8f0;border:none;padding:9px 18px;border-radius:8px;cursor:pointer}
    .apply{background:#22c55e;color:#052e16;border:none;padding:9px 20px;border-radius:8px;font-weight:700;cursor:pointer}
  `]
})
export class VersionDiffComponent {
  /** The base test we're drafting a new version for. */
  readonly test = input.required<string>();

  readonly close = output<void>();
  readonly applied = output<string>();   // emits the new method name after Apply

  readonly running = signal(true);
  readonly applying = signal(false);
  readonly phase = signal('drafting');
  readonly env = signal<VersionDraftEnvelope | null>(null);
  readonly manifest = computed(() => this.env()?.draft ?? null);
  readonly stagedManifest = computed(() => { const d = this.env()?.draft; return d && d.status === 'staged' ? d : null; });
  // which of the 3 stages we're in (for the step tracker)
  readonly stage = computed<'drafting' | 'review' | 'applying'>(() =>
    this.applying() ? 'applying' : (this.running() ? 'drafting' : 'review'));
  readonly seconds = signal(0);       // elapsed seconds while drafting (so the wait feels alive)
  private timer?: any;
  private clock?: any;
  private startMs = 0;

  constructor(private api: TestApiService) {
    // react to the test input changing. allowSignalWrites: we intentionally update state signals
    // from here (polling), which is the sanctioned escape hatch and avoids NG0600.
    effect(() => { const t = this.test(); if (t) this.startPoll(t); }, { allowSignalWrites: true });
  }

  private openedAt = 0;
  // Emit outputs OUTSIDE the current change-detection frame. The signal-based output() throws
  // NG0953 if emitted during CD / from a reactive context (e.g. when the modal is toggled by a
  // parent effect). Deferring to a macrotask guarantees we're past that frame.
  private emitClose() { setTimeout(() => this.close.emit(), 0); }
  private emitApplied(m: string) { setTimeout(() => this.applied.emit(m), 0); }

  // backdrop click closes - but ignore the click that opened the modal (same click can bubble
  // through), and never close while the agent is mid-draft.
  onBackdrop() {
    if (Date.now() - this.openedAt < 500) return;
    if (this.running()) return;
    this.emitClose();
  }

  private startPoll(test: string) {
    this.stop();
    this.openedAt = Date.now();
    this.startMs = Date.now(); this.seconds.set(0);
    this.clock = setInterval(() => this.seconds.set(Math.floor((Date.now() - this.startMs) / 1000)), 1000);
    // defer the first poll to a microtask so signal writes never happen during the effect's
    // synchronous run (belt-and-suspenders against NG0600).
    const tick = () => this.api.versionDraft(test).subscribe(e => {
      this.env.set(e); this.running.set(e.running); this.phase.set(e.phase);
      // ready only when a STAGED draft exists (ignore a stale applied one); keep polling otherwise.
      const staged = e.draft && e.draft.status === 'staged';
      if (!e.running && staged) this.stop();
    });
    queueMicrotask(tick);
    this.timer = setInterval(tick, 2500);
  }
  private stop() {
    if (this.timer) { clearInterval(this.timer); this.timer = undefined; }
    if (this.clock) { clearInterval(this.clock); this.clock = undefined; }
  }
  elapsedLabel(): string {
    const s = this.seconds(); const m = Math.floor(s / 60);
    return m ? `${m}m ${s % 60}s` : `${s}s`;
  }

  /** Compute a simple line diff (LCS-free; a readable add/del/same view good enough for review). */
  diff(): DiffLine[] {
    const e = this.env(); if (!e?.newCode) return [];
    const oldL = (e.oldCode || '').replace(/\r/g, '').split('\n');
    const newL = e.newCode.replace(/\r/g, '').split('\n');
    const oldSet = new Set(oldL);
    const newSet = new Set(newL);
    const out: DiffLine[] = [];
    // walk the new file; mark lines not in old as additions. Then list removed old lines grouped.
    let lastWasContext = false, contextRun = 0;
    for (const ln of newL) {
      if (newSet.has(ln) && !oldSet.has(ln)) { out.push({ kind: 'add', text: ln }); lastWasContext = false; contextRun = 0; }
      else {
        // collapse long runs of unchanged context
        if (contextRun < 2) out.push({ kind: 'same', text: ln });
        else if (!lastWasContext) out.push({ kind: 'gap', text: '⋯ unchanged ⋯' });
        contextRun++; lastWasContext = contextRun >= 2;
      }
    }
    const removed = oldL.filter(l => l.trim() && !newSet.has(l));
    if (removed.length) {
      out.push({ kind: 'gap', text: `— removed from v (${removed.length} line(s)) —` });
      removed.slice(0, 40).forEach(l => out.push({ kind: 'del', text: l }));
    }
    return out;
  }

  stageDone(s: 'drafting' | 'review'): boolean {
    if (s === 'drafting') return !this.running();           // draft produced
    if (s === 'review') return this.applying();             // moved past review into apply
    return false;
  }
  addedCount(): number { return this.diff().filter(l => l.kind === 'add').length; }

  apply() {
    const t = this.test();
    this.applying.set(true);                                 // show stage 3 immediately
    this.api.applyVersion(t).subscribe(r => {
      if (r.ok) this.emitApplied(r.newMethod);
      // brief beat so the user sees stage 3 light up, then hand off to the suite (which shows the run)
      setTimeout(() => this.emitClose(), 600);
    });
  }
  discard() {
    const t = this.test();
    this.api.discardVersion(t).subscribe(() => this.emitClose());
  }

  phaseLabel(): string {
    const p = this.phase();
    if (p === 'drafting') return 'Reading the latest version…';
    if (p === 'generating') return 'The agent is writing the new version…';
    return 'Preparing the draft…';
  }
}
