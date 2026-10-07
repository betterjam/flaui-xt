import { Component, OnInit, OnDestroy, ChangeDetectionStrategy, signal, computed, effect, ViewChild, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Subscription } from 'rxjs';
import { TestApiService, RunStatus, TestInfo, LiveRun, RunData, LiveStep, TestVersion, RecordingStep } from '../services/test-api.service';
import { TimelinePlayerComponent } from './timeline-player.component';
import { ReportViewComponent } from './report-view.component';
import { ReviewViewComponent } from './review-view.component';
import { StepGalleryComponent } from './step-gallery.component';
import { FlowCaptureComponent } from './flow-capture.component';
import { DebugPanelComponent } from './debug-panel.component';
import { ImportRanorexComponent } from './import-ranorex.component';

type DetailTab = 'live' | 'video' | 'report' | 'review' | 'actions' | 'debug';

/**
 * TestSuiteComponent - the unified master-detail test suite, built REACTIVELY with signals +
 * OnPush change detection. The view derives everything from signals via `computed`, so it only
 * re-renders when the underlying data actually changes - image bindings stay stable (no blink).
 *
 * Sidebar (master) = test list + run. Detail = Live | Video | Report tabs. Reports render
 * natively in Angular from the run JSON (backend exports data only).
 */
@Component({
  selector: 'app-test-suite',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, TimelinePlayerComponent, ReportViewComponent, ReviewViewComponent, StepGalleryComponent, FlowCaptureComponent, DebugPanelComponent, ImportRanorexComponent],
  template: `
  <div class="suite">
    <aside class="sidebar">
      <div class="brand">ElevaUI Suite <span class="tag">Exatouch tests</span></div>
      <button class="creator" [class.on]="studio()" (click)="studio.set(!studio()); importing.set(false)">
        🔴 Flow Capture <span class="tag2">test creator</span></button>
      <button class="importbtn" [class.on]="importing()" (click)="importing.set(!importing()); studio.set(false)">
        📥 Import Ranorex <span class="tag2">.rxrec → tests</span></button>
      <button class="runall" [disabled]="running()" (click)="runAll()">&#9654; Run all</button>
      <div class="list">
        <!-- grouped: AREA -> base test (latest shown) -> nested versions (expand for history) -->
        <div *ngFor="let area of groups(); trackBy: trackArea" class="area">
          <div class="area-h">{{ area.label }} <span class="acount">{{ area.count }}</span></div>

          <div *ngFor="let fam of area.families; trackBy: trackFam" class="fam">
            <!-- family row = the LATEST version. Indented by its depth in the inheritance tree. -->
            <div class="item" [class.active]="fam.latest.method===selectedMethod()" [class.child]="fam.depth>0"
                 [style.paddingLeft.px]="10 + fam.depth*16" (click)="select(fam.latest.method)">
              <!-- branch chevron: fold/unfold this trunk's children (navigate down to the leaves) -->
              <button *ngIf="fam.hasChildren" class="branch" title="Fold/unfold this branch"
                      (click)="toggleBranch(fam.base, $event)">{{ fam.collapsed ? '▸' : '▾' }}</button>
              <span *ngIf="!fam.hasChildren && fam.depth>0" class="twig" title="extends {{ fam.extends }}">↳</span>
              <span class="badge" [ngClass]="badgeOf(fam.latest)">
                {{ (running() && runningTest()===fam.latest.method) ? '…' : (fam.latest.status==='PASS' ? '✔' : fam.latest.status==='FAIL' ? '✘' : '·') }}
              </span>
              <span class="name">{{ fam.pretty }}</span>
              <span *ngIf="fam.extends" class="exttag" title="extends {{ fam.extends }} — runs the trunk then this branch as one test">⑂ {{ fam.extends }}</span>
              <span *ngIf="fam.versions.length>1" class="vtag">v{{ fam.latest.version }}</span>
              <button *ngIf="fam.versions.length>1" class="exp" (click)="toggleFam(fam.base, $event)">
                {{ isFamOpen(fam.base) ? '▾' : '▸' }}</button>
              <!-- delete the whole test - only a LEAF (no branches extend it) can be removed -->
              <button *ngIf="!fam.hasChildren" class="delt" title="Delete this test"
                      (click)="deleteTest(fam.latest.method, fam.pretty, $event)">🗑</button>
            </div>
            <!-- nested versions (history) -->
            <div *ngIf="fam.versions.length>1 && isFamOpen(fam.base)" class="vers">
              <div *ngFor="let v of fam.versions; trackBy: trackTest" class="vitem"
                   [class.active]="v.method===selectedMethod()" (click)="select(v.method)">
                <span class="badge sm" [ngClass]="badgeOf(v)">
                  {{ v.status==='PASS' ? '✔' : v.status==='FAIL' ? '✘' : '·' }}</span>
                <span class="vname">v{{ v.version }}{{ v.version===fam.latest.version ? ' · latest' : '' }}</span>
                <!-- only the LATEST version is deletable (deleting an older one would orphan its child) -->
                <button *ngIf="(v.version ?? 1)>1 && v.version===fam.latest.version" class="del"
                        title="Delete this version" (click)="deleteVersion(fam.base, v.version ?? 1, $event)">🗑</button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </aside>

    <main class="detail" *ngIf="studio()"><app-flow-capture></app-flow-capture></main>
    <main class="detail" *ngIf="importing()"><app-import-ranorex (imported)="onImported()"></app-import-ranorex></main>

    <main class="detail" *ngIf="!studio() && !importing() && selected() as sel">
      <header>
        <div>
          <h2>{{ sel.pretty }}</h2>
          <div class="meta">
            <span class="badge pill" [ngClass]="detailBadge()">{{ detailLabel() }}</span>
            <!-- version picker: appears once a test has more than one version -->
            <div *ngIf="versions().length > 1" class="vpick">
              <label>version</label>
              <select [value]="curVersion()" (change)="pickVersion($event)">
                <option *ngFor="let v of versions()" [value]="v.version">
                  v{{ v.version }}{{ v.version===latestVersion() ? ' (latest)' : '' }}
                </option>
              </select>
            </div>
            <span *ngIf="sel.when" class="when">last run {{ sel.when }}</span>
          </div>
        </div>
        <div class="hbtns">
          <button *ngIf="!running()" class="run" (click)="runOne(sel.method)">
            {{ sel.status ? '↻ Re-run' : '▶ Run' }}</button>
          <button *ngIf="running()" class="run stopbtn" (click)="stopRun()">⏹ Stop run</button>
          <button class="export" (click)="exportRanorex(sel.method)" title="Export this test to Ranorex (.rxrec + .rxrep)">📤 Export to Ranorex</button>
        </div>
      </header>

      <!-- running animation: an indeterminate bar, visible from any tab while a run is in progress -->
      <div *ngIf="running()" class="runbar"><div class="runbar-fill"></div></div>

      <!-- highlight: a freshly-applied version is now live and auto-running + re-reviewing -->
      <div *ngIf="appliedBanner()" class="applied-banner">
        <span class="spin"></span>
        <span><b>{{ appliedBanner() }}</b> was applied — running it now, then the AI will re-review it automatically.</span>
        <button (click)="appliedBanner.set('')">Dismiss</button>
      </div>

      <nav class="tabs">
        <button [class.on]="tab()==='actions'" (click)="tab.set('actions')">📝 Steps &amp; Actions</button>
        <button [class.on]="tab()==='live'" (click)="tab.set('live')">Live</button>
        <button [class.on]="tab()==='video'" (click)="tab.set('video')">▶ Video</button>
        <button [class.on]="tab()==='report'" (click)="tab.set('report')">Report</button>
        <button [class.on]="tab()==='debug'" (click)="tab.set('debug')">🐞 Debug</button>
        <button [class.on]="tab()==='review'" (click)="tab.set('review')">🤖 AI Review</button>
      </nav>

      <section class="pane">
        <!-- LIVE -->
        <div *ngIf="tab()==='live'">
          <div class="state" *ngIf="isThisRunning()"><span class="spin"></span><span>Running this test...</span>
            <button class="stoprun" (click)="stopRun()">⏹ Stop run</button>
          </div>
          <div class="state" *ngIf="!isThisRunning() && !liveSteps().length">
            <span>Not running. Press Run to start, or see the Report tab for the last result.</span>
          </div>
          <div *ngFor="let s of liveSteps(); let last = last; trackBy: trackStep" class="lstep">
            <div class="t"><span class="num">Step {{ s.n }}</span>{{ s.text }}</div>
            <app-step-gallery *ngIf="stepImages(s).length" [images]="stepImages(s)"></app-step-gallery>
            <!-- only the LAST step of a FAILED run with no image is a real interruption point -->
            <div *ngIf="!stepImages(s).length && last && reportStatus()==='FAIL'" class="interrupted">
              <span class="warn">&#9888;</span> Interrupted here — the app stopped; no screenshot was captured.
            </div>
          </div>
          <!-- stop option after the last element (e.g. when a run is stuck on a modal) -->
          <button *ngIf="running() && liveSteps().length" class="stoprun wide" (click)="stopRun()">⏹ Stop this run</button>
          <div #liveEnd></div>   <!-- scroll anchor BELOW the stop button, so auto-scroll reveals the button -->
        </div>

        <!-- VIDEO -->
        <app-timeline-player *ngIf="tab()==='video'"
          [steps]="videoSteps()" [failed]="reportStatus()==='FAIL'"
          [followLive]="isThisRunning()" [autoplay]="!isThisRunning() && videoSteps().length>0">
        </app-timeline-player>

        <!-- REPORT (native) -->
        <app-report-view *ngIf="tab()==='report'" [run]="runData()"></app-report-view>

        <!-- STEPS & ACTIONS: the inputs/actions involved in this test (from its captured-flow map) -->
        <div *ngIf="tab()==='actions'" class="actions-pane">
          <div *ngIf="!recording().length" class="state">
            <span>No recorded action map for this test. Tests created in 🔴 Flow Capture list every input and action they captured here.</span>
          </div>
          <ng-container *ngIf="recording().length">
            <div class="sa-h">Inputs involved <span class="ct">{{ inputsInvolved().length }}</span></div>
            <div *ngIf="!inputsInvolved().length" class="muted">No field values were changed in this flow.</div>
            <div class="inputs">
              <div *ngFor="let s of inputsInvolved()" class="inp">
                <span class="fid">{{ s.key }}</span><span class="arrow">→</span><span class="fval">{{ s.value }}</span>
              </div>
            </div>
            <div class="sa-h">Full action sequence <span class="ct">{{ recording().length }}</span>
              <span *ngIf="inheritedCount()" class="inh-note">▾ {{ inheritedCount() }} inherited from the trunk, then this branch</span>
            </div>
            <div class="aclist">
              <div *ngFor="let s of recording(); trackBy: trackRec" class="aline"
                   [class.pic]="s.action==='picture'" [class.set]="s.action==='set'" [class.inh]="s.inherited">
                <span class="an">{{ s.n }}</span>
                <span *ngIf="s.inherited" class="frombadge" title="inherited from {{ s.from }}">⑂ {{ s.from }}</span>
                <span class="atag">{{ s.action }}</span>
                <span class="ax">{{ s.label }}</span>
              </div>
            </div>
          </ng-container>
        </div>

        <!-- DEBUG: pause/step/inspect a live run -->
        <app-debug-panel *ngIf="tab()==='debug'" [test]="sel.method"></app-debug-panel>

        <!-- AI REVIEW (per-test) -->
        <app-review-view *ngIf="tab()==='review'" [test]="sel.method"
          (versionApplied)="onVersionApplied($event)"></app-review-view>
      </section>
    </main>
    <main class="detail empty" *ngIf="!studio() && !importing() && !selected()"><p>Select a test from the left.</p></main>

    <!-- result summary modal: pops when a run finishes -->
    <div *ngIf="resultModal() as r" class="rm-overlay" (click)="resultModal.set(null)">
      <div class="rm" [class.pass]="r.status==='PASS'" [class.fail]="r.status==='FAIL'" (click)="$event.stopPropagation()">
        <div class="rm-icon">{{ r.status==='PASS' ? '✔' : '✘' }}</div>
        <h3>{{ r.status==='PASS' ? 'Test passed' : 'Test failed' }}</h3>
        <div class="rm-test">{{ pretty(r.test) }}</div>
        <div class="rm-meta">{{ r.steps }} step{{ r.steps===1 ? '' : 's' }} replayed</div>
        <div class="rm-msg" *ngIf="r.message">{{ r.message }}</div>
        <div class="rm-actions">
          <button class="rm-report" (click)="resultModal.set(null); tab.set('report')">View report</button>
          <button class="rm-close" (click)="resultModal.set(null)">Close</button>
        </div>
      </div>
    </div>
  </div>
  `,
  styles: [`
    :host{display:block;font-family:Segoe UI,Arial,sans-serif;background:#0f172a;color:#e2e8f0;min-height:100vh}
    .suite{display:grid;grid-template-columns:320px 1fr;min-height:100vh}
    .sidebar{background:#111827;border-right:1px solid #334155;padding:18px 14px;display:flex;flex-direction:column}
    .brand{font-size:16px;font-weight:700;margin-bottom:14px}
    .brand .tag{font-size:11px;background:#334155;padding:2px 8px;border-radius:6px;margin-left:6px;font-weight:400}
    .creator{background:#1e293b;color:#fca5a5;border:1px solid #dc2626;padding:9px;border-radius:8px;font-weight:700;cursor:pointer;margin-bottom:8px;text-align:left}
    .creator.on{background:#7f1d1d;color:#fff}
    .creator .tag2{font-size:10px;color:#94a3b8;font-weight:400;float:right;margin-top:2px}
    .creator.on .tag2{color:#fecaca}
    .importbtn{background:#1e293b;color:#c4b5fd;border:1px solid #7c3aed;padding:9px;border-radius:8px;font-weight:700;cursor:pointer;margin-bottom:8px;text-align:left}
    .importbtn.on{background:#4c1d95;color:#fff}
    .importbtn .tag2{font-size:10px;color:#94a3b8;font-weight:400;float:right;margin-top:2px}
    .hbtns{display:flex;gap:8px;align-items:center}
    .export{background:#1e293b;color:#c4b5fd;border:1px solid #7c3aed;border-radius:8px;padding:9px 14px;font-weight:700;cursor:pointer}
    .export:hover{background:#4c1d95;color:#fff}
    .runall{background:#38bdf8;color:#0f172a;border:none;padding:9px;border-radius:8px;font-weight:700;cursor:pointer;margin-bottom:14px}
    .runall:disabled{background:#475569;color:#94a3b8}
    .list{overflow:auto;flex:1}
    .area{margin-bottom:8px}
    .area-h{font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:#64748b;font-weight:700;padding:8px 6px 4px}
    .area-h .acount{background:#1e293b;color:#94a3b8;border-radius:10px;padding:0 7px;font-size:10px;margin-left:4px}
    .fam{margin-bottom:1px}
    .item{display:flex;align-items:center;gap:10px;padding:9px 10px;border-radius:8px;cursor:pointer}
    .item:hover{background:#1e293b} .item.active{background:#1e293b;outline:1px solid #38bdf8}
    .item.child{border-left:1px solid #334155}   /* a branch hangs off its trunk */
    .name{font-size:13px;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    .vtag{font-size:11px;background:#334155;color:#cbd5e1;border-radius:6px;padding:1px 7px;font-weight:700}
    .exp{background:none;border:none;color:#94a3b8;cursor:pointer;font-size:12px;padding:2px 4px}
    .branch{background:none;border:none;color:#7dd3fc;cursor:pointer;font-size:11px;padding:0;width:12px;flex:0 0 12px;text-align:center}
    .twig{color:#475569;font-size:12px;width:12px;flex:0 0 12px;text-align:center}
    .exttag{font-size:10px;color:#a5b4fc;background:#1e1b4b;border:1px solid #3730a3;border-radius:6px;padding:1px 6px;max-width:120px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    .vers{margin:2px 0 4px 18px;border-left:1px solid #334155;padding-left:6px}
    .vitem{display:flex;align-items:center;gap:8px;padding:6px 8px;border-radius:6px;cursor:pointer}
    .vitem:hover{background:#1e293b} .vitem.active{background:#1e293b;outline:1px solid #38bdf8}
    .vname{font-size:12px;color:#cbd5e1;flex:1}
    .del{background:none;border:none;color:#64748b;cursor:pointer;font-size:12px;opacity:0;padding:2px 4px}
    .vitem:hover .del{opacity:1} .del:hover{color:#f87171}
    .delt{background:none;border:none;color:#64748b;cursor:pointer;font-size:12px;opacity:0;padding:2px 4px}
    .item:hover .delt{opacity:.8} .delt:hover{color:#f87171;opacity:1}
    .badge.sm{min-width:16px;height:16px;font-size:10px}
    .badge{display:inline-flex;align-items:center;justify-content:center;min-width:22px;height:22px;border-radius:50%;font-size:12px;font-weight:700}
    .badge.pill{min-width:auto;height:auto;padding:2px 12px;border-radius:20px}
    .PASS{background:#166534;color:#bbf7d0} .FAIL{background:#991b1b;color:#fecaca}
    .RUNNING{background:#854d0e;color:#fde68a} .NONE{background:#334155;color:#94a3b8}
    .detail{padding:22px 28px} .detail.empty{display:flex;align-items:center;justify-content:center;color:#64748b}
    header{display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:14px}
    h2{margin:0;font-size:20px} .meta{margin-top:6px;display:flex;gap:10px;align-items:center}
    .when{color:#94a3b8;font-size:13px}
    .vpick{display:flex;align-items:center;gap:6px}
    .vpick label{color:#94a3b8;font-size:12px;text-transform:uppercase;letter-spacing:.04em}
    .vpick select{background:#1e293b;color:#e2e8f0;border:1px solid #475569;border-radius:6px;padding:3px 8px;font-size:13px;cursor:pointer}
    .runbar{height:4px;border-radius:3px;background:#1e293b;overflow:hidden;margin:0 0 14px}
    .runbar-fill{height:100%;width:35%;background:linear-gradient(90deg,#38bdf8,#22c55e);border-radius:3px;animation:slide 1.1s ease-in-out infinite}
    @keyframes slide{0%{margin-left:-35%}100%{margin-left:100%}}
    .rm-overlay{position:fixed;inset:0;background:rgba(2,6,23,.7);display:flex;align-items:center;justify-content:center;z-index:50;animation:fadein .2s ease}
    @keyframes fadein{from{opacity:0}to{opacity:1}}
    .rm{background:#0f172a;border:1px solid #334155;border-radius:16px;padding:30px 36px;text-align:center;max-width:460px;animation:pop .25s ease}
    @keyframes pop{from{transform:scale(.9);opacity:0}to{transform:scale(1);opacity:1}}
    .rm.pass{border-color:#22c55e;box-shadow:0 0 40px rgba(34,197,94,.25)}
    .rm.fail{border-color:#dc2626;box-shadow:0 0 40px rgba(220,38,38,.25)}
    .rm-icon{font-size:46px;line-height:1;margin-bottom:8px}
    .rm.pass .rm-icon{color:#4ade80} .rm.fail .rm-icon{color:#f87171}
    .rm h3{margin:0 0 4px;font-size:22px;color:#e2e8f0}
    .rm-test{color:#94a3b8;font-size:14px;text-transform:capitalize}
    .rm-meta{color:#64748b;font-size:13px;margin-top:6px}
    .rm-msg{margin-top:14px;background:#1e293b;border-radius:8px;padding:10px 14px;color:#cbd5e1;font-size:13px;line-height:1.5}
    .rm-actions{display:flex;gap:10px;justify-content:center;margin-top:20px}
    .rm-actions button{border:none;border-radius:8px;padding:9px 18px;font-weight:700;cursor:pointer}
    .rm-report{background:#38bdf8;color:#0f172a} .rm-close{background:#334155;color:#e2e8f0}
    .applied-banner{display:flex;align-items:center;gap:12px;background:#16241c;border:1px solid #22c55e;border-radius:10px;padding:12px 16px;margin-bottom:14px;color:#bbf7d0;font-size:14px}
    .applied-banner button{margin-left:auto;background:#334155;color:#e2e8f0;border:none;padding:6px 12px;border-radius:6px;cursor:pointer}
    .run{background:#38bdf8;color:#0f172a;border:none;padding:9px 18px;border-radius:8px;font-weight:700;cursor:pointer}
    .run.stopbtn{background:#dc2626;color:#fff}
    .run.stopbtn:hover{background:#991b1b}
    .run:disabled{background:#475569;color:#94a3b8}
    .tabs{display:flex;gap:6px;border-bottom:1px solid #334155;margin-bottom:16px}
    .tabs button{background:none;border:none;color:#94a3b8;padding:10px 16px;cursor:pointer;font-size:14px;border-bottom:2px solid transparent}
    .tabs button.on{color:#e2e8f0;border-bottom-color:#38bdf8;font-weight:700}
    .state{display:flex;align-items:center;gap:10px;color:#94a3b8;padding:10px 0 18px}
    .stoprun{background:#7f1d1d;color:#fecaca;border:1px solid #dc2626;border-radius:8px;padding:7px 14px;font-weight:700;cursor:pointer}
    .stoprun:hover{background:#991b1b} .stoprun.wide{display:block;margin:8px 0 20px}
    .spin{width:16px;height:16px;border:3px solid #334155;border-top-color:#38bdf8;border-radius:50%;animation:rot .8s linear infinite}
    @keyframes rot{to{transform:rotate(360deg)}}
    .lstep{margin:0 0 16px;background:#1e293b;border-radius:10px;padding:14px 18px;animation:fade .35s ease}
    @keyframes fade{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:none}}
    .lstep .num{display:inline-block;background:#38bdf8;color:#0f172a;font-weight:700;font-size:12px;padding:2px 10px;border-radius:6px;margin-right:10px}
    .lstep .t{font-size:15px;line-height:1.5;margin-bottom:10px}
    .lstep img{max-width:520px;border:1px solid #475569;border-radius:6px;display:block}
    .interrupted{background:#3b1d1d;border:1px solid #7f1d1d;color:#fca5a5;border-radius:8px;padding:12px 16px;display:flex;gap:10px;align-items:center;font-size:14px}
    .interrupted .warn{color:#f87171;font-size:18px}
    .sa-h{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:#64748b;font-weight:700;margin:18px 0 10px}
    .sa-h .ct{background:#1e293b;color:#94a3b8;border-radius:10px;padding:0 8px;margin-left:6px}
    .muted{color:#64748b;font-size:13px;margin-bottom:8px}
    .inputs{display:flex;flex-wrap:wrap;gap:8px}
    .inp{display:flex;align-items:center;gap:8px;background:#0b1220;border:1px solid #334155;border-radius:8px;padding:7px 12px;font-size:13px}
    .inp .fid{color:#7dd3fc;font-family:Consolas,monospace} .inp .arrow{color:#64748b} .inp .fval{color:#bbf7d0;font-weight:600}
    .aclist{display:flex;flex-direction:column;gap:4px}
    .aline{display:flex;align-items:baseline;gap:10px;background:#1e293b;border-radius:6px;padding:7px 12px;font-size:13px}
    .aline .an{color:#64748b;font-size:11px;min-width:22px;text-align:right;font-variant-numeric:tabular-nums}
    .aline .atag{font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:#94a3b8;background:#334155;border-radius:5px;padding:1px 7px;min-width:64px;text-align:center}
    .aline .ax{color:#e2e8f0}
    .aline.set{outline:1px solid #1e3a2e} .aline.set .atag{background:#14532d;color:#bbf7d0}
    .aline.pic .atag{background:#3b1d3b;color:#f0abfc}
    .aline.inh{background:#15172e;opacity:.85}   /* a step inherited from the trunk */
    .frombadge{font-size:9px;color:#a5b4fc;background:#1e1b4b;border:1px solid #3730a3;border-radius:5px;padding:0 5px;max-width:110px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    .inh-note{font-size:10px;color:#a5b4fc;text-transform:none;letter-spacing:0;font-weight:400;margin-left:8px}
  `]
})
export class TestSuiteComponent implements OnInit, OnDestroy {
  // --- reactive state (signals) ---
  private readonly status = signal<RunStatus | null>(null);
  private readonly live = signal<LiveRun | null>(null);
  readonly selectedMethod = signal<string | null>(null);
  readonly studio = signal(false);   // Flow Capture (test creator) takes over the detail pane when on
  readonly importing = signal(false); // Import-Ranorex view takes over the detail pane when on
  readonly tab = signal<DetailTab>('actions');
  readonly liveSteps = signal<LiveStep[]>([]);
  readonly runData = signal<RunData | null>(null);
  readonly recording = signal<RecordingStep[]>([]);   // captured-flow map for the Steps & Actions tab
  // versions of the currently-selected test (drives the version picker)
  readonly versions = signal<TestVersion[]>([]);
  readonly latestVersion = signal(0);
  readonly appliedBanner = signal('');   // shows the new version name while it auto-runs/re-reviews
  readonly resultModal = signal<{ status: string; test: string; steps: number; message: string } | null>(null);
  private lastRunState = '';
  readonly openFams = signal<Set<string>>(new Set());   // which version-families are expanded
  readonly collapsed = signal<Set<string>>(new Set());  // which inheritance branches are folded (default: all open -> leaves visible)

