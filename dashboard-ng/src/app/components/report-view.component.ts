import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { LiveStep, RunData } from '../services/test-api.service';
import { StepGalleryComponent } from './step-gallery.component';

/**
 * ReportViewComponent - renders a test run's report NATIVELY in Angular from the run JSON
 * (no separate C#-generated HTML file). The backend only exports data; this owns the HTML.
 * Shows the verdict, each step with its description + screenshot, and a clear "Interrupted
 * here" panel for a crash point with no screenshot. Click a screenshot to enlarge (lightbox).
 */
@Component({
  selector: 'app-report-view',
  standalone: true,
  imports: [CommonModule, StepGalleryComponent],
  template: `
  <ng-container *ngIf="run?.steps?.length; else empty">
    <div class="verdict" [class.pass]="run!.status==='PASS'" [class.fail]="run!.status==='FAIL'">
      <span class="mark">{{ run!.status==='PASS' ? '✔' : '✘' }}</span>
      <div>
        <div class="head">{{ run!.title || run!.test }}</div>
        <div class="sub">{{ run!.status==='PASS' ? 'All steps completed successfully.' : 'The test failed.' }}
          <span *ngIf="run!.finishedAt"> · {{ run!.finishedAt }}</span></div>
      </div>
    </div>

    <div *ngFor="let s of run!.steps; let last = last" class="step">
      <div class="t"><span class="num">Step {{ s.n }}</span>{{ s.text }}</div>
      <app-step-gallery *ngIf="stepImages(s).length" [images]="stepImages(s)" [imgBase]="imgBase"></app-step-gallery>
      <div *ngIf="!s.image && last && run!.status==='FAIL'" class="interrupted">
        <span class="warn">&#9888;</span> Interrupted here — the app crashed or the test stopped; no screenshot was captured.
      </div>
    </div>
  </ng-container>
  <ng-template #empty><div class="empty">No saved run for this test yet. Run it to generate a report.</div></ng-template>
  `,
  styles: [`
    :host{display:block;padding:8px 0}
    .verdict{display:flex;gap:14px;align-items:center;margin:0 0 18px;padding:14px 18px;border-radius:10px}
    .verdict.pass{background:#16653433;border:1px solid #166534} .verdict.fail{background:#991b1b33;border:1px solid #991b1b}
    .verdict .mark{font-size:26px} .verdict.pass .mark{color:#4ade80} .verdict.fail .mark{color:#f87171}
    .head{font-weight:700;font-size:16px} .sub{color:#94a3b8;font-size:13px;margin-top:2px}
    .step{margin:0 0 16px;background:#1e293b;border-radius:10px;padding:16px 18px}
    .step .num{display:inline-block;background:#38bdf8;color:#0f172a;font-weight:700;font-size:12px;padding:2px 10px;border-radius:6px;margin-right:10px}
    .step .t{font-size:15px;line-height:1.5;margin-bottom:10px}
    .step img{max-width:520px;border:1px solid #475569;border-radius:6px;display:block;cursor:zoom-in}
    .interrupted{background:#3b1d1d;border:1px solid #7f1d1d;color:#fca5a5;border-radius:8px;padding:14px 18px;font-size:14px;display:flex;align-items:center;gap:10px}
    .interrupted .warn{font-size:20px;color:#f87171}
    .empty{color:#94a3b8;padding:24px}
    .lb{position:fixed;inset:0;background:rgba(0,0,0,.9);display:flex;align-items:center;justify-content:center;cursor:zoom-out;z-index:100}
    .lb img{max-width:95vw;max-height:95vh;border:2px solid #38bdf8;border-radius:8px}
  `]
})
export class ReportViewComponent {
  @Input() run: RunData | null = null;
  @Input() imgBase = '/report/';

  img(file: string): string { return this.imgBase + file; }
  trackStep(_i: number, s: LiveStep): number { return s.n; }

  /** All frames for a step: burst-album frames if present, else the single image (or none). */
  stepImages(s: LiveStep): string[] {
    if (s.images && s.images.length) return s.images;
    return s.image ? [s.image] : [];
  }
}
