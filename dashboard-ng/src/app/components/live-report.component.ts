import { Component, OnInit, OnDestroy, ViewChildren, QueryList, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { TestApiService, LiveRun, RunState, LiveStep } from '../services/test-api.service';
import { TimelinePlayerComponent } from './timeline-player.component';
import { StepGalleryComponent } from './step-gallery.component';

type ViewKind = 'list' | 'video';

/**
 * Live streaming report: polls the shared service and renders each step (with screenshot) as
 * it appears, with a spinner while running and a PASS/FAIL finish banner. The "it opens X, sees
 * new stuff, describes what it found" view, in Angular.
 */
@Component({
  selector: 'app-live-report',
  standalone: true,
  imports: [CommonModule, RouterLink, TimelinePlayerComponent, StepGalleryComponent],
  template: `
  <header>
    <a routerLink="/" class="back">&larr; Back to all tests</a>
    <h1>{{ title }}</h1>
    <div class="viewtoggle" *ngIf="steps.length">
      <button [class.on]="view==='list'" (click)="view='list'">List</button>
      <button [class.on]="view==='video'" (click)="view='video'">&#9654; Video</button>
    </div>
  </header>

  <!-- idle: nothing running and no fresh result -> neutral message, NO false finish -->
  <div class="state" *ngIf="state==='idle'">
    <span>No test is running. Go back and start one to watch it live.</span>
  </div>

  <!-- running -->
  <div class="state" *ngIf="state==='running'">
    <span class="spin"></span><span>Running the test, please wait...</span>
  </div>

  <!-- finished -->
  <div class="state" *ngIf="state==='passed' || state==='failed'">
    <span class="done" [class.pass]="state==='passed'" [class.fail]="state==='failed'">
      {{ state==='passed' ? '✔' : '✘' }}</span>
    <span>Test {{ state }}</span>
  </div>

  <!-- VIDEO view: timeline player with subtitle captions -->
  <app-timeline-player *ngIf="view==='video'"
    [steps]="videoFrames" [imgBase]="'/report/'" [failed]="state==='failed'"
    [followLive]="state==='running'" [autoplay]="state!=='running'">
  </app-timeline-player>

  <!-- LIST view: streamed steps -->
  <ng-container *ngIf="view==='list'">
    <div *ngFor="let s of steps; let last = last; trackBy: trackStep" class="step" #stepEl>
      <div class="t"><span class="num">Step {{ s.n }}</span>{{ s.text }}</div>
      <app-step-gallery *ngIf="stepImages(s).length" [images]="stepImages(s)"></app-step-gallery>
      <!-- no image at the final step of a failed run = the interruption point -->
      <div *ngIf="!s.image && last && state==='failed'" class="interrupted">
        <span class="warn">&#9888;</span> Interrupted here — the app crashed or the test stopped; no screenshot was captured.
      </div>
    </div>
  </ng-container>

  <div *ngIf="state==='passed' || state==='failed'" class="banner" [class.pass]="state==='passed'" [class.fail]="state==='failed'">
    <span>{{ state==='passed' ? '✔ All steps completed successfully.' : '✘ The test failed - see the last step above.' }}</span>
    <span class="actions">
      <button *ngIf="state==='failed' && test" class="retrybtn" (click)="retry()">&#8635; Retry test</button>
      <a *ngIf="report" [href]="reportUrl()" target="_blank" class="reportbtn">View full report &rarr;</a>
    </span>
  </div>
  `,
  styles: [`
    :host{display:block;font-family:Segoe UI,Arial,sans-serif;background:#0f172a;color:#e2e8f0;min-height:100vh}
    header{background:#1e293b;padding:20px 32px;border-bottom:3px solid #38bdf8;position:relative}
    .back{color:#38bdf8;text-decoration:none;font-size:14px} .back:hover{text-decoration:underline}
    h1{margin:8px 0 0;font-size:20px}
    .viewtoggle{position:absolute;right:32px;top:20px;display:flex;gap:6px}
    .viewtoggle button{background:#334155;color:#e2e8f0;border:none;padding:7px 14px;border-radius:8px;font-size:13px;cursor:pointer}
    .viewtoggle button.on{background:#38bdf8;color:#0f172a;font-weight:700}
    .state{margin:16px 32px;display:flex;align-items:center;font-size:15px}
    .spin{width:18px;height:18px;border:3px solid #334155;border-top-color:#38bdf8;border-radius:50%;margin-right:12px;animation:rot .8s linear infinite}
    @keyframes rot{to{transform:rotate(360deg)}}
    .done{font-size:22px;margin-right:10px} .pass{color:#4ade80} .fail{color:#f87171}
    .step{margin:0 32px 18px;background:#1e293b;border-radius:10px;padding:16px 20px;animation:fade .4s ease}
    @keyframes fade{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:none}}
    .step .num{display:inline-block;background:#38bdf8;color:#0f172a;font-weight:700;font-size:12px;padding:2px 10px;border-radius:6px;margin-right:10px}
    .step .t{font-size:15px;line-height:1.5;margin-bottom:10px}
    .step img{max-width:520px;border:1px solid #475569;border-radius:6px;display:block}
    .step .interrupted{background:#3b1d1d;border:1px solid #7f1d1d;color:#fca5a5;border-radius:8px;padding:14px 18px;font-size:14px;display:flex;align-items:center;gap:10px}
    .step .interrupted .warn{font-size:20px;color:#f87171}
    .banner{margin:0 32px 20px;padding:14px 20px;border-radius:10px;font-weight:600;display:flex;justify-content:space-between;align-items:center}
    .banner.pass{background:#166534;color:#bbf7d0} .banner.fail{background:#991b1b;color:#fecaca}
    .actions{display:flex;gap:10px;align-items:center}
    .retrybtn{background:#fbbf24;color:#0f172a;border:none;padding:8px 16px;border-radius:8px;font-weight:700;cursor:pointer}
    .retrybtn:hover{background:#f59e0b}
    .reportbtn{background:#38bdf8;color:#0f172a;padding:8px 16px;border-radius:8px;text-decoration:none;font-weight:700}
    .reportbtn:hover{background:#0ea5e9}
  `]
})
export class LiveReportComponent implements OnInit, OnDestroy {
  live?: LiveRun;
  steps: LiveStep[] = [];        // the rendered list - only GROWS, never replaced (no blink)
  videoFrames: LiveStep[] = [];  // steps expanded to one entry per album frame (for the video view)
  state: RunState = 'idle';
  title = 'Test';
  report = '';
  test = '';                     // which test ran (for Retry)
  view: ViewKind = 'list';       // list (streamed) | video (timeline player)

  @ViewChildren('stepEl') stepEls!: QueryList<ElementRef>;
  private sub?: Subscription;
  private opened = false;

  constructor(private api: TestApiService) {}

  // trackBy: identify a step by its number so Angular reuses existing DOM and only
  // appends new ones (this is what stops the whole list re-rendering/blinking each poll).
  trackStep(_i: number, s: LiveStep) { return s.n; }

  ngOnInit() { this.startPolling(); }

  private startPolling() {
    this.sub?.unsubscribe();
    this.sub = this.api.livePoll(1200).subscribe(l => {
      this.title = l.title;
      this.report = l.report;
      this.test = l.test;
      this.state = l.runState;

      const incoming = l.steps || [];

      // A fresh run resets the stream (server has fewer steps than we're showing) -> clear once.
      if (incoming.length < this.steps.length) { this.steps = []; this.opened = false; }

      // Update EXISTING steps in place when their content changed - this is what makes the view
      // FOLLOW a streaming burst album (its frames grow) or any step whose text/image updated.
      // trackBy: n keeps the DOM node, so only the changed bindings refresh (no flicker).
      const prevLen = this.steps.length;
      for (let i = 0; i < Math.min(prevLen, incoming.length); i++) {
        const cur = this.steps[i], inc = incoming[i];
        if (cur.text !== inc.text || cur.image !== inc.image ||
            (inc.images?.length || 0) !== (cur.images?.length || 0)) {
          this.steps[i] = inc;
        }
      }

      // Append genuinely-new steps and scroll to the newest (list view only).
      if (incoming.length > prevLen) {
        this.steps.push(...incoming.slice(prevLen));
        if (this.view === 'list') setTimeout(() => this.scrollToLast(), 60);
      }

      // Keep the video frames in sync (album steps expanded to one frame each). New reference so
      // the player picks up the change.
      this.videoFrames = this.expandFrames(this.steps);

      if (l.runState === 'passed' || l.runState === 'failed') {
        this.sub?.unsubscribe();
        // scroll to the completion banner (the bit we previously missed)
        setTimeout(() => this.scrollToEnd(), 120);
        if (!this.opened && l.report) { this.opened = true; window.open(this.api.reportUrl(l.report), '_blank'); }
      }
    });
  }
  ngOnDestroy() { this.sub?.unsubscribe(); }

  private scrollToLast() {
    const els = this.stepEls?.toArray();
    if (els?.length) els[els.length - 1].nativeElement.scrollIntoView({ behavior: 'smooth', block: 'end' });
  }
  /** Scroll all the way down so the PASS/FAIL completion banner is visible. */
  private scrollToEnd() {
    window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
  }

  img(file: string) { return this.api.reportUrl(file); }
  reportUrl() { return this.report ? this.api.reportUrl(this.report) : '#'; }

  /** All frames for a step: the burst-album frames if present, else the single image (or none). */
  stepImages(s: LiveStep): string[] {
    if (s.images && s.images.length) return s.images;
    return s.image ? [s.image] : [];
  }

  /** Expand multi-frame (album) steps into one entry per frame so the video plays through EVERY
   *  frame, each getting the per-frame dwell time. Single-image steps pass through unchanged. */
  private expandFrames(steps: LiveStep[]): LiveStep[] {
    const out: LiveStep[] = [];
    for (const s of steps) {
      const imgs = this.stepImages(s);
      if (imgs.length <= 1) { out.push(s); continue; }
      imgs.forEach((im, k) =>
        out.push({ n: s.n, text: `${s.text} · frame ${k + 1}/${imgs.length}`, image: im, group: true }));
    }
    return out;
  }

  /** Re-run the test that just failed, then resume live polling for the fresh run. */
  retry() {
    if (!this.test) return;
    const t = this.test;
    // reset local view so the new run streams from scratch
    this.steps = []; this.opened = false; this.state = 'running'; this.title = 'Retrying...';
    this.api.run(t).subscribe(() => this.startPolling());
  }
}
