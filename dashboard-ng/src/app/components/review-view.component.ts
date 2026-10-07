import { Component, OnDestroy, ChangeDetectionStrategy, signal, input, output, effect, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subscription } from 'rxjs';
import { TestApiService, Review, VersionVerdict } from '../services/test-api.service';
import { VersionDiffComponent } from './version-diff.component';

/**
 * ReviewView - the AI review for ONE test (its own section, inside that test's detail pane).
 * It runs the test first if there's no evidence yet (or re-runs it on force), then the AI agent
 * analyzes THAT test and we render its verdict. Each suggestion (gap / quality issue / top rec) can
 * be ACCEPTED; accepting >=1 and pressing "Draft new version" asks the agent to rewrite the test as
 * a new version implementing them - shown as a diff to Apply or Discard (VersionDiffComponent).
 * Suggestions can reference a screenshot (step #) so the user sees WHERE the fix applies.
 */
@Component({
  selector: 'app-review-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, VersionDiffComponent],
  template: `
  <div class="head">
    <div>
      <h2>AI Review <span class="for">for this test</span></h2>
      <p class="sub">Grounded in the real app (runtime map) + this test's own run evidence. Accept fixes to draft a new version.</p>
    </div>
    <div class="acts">
      <button class="run" [disabled]="running()" (click)="run(false)">
        {{ running() ? 'Working…' : (review()?.summary ? '↻ Re-review' : '▶ Review this test') }}</button>
      <button class="force" [disabled]="running()" (click)="run(true)" title="Re-run this test fresh, then review">
        ⟳ Re-run & review</button>
    </div>
  </div>

  <!-- SELF-CORRECTING VERDICT banner: did this version achieve what we accepted? -->
  <div *ngIf="verdict() as v" class="verdict" [ngClass]="v.retrying ? 'retry' : (v.achieved ? 'ok' : 'fail')">
    <div class="vhead">
      <span class="vicon">{{ v.retrying ? '⟳' : (v.achieved ? '✓' : '✗') }}</span>
      <strong *ngIf="v.retrying">Attempt {{ v.attempt }} failed — discarding the broken version & retrying from the last good one (learning from the error)…</strong>
      <strong *ngIf="!v.retrying && v.achieved">This version passed and achieved the improvement<span *ngIf="v.attempt>1"> (on attempt {{ v.attempt }})</span></strong>
      <strong *ngIf="!v.retrying && !v.achieved">Could not achieve it after {{ v.attempt }} attempt(s) — rolled back, no broken version kept</strong>
      <span class="vconf" *ngIf="!v.retrying && v.runStatus">run: {{ v.runStatus }}<span *ngIf="v.steps"> · {{ v.steps }} steps</span> · deterministic</span>
    </div>
    <div *ngIf="v.added" class="vadded">
      <span class="addedlabel">Added in v{{ versionNum() }} <span *ngIf="v.fromVersion">(vs v{{ v.fromVersion }})</span>:</span>
      {{ v.added }}
    </div>
    <div class="vreason">{{ v.reason }}</div>
    <ul *ngIf="!v.achieved && v.missing?.length" class="vmissing">
      <li *ngFor="let m of v.missing">Still missing: {{ m }}</li>
    </ul>
    <div *ngIf="!v.retrying && !v.achieved" class="vacts">
      <button class="vretry" (click)="discardAndRetry()">↻ Discard this version & try a fresh attempt</button>
    </div>
  </div>

  <div *ngIf="running()" class="state">
    <span class="spin"></span><span>{{ phaseLabel() }}</span>
  </div>

  <ng-container *ngIf="review() as r">
    <div *ngIf="r.summary" class="score">
      <div class="ring" [style.--pct]="r.coverageScore"><span>{{ r.coverageScore }}</span><small>coverage</small></div>
      <p class="summary">{{ r.summary }}</p>
    </div>

    <!-- draft panel: accept AI suggestions below AND/OR type your own instruction, then draft -->
    <div class="draftpanel">
      <div class="dprow">
        <span class="dpcount">{{ acceptedCount() }} AI suggestion(s) accepted</span>
        <button *ngIf="acceptedCount()>0" class="clear" (click)="clearAccepted()">Clear</button>
      </div>
      <textarea class="hfeedback" rows="2"
        [value]="humanFeedback()" (input)="humanFeedback.set($any($event.target).value)"
        placeholder="Optional: write your own instruction for the new version (e.g. 'also verify the price field rejects letters'). You can draft with just this, no AI suggestion needed."></textarea>
      <button class="draft" [disabled]="acceptedCount()===0 && !humanFeedback().trim()" (click)="openDraft()">
        ✎ Draft new version &rarr;</button>
    </div>

    <section *ngIf="r.topRecommendations?.length">
      <h3>Top recommendations</h3>
      <div *ngFor="let rec of r.topRecommendations; let i = index" class="rec card"
           [class.accepted]="isAccepted(rec)">
        <label class="pick"><input type="checkbox" [checked]="isAccepted(rec)" (change)="toggle(rec)"><span></span></label>
        <div class="recbody"><div class="issue">{{ rec }}</div></div>
      </div>
    </section>

    <section *ngIf="r.gaps?.length">
      <h3>Coverage gaps <span class="count">{{ r.gaps.length }}</span></h3>
      <div *ngFor="let g of r.gaps" class="card" [ngClass]="g.severity" [class.accepted]="isAccepted(gapText(g))">
        <label class="pick"><input type="checkbox" [checked]="isAccepted(gapText(g))" (change)="toggle(gapText(g))"><span></span></label>
        <div class="recbody">
          <div class="row"><span class="sev">{{ g.severity }}</span><strong>{{ g.area }}</strong></div>
          <div class="issue">{{ g.issue }}</div>
          <div class="fix"><b>Fix:</b> {{ g.fix }}</div>
          <figure *ngIf="g.screenshot" class="crop">
            <img [src]="img(g.screenshot)" loading="lazy" />
            <figcaption>where this applies{{ g.step ? ' · step ' + g.step : '' }}</figcaption>
          </figure>
        </div>
      </div>
    </section>

    <section *ngIf="r.qualityIssues?.length">
      <h3>Test-quality issues <span class="count">{{ r.qualityIssues.length }}</span></h3>
      <div *ngFor="let q of r.qualityIssues" class="card" [ngClass]="q.severity" [class.accepted]="isAccepted(qualText(q))">
        <label class="pick"><input type="checkbox" [checked]="isAccepted(qualText(q))" (change)="toggle(qualText(q))"><span></span></label>
        <div class="recbody">
          <div class="row"><span class="sev">{{ q.severity }}</span><strong>{{ q.test }}</strong></div>
          <div class="issue">{{ q.issue }}</div>
          <div class="fix"><b>Fix:</b> {{ q.fix }}</div>
          <figure *ngIf="q.screenshot" class="crop">
            <img [src]="img(q.screenshot)" loading="lazy" />
            <figcaption>where this applies{{ q.step ? ' · step ' + q.step : '' }}</figcaption>
          </figure>
        </div>
      </div>
    </section>

    <div *ngIf="!r.summary && !running()" class="empty">
      No review yet for this test. Press <b>“Review this test”</b> — the agent runs it if needed, then analyzes it.
    </div>
  </ng-container>

  <app-version-diff *ngIf="showDraft()" [test]="test()"
    (close)="showDraft.set(false)" (applied)="onApplied($event)"></app-version-diff>
  `,
  styles: [`
    :host{display:block;padding:8px 0}
    .head{display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:16px;gap:14px}
    h2{margin:0;font-size:18px} .for{color:#a78bfa;font-size:13px;font-weight:600}
    .sub{color:#94a3b8;font-size:13px;margin:4px 0 0}
    .acts{display:flex;gap:8px;flex:none}
    .run{background:#a78bfa;color:#0f172a;border:none;padding:9px 16px;border-radius:8px;font-weight:700;cursor:pointer}
    .run:disabled,.force:disabled{background:#475569;color:#94a3b8;cursor:not-allowed}
    .force{background:#334155;color:#e2e8f0;border:none;padding:9px 14px;border-radius:8px;font-weight:600;cursor:pointer}
    .force:hover:not(:disabled){background:#475569}
    .state{display:flex;align-items:center;gap:10px;color:#94a3b8;padding:14px 0}
    .spin{width:16px;height:16px;border:3px solid #334155;border-top-color:#a78bfa;border-radius:50%;animation:rot .8s linear infinite}
    @keyframes rot{to{transform:rotate(360deg)}}
    .score{display:flex;gap:20px;align-items:center;margin-bottom:18px;background:#1e293b;border-radius:12px;padding:18px 22px}
    .ring{width:84px;height:84px;border-radius:50%;display:flex;flex-direction:column;align-items:center;justify-content:center;flex:none;
      background:conic-gradient(#a78bfa calc(var(--pct)*1%), #334155 0)}
    .ring span{font-size:24px;font-weight:700} .ring small{font-size:10px;color:#cbd5e1}
    .summary{margin:0;font-size:14px;line-height:1.6}
    .draftpanel{background:#172554;border:1px solid #3b82f6;border-radius:10px;padding:12px 16px;margin-bottom:14px}
    .dprow{display:flex;justify-content:space-between;align-items:center;margin-bottom:8px}
    .dpcount{font-weight:600;color:#bfdbfe;font-size:13px}
    .dprow .clear{background:none;border:none;color:#94a3b8;cursor:pointer;font-size:12px}
    .hfeedback{width:100%;box-sizing:border-box;background:#0f172a;color:#e2e8f0;border:1px solid #334155;border-radius:8px;
      padding:8px 10px;font-size:13px;font-family:inherit;resize:vertical;margin-bottom:8px}
    .hfeedback:focus{outline:none;border-color:#3b82f6}
    .draft{background:#3b82f6;color:#fff;border:none;padding:9px 18px;border-radius:8px;font-weight:700;cursor:pointer}
    .draft:disabled{background:#475569;color:#94a3b8;cursor:not-allowed}
    h3{font-size:16px;margin:22px 0 10px;display:flex;align-items:center;gap:8px}
    .count{background:#334155;color:#cbd5e1;font-size:12px;padding:1px 9px;border-radius:20px}
    .card{display:flex;gap:12px;background:#1e293b;border-radius:10px;padding:14px 18px;margin-bottom:10px;border-left:4px solid #475569;transition:outline .15s}
    .card.accepted{outline:2px solid #22c55e;background:#16241c}
    .card.high{border-left-color:#f87171} .card.medium{border-left-color:#fbbf24} .card.low{border-left-color:#38bdf8}
    .pick{flex:none;display:flex;align-items:flex-start;padding-top:2px;cursor:pointer}
    .pick input{width:18px;height:18px;accent-color:#22c55e;cursor:pointer}
    .recbody{flex:1}
    .row{display:flex;align-items:center;gap:10px;margin-bottom:6px}
    .sev{font-size:11px;text-transform:uppercase;font-weight:700;padding:2px 8px;border-radius:6px;background:#334155;color:#cbd5e1}
    .card.high .sev{background:#7f1d1d;color:#fecaca} .card.medium .sev{background:#854d0e;color:#fde68a}
    .issue{font-size:14px;line-height:1.5;margin-bottom:6px} .fix{font-size:13px;color:#cbd5e1;line-height:1.5}
    .crop{margin:10px 0 0} .crop img{max-width:340px;border:1px solid #475569;border-radius:6px;display:block}
    .crop figcaption{font-size:11px;color:#94a3b8;margin-top:4px}
    .empty{color:#94a3b8;padding:20px;background:#1e293b;border-radius:10px}
    .verdict{border-radius:12px;padding:14px 18px;margin-bottom:16px;border:1px solid #334155}
    .verdict.ok{background:#14241b;border-color:#22c55e}
    .verdict.fail{background:#2a1416;border-color:#f87171}
    .verdict.retry{background:#1e2438;border-color:#a78bfa}
    .vhead{display:flex;align-items:center;gap:10px;font-size:14px}
    .vicon{font-size:18px;font-weight:800}
    .verdict.ok .vicon{color:#4ade80} .verdict.fail .vicon{color:#f87171}
    .verdict.retry .vicon{color:#a78bfa;display:inline-block;animation:rot 1.2s linear infinite}
    .vconf{margin-left:auto;font-size:12px;color:#94a3b8;font-weight:600}
    .vadded{font-size:13px;color:#e2e8f0;margin-top:8px;line-height:1.5;background:rgba(56,189,248,.1);border-radius:8px;padding:8px 12px}
    .addedlabel{color:#7dd3fc;font-weight:700}
    .vreason{font-size:13px;color:#cbd5e1;margin-top:8px;line-height:1.5}
    .vmissing{margin:8px 0 0;padding-left:18px} .vmissing li{font-size:13px;color:#fca5a5;margin:3px 0}
    .vacts{margin-top:12px}
    .vretry{background:#f87171;color:#2a1416;border:none;padding:8px 16px;border-radius:8px;font-weight:700;cursor:pointer}
  `]
})
export class ReviewViewComponent implements OnDestroy {
  readonly test = input.required<string>();
  /** Bubble up the new method name after a version is applied (suite refreshes the picker). */
  readonly versionApplied = output<string>();