  // --- derived (computed) - the view binds these; they only change when inputs change ---
  readonly tests = computed<TestInfo[]>(() => this.status()?.tests ?? []);

  // group tests: AREA -> version-FAMILY (base) -> versions[]; the family row is the LATEST version.
  readonly groups = computed(() => {
    const areaLabels: Record<string, string> = { items: 'Items', navigation: 'Navigation', other: 'Other' };
    const order = ['items', 'navigation'];   // 'other' is forced last; imported folders sort alpha between
    const byArea = new Map<string, Map<string, TestInfo[]>>();
    for (const t of this.tests()) {
      const area = t.area ?? 'other';
      const base = t.base ?? t.method;
      if (!byArea.has(area)) byArea.set(area, new Map());
      const fams = byArea.get(area)!;
      if (!fams.has(base)) fams.set(base, []);
      fams.get(base)!.push(t);
    }
    // rank: known areas first (in `order`), then imported folders alphabetically, then 'other' last
    const rank = (a: string) => a === 'other' ? 1e6 : (order.indexOf(a) < 0 ? 1000 : order.indexOf(a));
    const areas = [...byArea.keys()].sort((a, b) => {
      const d = rank(a) - rank(b);
      return d !== 0 ? d : a.localeCompare(b);
    });
    const collapsed = this.collapsed();   // bases whose branch is folded (read so this recomputes on toggle)
    return areas.map(area => {
      // one node per version-family
      const nodes = [...byArea.get(area)!.entries()].map(([base, versions]) => {
        const sorted = [...versions].sort((a, b) => (a.version ?? 1) - (b.version ?? 1));
        const latest = sorted[sorted.length - 1];
        return { base, pretty: latest.pretty, versions: sorted, latest, extends: latest.extends ?? '', children: [] as any[] };
      });
      // wire the trunk->branch tree from each node's `extends` (parent base). A node whose parent isn't in
      // this area falls back to a root, so nothing ever disappears.
      const byBase = new Map(nodes.map(n => [n.base, n]));
      const roots: any[] = [];
      for (const n of nodes) { const p = n.extends ? byBase.get(n.extends) : null; if (p) p.children.push(n); else roots.push(n); }
      // depth-first flatten so the list reads trunk -> branches -> leaves; collapsed branches hide descendants
      const families: any[] = [];
      const walk = (n: any, depth: number, hidden: boolean) => {
        const hasChildren = n.children.length > 0;
        const isCollapsed = collapsed.has(n.base);
        if (!hidden) families.push({ ...n, depth, hasChildren, collapsed: isCollapsed });
        n.children.sort((a: any, b: any) => a.pretty.localeCompare(b.pretty)).forEach((c: any) => walk(c, depth + 1, hidden || isCollapsed));
      };
      roots.sort((a, b) => a.pretty.localeCompare(b.pretty)).forEach(r => walk(r, 0, false));
      return { key: area, label: areaLabels[area] ?? area, families, count: nodes.length };
    });
  });
  readonly running = computed(() => !!this.status()?.running);
  readonly runningTest = computed(() => this.live()?.test ?? '');
  readonly selected = computed<TestInfo | undefined>(() =>
    this.tests().find(t => t.method === this.selectedMethod()));
  readonly isThisRunning = computed(() =>
    this.running() && !!this.selected() && this.live()?.test === this.selectedMethod());
  readonly reportStatus = computed<'PASS' | 'FAIL' | ''>(() => this.runData()?.status ?? '');
  readonly videoSteps = computed<LiveStep[]>(() =>
    this.expandFrames(this.isThisRunning() ? this.liveSteps() : (this.runData()?.steps ?? [])));
  readonly curVersion = computed(() => this.versionOf(this.selectedMethod() ?? ''));
  readonly inputsInvolved = computed(() => this.recording().filter(s => s.action === 'set'));
  readonly inheritedCount = computed(() => this.recording().filter(s => s.inherited).length);