  readonly running = signal(false);
  readonly phase = signal('');
  readonly review = signal<Review | null>(null);
  readonly accepted = signal<Set<string>>(new Set());
  readonly humanFeedback = signal('');     // free-text instruction the user types for the new version
  readonly showDraft = signal(false);
  readonly verdict = signal<VersionVerdict | null>(null);
  readonly acceptedCount = computed(() => this.accepted().size);
  private sub?: Subscription;
  private timer?: any;
  private vtimer?: any;

  constructor(private api: TestApiService) {
    effect(() => {
      const t = this.test();
      this.review.set(null); this.running.set(false); this.phase.set(''); this.accepted.set(new Set());
      this.verdict.set(null);
      this.stop();
      if (t) {
        this.loadReview(t);    // poll the review (so a running review resolves to done, not stuck "Working…")
        this.pollVerdict(t);   // show the achieved/not-achieved verdict (+ keep polling while retrying)
      }
    }, { allowSignalWrites: true });
  }
  ngOnDestroy() { this.sub?.unsubscribe(); this.stop(); if (this.vtimer) clearInterval(this.vtimer); }

  run(force: boolean) {
    const t = this.test(); if (!t) return;
    this.running.set(true);
    this.phase.set(force ? 'starting (re-running this test)' : 'starting');
    this.accepted.set(new Set());
    // poll even if the kick-off request errors (server hiccup): the state file is the authority,
    // and polling resolves to it - otherwise the spinner sticks on "Working…" forever.
    this.api.runReview(t, force).subscribe({ next: () => this.poll(), error: () => this.poll() });
  }