  @ViewChild('liveEnd') liveEnd?: ElementRef<HTMLElement>;

  private statusSub?: Subscription;
  private liveSub?: Subscription;
  private loadedFor = '';   // which test's runData is currently loaded (avoids refetch churn)

  constructor(private api: TestApiService) {
    // auto-scroll to the newest live step as it streams in (reacts to the liveSteps signal)
    effect(() => {
      this.liveSteps();                       // depend on step changes
      if (this.tab() !== 'live') return;
      setTimeout(() => this.liveEnd?.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'end' }), 50);
    });
  }

  ngOnInit() {
    this.statusSub = this.api.statusPoll(2000).subscribe(s => {
      this.status.set(s);                                  // signal equality skips no-op renders
      if (!this.selectedMethod() && s.tests.length) this.select(s.tests[0].method);
    });
    this.liveSub = this.api.livePoll(1200).subscribe(l => this.onLive(l));
  }
  ngOnDestroy() { this.statusSub?.unsubscribe(); this.liveSub?.unsubscribe(); }

  // --- selection --------------------------------------------------------------
  select(method: string) {
    this.studio.set(false);     // picking a test leaves the Flow Capture studio
    this.importing.set(false);  // ...and the Import view, so the test detail actually opens
    if (this.selectedMethod() === method) return;
    this.selectedMethod.set(method);
    this.liveSteps.set([]);
    this.recording.set([]);
    this.loadRunData(method, true);
    this.loadVersions(method);
    this.api.recording(method).subscribe(r => { if (this.selectedMethod() === method) this.recording.set(r || []); });
    this.tab.set('actions');   // Steps & Actions is the default tab
  }

  // --- versioning -------------------------------------------------------------
  private baseOf(method: string) { return method.replace(/_v\d+$/, ''); }
  private versionOf(method: string) { const m = method.match(/_v(\d+)$/); return m ? +m[1] : 1; }
  private loadVersions(method: string) {
    this.api.versionList(this.baseOf(method)).subscribe(v => {
      if (this.baseOf(this.selectedMethod() ?? '') !== v.base) return;
      this.versions.set(v.versions ?? []);
      this.latestVersion.set(v.latest ?? 0);
    });
  }
  pickVersion(e: Event) {
    const ver = +(e.target as HTMLSelectElement).value;
    const v = this.versions().find(x => x.version === ver);
    if (v) this.select(v.method);
  }
  /** A new version was applied (written + auto-running). Refresh the list and jump to it. */
  onVersionApplied(newMethod: string) {
    this.appliedBanner.set(this.prettyVersion(newMethod));   // highlight what's happening
    this.liveSteps.set([]); this.tab.set('live');            // take them to the Live run
    this.selectedMethod.set(newMethod);
    const next = new Set(this.openFams()); next.add(this.baseOf(newMethod)); this.openFams.set(next); // reveal it in the tree
    this.loadRunData(newMethod, true);
    // the version may take a moment to appear in /api/status (Get-Tests rescan); keep refreshing
    let tries = 0;
    const t = setInterval(() => {
      this.loadVersions(newMethod);
      if (this.versions().some(v => v.method === newMethod) || ++tries > 20) clearInterval(t);
    }, 1500);
  }
  private prettyVersion(method: string) { return `${this.baseOf(method)} v${this.versionOf(method)}`; }

  /** Fetch the saved run; only refetch when the test changes or a fresh run finishes. */
  private loadRunData(method: string, force = false) {
    if (!force && this.loadedFor === method && this.runData()) return;
    this.loadedFor = method;
    this.api.runData(method).subscribe(d => {
      if (this.selectedMethod() === method) this.runData.set(d.steps?.length ? d : null);
    });
  }

  // --- live stream ------------------------------------------------------------
  private autoFollowed: string | null = null;   // which running test we've already jumped to

  private onLive(l: LiveRun) {
    this.live.set(l);

    // Auto-follow ANY running test - whoever started it (dashboard, CLI, watchdog): jump to its
    // Live screen so you can watch the capture in real time. Switch only ONCE per run, then leave
    // the user free to browse; reset when the run ends so the next run auto-follows again.
    // A DEBUG run carries a flag - never auto-follow it to Live. Belt + suspenders: also never yank the
    // user away while they're ON the Debug tab.
    if (l.runState === 'running' && l.test && !l.debug && this.tab() !== 'debug') {
      if (this.autoFollowed !== l.test) {
        this.autoFollowed = l.test;
        this.select(l.test);     // select the running test (no-op if already selected)
        this.tab.set('live');    // force the Live tab regardless of saved status
      }
    } else if (l.runState !== 'running') {
      this.autoFollowed = null;
    }

    if (this.isThisRunning()) {
      const cur = this.liveSteps();
      const incoming = l.steps || [];
      if (incoming.length < cur.length) { this.liveSteps.set([]); }      // fresh run reset
      else {
        // Reconcile: update existing steps in place (so a streaming album grows from "starting..."
        // to its frames) AND append new ones. Only emit a new array reference when something
        // actually changed, so OnPush doesn't re-render needlessly.
        let changed = incoming.length > cur.length;
        const next = cur.slice();
        for (let i = 0; i < Math.min(cur.length, incoming.length); i++) {
          const a = cur[i], b = incoming[i];
          if (a.text !== b.text || a.image !== b.image ||
              (a.images?.length || 0) !== (b.images?.length || 0)) { next[i] = b; changed = true; }
        }
        if (incoming.length > cur.length) next.push(...incoming.slice(cur.length));
        if (changed) this.liveSteps.set(next);
      }
    }
    // on the running -> finished TRANSITION, pop a summary modal (once)
    const prev = this.lastRunState;
    this.lastRunState = l.runState;
    if (prev === 'running' && (l.runState === 'passed' || l.runState === 'failed')) {
      const steps = l.steps || [];
      this.resultModal.set({
        status: l.runState === 'passed' ? 'PASS' : 'FAIL',
        test: l.test || this.selectedMethod() || 'test',
        steps: steps.length,
        message: steps.length ? steps[steps.length - 1].text : '',
      });
    }

    if ((l.runState === 'passed' || l.runState === 'failed') && l.test && l.test === this.selectedMethod()) {
      this.loadRunData(l.test, true);                                    // refresh report after a run
      if (this.appliedBanner()) this.appliedBanner.set('');             // run done -> clear the highlight
    }
  }

  // --- actions ----------------------------------------------------------------
  // Run / Re-run always takes you to the Live tab so you watch the run immediately.
  runAll() { this.liveSteps.set([]); this.tab.set('live'); this.api.run().subscribe(); }
  runOne(method: string) { this.liveSteps.set([]); this.tab.set('live'); this.api.run(method).subscribe(); }
  stopRun() { this.api.stopRun().subscribe(); }
  pretty(method: string) { return (method || '').replace(/_v\d+$/, '').replace(/_/g, ' '); }
  /** a Ranorex import finished -> refresh the test list so the new tests appear. */
  onImported() { this.api.status().subscribe(s => this.status.set(s)); }
  /** export the current test to Ranorex and download the .rxrec + .rxrep. */
  exportRanorex(method: string) {
    this.api.exportRanorex(method).subscribe(r => {
      if (r.ok && r.rxrec) {
        window.open(this.api.ranorexFileUrl(r.rxrec), '_blank');
        if (r.rxrep) setTimeout(() => window.open(this.api.ranorexFileUrl(r.rxrep!), '_blank'), 300);
      } else { alert(r.error || 'Export needs a recording map — record or import this test first.'); }
    });
  }

  // --- view helpers -----------------------------------------------------------
  badgeOf(t: TestInfo): string {
    if (this.running() && this.runningTest() === t.method) return 'RUNNING';
    return t.status ?? 'NONE';
  }
  detailBadge(): string {
    if (this.isThisRunning()) return 'RUNNING';
    return this.selected()?.status ?? 'NONE';
  }
  detailLabel(): string {
    if (this.isThisRunning()) return 'running';
    return this.selected()?.status ? this.selected()!.status!.toLowerCase() : 'not run yet';
  }
  img(file: string) { return this.api.reportUrl(file); }
  /** All frames for a step: burst-album frames if present, else the single image (or none). */
  stepImages(s: LiveStep): string[] {
    if (s.images && s.images.length) return s.images;
    return s.image ? [s.image] : [];
  }

  /** For the video player: expand a multi-frame (album) step into one frame each, so playback
   *  walks through EVERY frame (dwelling the per-frame time on each) instead of only the first. */
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
  trackRec(_i: number, s: RecordingStep) { return s.n; }
  trackTest(_i: number, t: TestInfo) { return t.method; }
  trackStep(_i: number, s: LiveStep) { return s.n; }
  trackArea(_i: number, a: { key: string }) { return a.key; }
  trackFam(_i: number, f: { base: string }) { return f.base; }

  // delete a specific version (with confirm); if it was selected, fall back to the base test.
  deleteVersion(base: string, version: number, e: Event) {
    e.stopPropagation();
    if (!confirm(`Delete ${base} v${version}? This removes the test file and its results.`)) return;
    this.api.deleteVersion(base, version).subscribe(r => {
      if (r.blocked) { alert(r.blocked); return; }
      if (this.selectedMethod() === `${base}_v${version}`) this.selectedMethod.set(base);
      this.loadVersions(base);
      this.api.status().subscribe(s => this.status.set(s));   // refresh the tree immediately
    });
  }

  // version-family expand/collapse
  isFamOpen(base: string) { return this.openFams().has(base); }
  toggleFam(base: string, e: Event) {
    e.stopPropagation();
    const next = new Set(this.openFams());
    next.has(base) ? next.delete(base) : next.add(base);
    this.openFams.set(next);
  }
  /** Delete a whole test — only offered for leaves (no branches extend it). */
  deleteTest(method: string, pretty: string, e: Event) {
    e.stopPropagation();
    if (!confirm(`Delete the test "${pretty}"? This removes its test file, recorded map and run history. This cannot be undone.`)) return;
    this.api.deleteTest(method).subscribe(r => {
      if (r?.blocked) { alert(r.blocked); return; }
      if (this.selectedMethod() === method) this.selectedMethod.set(null);
      this.api.status().subscribe(s => this.status.set(s));   // refresh the tree so it disappears
    });
  }
  // fold/unfold an inheritance branch (trunk node) so you can navigate trunk -> branches -> leaves
  toggleBranch(base: string, e: Event) {
    e.stopPropagation();
    const next = new Set(this.collapsed());
    next.has(base) ? next.delete(base) : next.add(base);
    this.collapsed.set(next);
  }
}