  // --- accept selection -------------------------------------------------------
  gapText(g: { area: string; issue: string; fix: string }) { return `[${g.area}] ${g.issue} FIX: ${g.fix}`; }
  qualText(q: { test: string; issue: string; fix: string }) { return `${q.issue} FIX: ${q.fix}`; }
  isAccepted(text: string) { return this.accepted().has(text); }
  toggle(text: string) {
    const next = new Set(this.accepted());
    next.has(text) ? next.delete(text) : next.add(text);
    this.accepted.set(next);
  }
  clearAccepted() { this.accepted.set(new Set()); }
  openDraft() {
    const t = this.test();
    const items = [...this.accepted()];
    const human = this.humanFeedback().trim();
    if (human) items.push(`[Human instruction] ${human}`);     // your own words become an accepted item
    if (!items.length) return;
    this.showDraft.set(true);                                   // show the modal IMMEDIATELY (stays on top)
    this.api.draftVersion(t, items).subscribe();               // then kick off the agent; modal polls for it
    this.humanFeedback.set('');
  }
  $any(x: any) { return x; }   // template helper for event.target typing
  onApplied(newMethod: string) {
    this.showDraft.set(false);
    this.verdict.set(null);                 // a new version was applied -> verdict is being computed
    setTimeout(() => this.versionApplied.emit(newMethod), 0);   // emit outside CD (avoid NG0953)
  }

  // poll the self-correcting verdict; keep polling while the loop is still retrying.
  private pollVerdict(test: string) {
    if (this.vtimer) { clearInterval(this.vtimer); this.vtimer = undefined; }
    const tick = () => this.api.verdict(test).subscribe(v => {
      this.verdict.set(v);
      if (v && !v.retrying && this.vtimer) { clearInterval(this.vtimer); this.vtimer = undefined; }
    });
    tick();
    this.vtimer = setInterval(tick, 4000);
  }

  // manual fallback after the auto-loop exhausts: draft a fresh attempt. The server reuses the
  // original accepted improvements + the accumulated failure feedback, so the new attempt still
  // learns from every miss. We pass the verdict's "missing" items to reinforce what to fix.
  discardAndRetry() {
    const t = this.test(); if (!t) return;
    const missing = this.verdict()?.missing ?? [];
    this.verdict.set(null);
    this.showDraft.set(true);
    this.api.draftVersion(t, missing).subscribe();   // server merges with stored accepted + feedback
  }

  private apply(env: { running: boolean; phase: string; review: Review }) {
    this.running.set(env.running); this.phase.set(env.phase); this.review.set(env.review);
  }
  // load the review and, if it's running, keep polling until it settles (no stuck "Working…").
  private loadReview(test: string) {
    this.stop();
    const tick = () => this.api.review(test).subscribe({
      next: env => {
        this.apply(env);
        if (!env.running && this.timer) { clearInterval(this.timer); this.timer = undefined; }
      },
      error: () => { }   // server briefly down - keep polling, a later tick settles the state
    });
    tick();
    this.timer = setInterval(tick, 3000);
  }
  private stop() { if (this.timer) { clearInterval(this.timer); this.timer = undefined; } }
  private poll() {
    this.stop();
    const t = this.test();
    this.timer = setInterval(() => {
      this.api.review(t).subscribe({
        next: env => { this.apply(env); if (!env.running) this.stop(); },
        error: () => { }   // keep polling through server hiccups
      });
    }, 3000);
  }

  img(file: string) { return this.api.reportUrl(file); }
  versionNum(): number { const m = this.test()?.match(/_v(\d+)$/); return m ? +m[1] : 1; }
  phaseLabel(): string {
    const p = this.phase();
    if (p?.startsWith('running:')) return `Running this test first to gather real evidence…`;
    if (p === 'analyzing') return 'The AI agent is analyzing this test…';
    if (p?.includes('re-running')) return 'Re-running this test, then the AI will analyze…';
    return 'Preparing the review…';
  }
}
