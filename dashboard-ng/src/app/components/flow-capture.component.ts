import { Component, OnInit, OnDestroy, ChangeDetectionStrategy, signal, computed, effect, inject, ViewChild, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TestApiService, LiveStep } from '../services/test-api.service';
import { TimelinePlayerComponent } from './timeline-player.component';

/** One open window/layer reported by the driver. */
interface WinInfo { handle: number; title: string; w: number; h: number; main: boolean; dialog?: boolean; selected?: boolean; }
/** The driver's scene state after each action. */
interface Scene {
  ok: boolean; screen: string; changed: boolean; keyboard: boolean; foreground: string;
  foregroundIsApp: boolean; file: string; note?: string;
  windowCount: number; extraWindows: number; disappeared: boolean; needsInput: boolean; windows: WinInfo[];
}

/**
 * Flow Capture - the LIVE TEST CREATOR. You drive Exatouch by hand; this panel records your real
 * interactions and turns them into a runnable test. It also watches the window stack: when a window
 * closes or several are open, it ASKS you where to continue (you pick the layer). Because your real
 * taps make the app hide its own keyboard, the recorded test is naturally keyboard-clean.
 *
 *   ● Start recording  -> the driver installs a mouse hook + focus listener
 *   (you click around the real app)
 *   📸 Picture          -> take a deliberate snapshot at any moment
 *   🔎 Find layers      -> list open modals/windows; pick one to continue on
 *   ■ Stop              -> freeze the captured steps
 *   💾 Create test      -> name it; the driver writes a replayable NUnit test
 */
@Component({
  selector: 'app-flow-capture',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, FormsModule, TimelinePlayerComponent],
  template: `
  <div class="fc">
    <header>
      <h2>🔴 Flow Capture <span class="sub">Live Test Creator</span></h2>
      <div class="head-right">
        <div class="conn" [class.on]="driverOnline()">
          <span class="dot"></span>{{ driverOnline() ? 'driver online' : 'driver offline' }}
        </div>
        <button class="restart" [disabled]="launching()" (click)="restartEngine()"
                title="Close Exatouch and relaunch the engine fresh (latest build) — a clean state to retry">
          {{ launching() ? '… restarting' : '↻ Restart engine' }}</button>
        <button class="stopstudio" *ngIf="driverOnline()" (click)="stopStudio()">⏹ Stop studio</button>
      </div>
    </header>

    <div *ngIf="!driverOnline()" class="offline">
      The live driver isn't running. Start it, then this panel connects automatically:
      <code>dotnet test --no-build --filter Run_automation_driver</code>
      <button (click)="launch()" [disabled]="launching()">{{ launching() ? 'starting…' : 'Start driver' }}</button>
    </div>

    <ng-container *ngIf="driverOnline()">
      <!-- toolbar -->
      <div class="toolbar">
        <button class="rec" [class.live]="recording()" [disabled]="replaying() || launching()" (click)="toggleRecord()">
          {{ recording() ? '■ Stop recording' : (extendOf() ? '● Start recording branch off ' + extendOf() : '● Start recording') }}
        </button>
        <button (click)="picture()" [disabled]="busy() || replaying()">📸 Picture</button>
        <button (click)="findLayers()" [disabled]="busy() || replaying()">🔎 Find layers</button>
        <button class="clear" (click)="clear()" [disabled]="busy() || replaying() || stepCount()===0">🗑 Clear</button>
        <label class="chk" title="If on, the generated test auto-closes validation modals during replay. Off = the test sees the popups it triggers.">
          <input type="checkbox" [checked]="autoDismiss()" (change)="toggleAutoDismiss($event)" /> Auto-dismiss modals
        </label>
        <span class="spacer"></span>
        <input class="name" [(ngModel)]="testName" placeholder="Name this test (e.g. Edit_price_with_SRP_modal)" />
        <button class="save" (click)="createTest()" [disabled]="busy() || recording() || stepCount()===0">💾 Create test</button>
      </div>

      <!-- ⑂ EXTEND: build a branch off an existing test (the trunk). We replay the parent to its end state,
           then you record only the NEW steps (the diff). Running the branch later replays trunk + branch as one. -->
      <div class="extendbar">
        <span class="eb-h">⑂ Extend a test:</span>
        <select [(ngModel)]="extendChoice" [disabled]="recording() || replaying() || launching()">
          <option value="">— none (record a new root test) —</option>
          <option *ngFor="let t of extendables()" [value]="t.base">{{ t.pretty }}{{ t.extends ? '  (↳ ' + t.extends + ')' : '' }}</option>
        </select>
        <button class="extend" [disabled]="!extendChoice || recording() || busy() || replaying() || launching()" (click)="extendTest()">▶ Replay trunk &amp; record branch</button>
        <span *ngIf="extendOf()" class="eb-on">⑂ branching off <b>{{ extendOf() }}</b> — your recording becomes <b>{{ extendOf() }} + these new steps</b></span>
      </div>

      <!-- while the parent replays, the controls are LOCKED -->
      <div *ngIf="replaying()" class="replaybar">
        <span class="spin"></span> ▶ Replaying the parent test… controls are locked until it reaches the branch point.
      </div>
      <!-- parent done: Exatouch is live at the branch point, waiting for you to record the child's new steps -->
      <div *ngIf="branchReady() && !recording()" class="branchready">
        ✅ Parent <b>{{ extendOf() }}</b> replayed — Exatouch is live at the branch point. Press
        <b>● Start recording</b> to capture your new steps; on <b>💾 Create test</b> the child becomes <b>{{ extendOf() }} + your steps</b>.
      </div>

      <!-- scene status -->
      <div class="scene" *ngIf="scene() as s">
        <span class="chip">screen: <b>{{ s.screen }}</b></span>
        <span class="chip" [class.warn]="s.keyboard">{{ s.keyboard ? '⌨ keyboard UP' : '⌨ clear' }}</span>
        <span class="chip">windows: <b>{{ s.windowCount }}</b></span>
        <span class="chip" [class.live]="recording()">{{ recording() ? '● REC · ' + stepCount() + ' steps' : 'idle' }}</span>
        <span class="chip" *ngIf="s.note">{{ s.note }}</span>
      </div>

      <!-- 🖥 SCREEN VISOR: live view of the layer we're about to capture; pick which layer -->
      <div class="visor">
        <div class="visor-h">
          🖥 Screen visor — <b>capturing: {{ visorLayer() || '…' }}</b>
          <span class="hint">this exact layer is what 📸 Picture will take</span>
        </div>
        <div class="visor-body">
          <img *ngIf="visorUrl()" [src]="visorUrl()" alt="live screen" />
          <div *ngIf="!visorUrl()" class="empty">connecting to the live screen…</div>
        </div>
        <div class="layerpick" *ngIf="layers().length">
          <span class="lp-h">Layers ({{ layers().length }}):</span>
          <button *ngFor="let w of layers()" class="lp" [class.sel]="w.selected" (click)="selectLayer(w.handle)">
            <span class="dotL" [class.on]="w.selected"></span>{{ w.title }}
            <span class="lp-d">{{ w.main ? 'main' : (w.dialog ? 'dialog' : 'window') }} · {{ w.w }}×{{ w.h }}</span>
          </button>
        </div>
      </div>

      <!-- AMBIGUITY: a window closed or several are open -> ask where to continue -->
      <div class="ask" *ngIf="(scene()?.needsInput || showLayers()) && (scene()?.windows?.length || 0) > 0">
        <div class="ask-h">
          <span class="warn">⚠</span>
          {{ scene()?.disappeared ? 'A window just closed — where do we continue?' : 'Multiple windows are open — which layer should we act on?' }}
        </div>
        <div class="layers">
          <button *ngFor="let w of scene()?.windows" class="layer" [class.main]="w.main" (click)="resume(w.handle)">
            <span class="lt">{{ w.title }}</span>
            <span class="ld">{{ w.main ? 'main form' : (w.dialog ? 'dialog / modal' : 'window') }} · {{ w.w }}×{{ w.h }}</span>
          </button>
        </div>
      </div>

      <!-- two panes: PICTURES play like a video; ACTIONS are a live text log -->
      <div class="panes">
        <!-- 🎬 pictures -> video viewer -->
        <div class="pane vid">
          <div class="pane-h">🎬 Pictures <span class="ct">{{ pictures().length }}</span></div>
          <app-timeline-player *ngIf="pictures().length; else noPics"
            [steps]="pictures()" [followLive]="recording()" [autoplay]="false">
          </app-timeline-player>
          <ng-template #noPics>
            <div class="empty">No pictures yet. Press <b>📸 Picture</b> at any moment to capture one — they play here like a video.</div>
          </ng-template>
        </div>

        <!-- 📝 action log -->
        <div class="pane log">
          <div class="pane-h">📝 Action log <span class="ct">{{ actions().length }}</span></div>
          <div *ngIf="actions().length===0" class="empty">
            Press <b>● Start recording</b>, then drive Exatouch by hand. Each click, tab and value change is written here as plain text.
          </div>
          <div class="lines" #logBox>
            <div *ngFor="let s of actions(); trackBy: trackStep" class="line">
              <span class="ln">{{ s.n }}</span><span class="lx">{{ s.text }}</span>
            </div>
            <div #logEnd></div>
          </div>
        </div>
      </div>
    </ng-container>
  </div>
  `,
  styles: [`
    :host{display:block}
    .fc{padding:8px 4px}
    header{display:flex;align-items:center;justify-content:space-between;margin-bottom:14px}
    h2{margin:0;font-size:20px} h2 .sub{font-size:12px;color:#94a3b8;background:#334155;padding:2px 8px;border-radius:6px;margin-left:8px;font-weight:400}
    .head-right{display:flex;align-items:center;gap:12px}
    .stopstudio{background:#7f1d1d;color:#fecaca;border:1px solid #dc2626;border-radius:8px;padding:7px 13px;font-size:12px;font-weight:700;cursor:pointer}
    .stopstudio:hover{background:#991b1b}
    .restart{background:#1e293b;color:#fbbf24;border:1px solid #b45309;border-radius:8px;padding:7px 13px;font-size:12px;font-weight:700;cursor:pointer}
    .restart:hover{background:#292524} .restart:disabled{opacity:.5;cursor:default}
    .conn{display:flex;align-items:center;gap:7px;font-size:12px;color:#94a3b8}
    .conn .dot{width:9px;height:9px;border-radius:50%;background:#64748b}
    .conn.on{color:#bbf7d0} .conn.on .dot{background:#22c55e;box-shadow:0 0 8px #22c55e}
    .offline{background:#1e293b;border:1px solid #475569;border-radius:10px;padding:16px;color:#cbd5e1;font-size:14px;display:flex;flex-direction:column;gap:10px;align-items:flex-start}
    .offline code{background:#0f172a;padding:6px 10px;border-radius:6px;color:#7dd3fc;font-family:Consolas,monospace}
    .offline button,.toolbar button,.save{cursor:pointer}
    .toolbar{display:flex;align-items:center;gap:10px;margin-bottom:12px;flex-wrap:wrap}
    .toolbar button{background:#334155;color:#e2e8f0;border:none;padding:9px 14px;border-radius:8px;font-size:13px}
    .toolbar button:disabled{opacity:.45;cursor:default}
    .rec{background:#dc2626 !important;color:#fff !important;font-weight:700}
    .clear{background:#3b1d1d !important;color:#fca5a5 !important}
    .chk{display:flex;align-items:center;gap:6px;font-size:12px;color:#cbd5e1;cursor:pointer;user-select:none}
    .chk input{cursor:pointer}
    .rec.live{background:#7f1d1d !important;animation:pulse 1.2s infinite}
    @keyframes pulse{50%{box-shadow:0 0 0 4px rgba(220,38,38,.3)}}
    .spacer{flex:1}
    .name{background:#0f172a;border:1px solid #475569;color:#e2e8f0;border-radius:8px;padding:9px 12px;font-size:13px;width:340px}
    .save{background:#22c55e !important;color:#06281a !important;font-weight:700}
    .extendbar{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-bottom:14px;background:#0b1220;border:1px solid #3730a3;border-radius:10px;padding:9px 12px}
    .extendbar .eb-h{font-size:12px;color:#a5b4fc;font-weight:700}
    .extendbar select{background:#0f172a;border:1px solid #475569;color:#e2e8f0;border-radius:8px;padding:7px 10px;font-size:13px;max-width:320px}
    .extendbar .extend{background:#4338ca;color:#e0e7ff;border:none;border-radius:8px;padding:8px 13px;font-size:13px;font-weight:700;cursor:pointer}
    .extendbar .extend:disabled{opacity:.45;cursor:default}
    .extendbar .eb-on{font-size:12px;color:#fecaca;background:#7f1d1d;border-radius:14px;padding:3px 11px;font-weight:600}
    .extendbar .eb-on b{color:#fff}
    .replaybar{display:flex;align-items:center;gap:10px;background:#1e1b4b;border:1px solid #4338ca;color:#c7d2fe;border-radius:10px;padding:11px 14px;margin-bottom:14px;font-size:13px;font-weight:600}
    .branchready{background:#16241c;border:1px solid #22c55e;color:#bbf7d0;border-radius:10px;padding:11px 14px;margin-bottom:14px;font-size:13px}
    .branchready b{color:#fff}
    .spin{width:14px;height:14px;border:2px solid #6366f1;border-top-color:transparent;border-radius:50%;display:inline-block;animation:spin .8s linear infinite}
    @keyframes spin{to{transform:rotate(360deg)}}
    .scene{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:14px}
    .chip{background:#1e293b;border:1px solid #334155;border-radius:20px;padding:4px 12px;font-size:12px;color:#cbd5e1}
    .chip.warn{background:#3b1d1d;border-color:#7f1d1d;color:#fca5a5}
    .chip.live{background:#7f1d1d;border-color:#dc2626;color:#fecaca;font-weight:700}
    .visor{background:#0b1220;border:1px solid #334155;border-radius:12px;padding:12px;margin-bottom:16px}
    .visor-h{font-size:13px;color:#cbd5e1;margin-bottom:10px} .visor-h b{color:#7dd3fc}
    .visor-h .hint{color:#64748b;font-size:12px;margin-left:8px}
    .visor-body{background:#000;border:1px solid #1e293b;border-radius:8px;min-height:140px;display:flex;align-items:center;justify-content:center;overflow:hidden}
    .visor-body img{max-width:100%;max-height:460px;display:block}
    .visor-body .empty{color:#475569;padding:30px}
    .layerpick{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-top:10px}
    .lp-h{font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:#64748b;font-weight:700}
    .lp{background:#1e293b;border:1px solid #475569;border-radius:8px;padding:7px 11px;cursor:pointer;color:#cbd5e1;font-size:12px;display:flex;align-items:center;gap:7px}
    .lp:hover{border-color:#38bdf8} .lp.sel{border-color:#22c55e;background:#16241c;color:#bbf7d0}
    .lp .lp-d{color:#64748b;font-size:11px} .lp.sel .lp-d{color:#86efac}
    .dotL{width:8px;height:8px;border-radius:50%;background:#475569} .dotL.on{background:#22c55e;box-shadow:0 0 7px #22c55e}
    .ask{background:#241c0f;border:1px solid #d97706;border-radius:10px;padding:14px 16px;margin-bottom:16px}
    .ask-h{font-size:14px;color:#fde68a;margin-bottom:10px;display:flex;align-items:center;gap:8px}
    .ask-h .warn{font-size:18px}
    .layers{display:flex;gap:10px;flex-wrap:wrap}
    .layer{background:#1e293b;border:1px solid #475569;border-radius:8px;padding:10px 14px;cursor:pointer;text-align:left;min-width:200px}
    .layer:hover{border-color:#38bdf8} .layer.main{border-color:#22c55e}
    .layer .lt{display:block;font-size:13px;color:#e2e8f0;font-weight:600}
    .layer .ld{display:block;font-size:11px;color:#94a3b8;margin-top:3px}
    .steps-h{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:#64748b;font-weight:700;margin-bottom:10px}
    .steps-h .ct{background:#1e293b;color:#94a3b8;border-radius:10px;padding:0 8px;margin-left:6px}
    .empty{color:#64748b;font-size:14px;background:#1e293b;border-radius:10px;padding:18px}
    @keyframes fade{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:none}}
    .panes{display:grid;grid-template-columns:1.4fr 1fr;gap:16px;align-items:start}
    .pane{background:#0b1220;border:1px solid #334155;border-radius:12px;padding:12px}
    .pane-h{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:#94a3b8;font-weight:700;margin-bottom:10px}
    .pane-h .ct{background:#1e293b;color:#94a3b8;border-radius:10px;padding:0 8px;margin-left:6px}
    .lines{max-height:460px;overflow:auto;display:flex;flex-direction:column;gap:4px}
    .line{display:flex;gap:10px;align-items:baseline;padding:7px 10px;background:#1e293b;border-radius:6px;font-size:13px;animation:fade .25s ease}
    .line .ln{color:#64748b;font-size:11px;min-width:22px;text-align:right;font-variant-numeric:tabular-nums}
    .line .lx{color:#e2e8f0;line-height:1.45}
    @media(max-width:1100px){.panes{grid-template-columns:1fr}}
  `]
})
export class FlowCaptureComponent implements OnInit, OnDestroy {
  private api = inject(TestApiService);
  private readonly DRIVER = 'http://localhost:5200';

  readonly driverOnline = signal(false);
  readonly recording = signal(false);
  readonly busy = signal(false);
  readonly launching = signal(false);
  readonly scene = signal<Scene | null>(null);
  readonly steps = signal<LiveStep[]>([]);
  readonly showLayers = signal(false);
  readonly autoDismiss = signal(false);  // generated test auto-dismisses modals? (off by default)
  readonly visorUrl = signal('');        // live visor image (overwritten each poll)
  readonly visorLayer = signal('');      // which layer the visor is showing / will capture
  readonly layers = signal<WinInfo[]>([]); // open layers for the chooser (with `selected`)
  readonly extendables = signal<{ base: string; pretty: string; extends: string }[]>([]); // tests you can branch off
  readonly extendOf = signal('');          // the parent test this recording is branching off (the trunk)
  readonly replaying = signal(false);      // the engine is replaying the parent - controls are LOCKED
  readonly branchReady = signal(false);    // parent replayed, app at the branch point, waiting for the user to record
  testName = '';
  extendChoice = '';                       // the dropdown selection (parent base to extend)

  readonly stepCount = computed(() => this.steps().length);
  // pictures = steps that carry an image (the video viewer); actions = text-only steps (the log)
  readonly pictures = computed(() => this.steps().filter(s => !!this.firstImg(s)));
  readonly actions = computed(() => this.steps().filter(s => !this.firstImg(s)));

  @ViewChild('logEnd') logEnd?: ElementRef<HTMLElement>;

  private sceneTimer?: any;
  private flowSub?: any;

  constructor() {
    // tail the action log: scroll to the newest line whenever the actions change
    effect(() => { this.actions(); setTimeout(() => this.logEnd?.nativeElement?.scrollIntoView({ behavior: 'smooth', block: 'end' }), 30); });
  }

  ngOnInit() {
    // While Flow Capture is alive, Exatouch must be open: probe the driver, auto-launch it if it's down.
    this.tick().then(() => { if (!this.driverOnline()) this.launch(); });
    this.sceneTimer = setInterval(() => this.tick(), 1500);
    // captured steps stream through the studio's OWN channel (not the test-run live file)
    this.flowSub = this.api.flowPoll(1000).subscribe(l => this.steps.set(l.steps || []));
    this.loadExtendables();   // tests you can extend (the trunks)
  }
  /** (Re)load the list of tests you can branch off — call after creating a test so it appears immediately. */
  private loadExtendables() {
    this.api.status().subscribe(s => {
      const seen = new Set<string>(); const list: { base: string; pretty: string; extends: string }[] = [];
      for (const t of (s.tests || [])) {
        const b = t.base ?? t.method; if (seen.has(b)) continue; seen.add(b);
        list.push({ base: b, pretty: t.pretty || b, extends: t.extends || '' });
      }
      this.extendables.set(list.sort((a, b) => a.pretty.localeCompare(b.pretty)));
      // default the picker to the universal root "Run the app" so every new test branches off it
      if (!this.extendChoice && !this.extendOf() && list.some(t => t.base === 'Run_the_app')) this.extendChoice = 'Run_the_app';
    });
  }
  ngOnDestroy() { clearInterval(this.sceneTimer); this.flowSub?.unsubscribe(); }

  private async tick() {
    try {
      const r = await fetch(`${this.DRIVER}/scene`, { method: 'POST' });
      this.scene.set(await r.json());
      this.driverOnline.set(true);
      // refresh the visor: a live frame of the SELECTED layer + the current layer list
      const pr = await fetch(`${this.DRIVER}/preview`, { method: 'POST' });
      const p = await pr.json();
      this.visorLayer.set(p.layer || '');
      this.layers.set(p.windows || []);
      this.visorUrl.set(this.api.reportUrl('visor.png') + '?t=' + Date.now());
    } catch { this.driverOnline.set(false); }
  }

  /** Poll until the driver answers again (after a launch/restart), or give up after maxTries. */
  private waitForDriver(maxTries = 40): Promise<void> {
    return new Promise(resolve => {
      let tries = 0;
      const t = setInterval(async () => { await this.tick(); if (this.driverOnline() || ++tries > maxTries) { clearInterval(t); resolve(); } }, 1500);
    });
  }

  private async post(path: string): Promise<any> {
    this.busy.set(true);
    try { const r = await fetch(`${this.DRIVER}${path}`, { method: 'POST' }); const j = await r.json(); if (j && j.screen !== undefined) this.scene.set(j); return j; }
    catch { this.driverOnline.set(false); return null; }
    finally { this.busy.set(false); }
  }

  /** Choose which layer the visor shows and the picture will capture. */
  async selectLayer(handle: number) {
    try {
      const r = await fetch(`${this.DRIVER}/select?handle=${handle}`, { method: 'POST' });
      const p = await r.json();
      this.visorLayer.set(p.layer || '');
      this.layers.set(p.windows || []);
      this.visorUrl.set(this.api.reportUrl('visor.png') + '?t=' + Date.now());
    } catch { }
  }

  async toggleRecord() {
    if (this.recording()) { await this.post('/record/stop'); this.recording.set(false); }
    else {
      // a fresh start clears any branch link; but if we just replayed a parent (extendOf set), KEEP it so
      // the recorded steps are saved as that test's branch.
      this.steps.set([]); this.branchReady.set(false);
      if (!this.extendOf()) this.extendChoice = '';
      await this.post('/record/start'); this.recording.set(true);
    }
  }
  /** Extend an existing test. We RESTART Exatouch fresh and replay the parent (trunk) to its end — with the
   *  controls LOCKED while it runs. When it finishes, control returns to you AT the branch point: press
   *  ● Start recording when you're ready to capture the branch, then 💾 Create test. */
  async extendTest() {
    const parent = this.extendChoice;
    if (!parent || this.recording() || this.launching() || this.replaying()) return;
    if (!confirm(`Extend "${parent}"?\n\nThe engine will restart Exatouch fresh and replay "${parent}" to its end (the branch point) with the controls locked. When it finishes you take over — press ● Start recording to capture your branch.`)) return;
    this.steps.set([]); this.extendOf.set(''); this.branchReady.set(false);
    // 1) restart for a clean, known starting state (every test starts at the fresh app)
    this.launching.set(true); this.driverOnline.set(false); this.visorUrl.set('');
    try { await fetch('/api/creator/restart', { method: 'POST' }); } catch { }
    await this.waitForDriver();
    this.launching.set(false);
    if (!this.driverOnline()) { alert('The engine did not come back up. Press ↻ Restart engine and try again.'); return; }
    // 2) replay the parent to the branch point — LOCK the controls while it runs
    this.replaying.set(true);
    const j = await this.post(`/record/extend?parent=${encodeURIComponent(parent)}`);
    this.replaying.set(false);
    // 3) hand control back to the user at the branch point (do NOT auto-record)
    if (j?.ok) { this.extendOf.set(parent); this.branchReady.set(true); }
    else alert((j?.note || j?.error || `Could not replay "${parent}" to extend it.`) +
               `\n\nIf this is an "unknown path" error, the driver is an older build — press ↻ Restart engine, then try again.`);
  }
  async picture() { await this.post('/picture?note=picture'); }
  async toggleAutoDismiss(e: Event) {
    const on = (e.target as HTMLInputElement).checked;
    this.autoDismiss.set(on);
    try { await fetch(`${this.DRIVER}/record/autodismiss?on=${on}`, { method: 'POST' }); } catch { }
  }
  /** Stop the whole studio: close the driver + Exatouch so the build is free to run tests in the suite. */
  async stopStudio() {
    if (!confirm('Stop the Flow Capture studio? This closes Exatouch and frees the build so you can run your recorded tests from the suite.')) return;
    if (this.recording()) { await this.post('/record/stop'); this.recording.set(false); }
    try { await fetch(`${this.DRIVER}/stop`, { method: 'POST' }); } catch { }
    this.driverOnline.set(false);
    this.visorUrl.set('');
  }
  async clear() {
    if (this.recording()) { await this.post('/record/stop'); this.recording.set(false); }
    await this.post('/record/clear'); this.steps.set([]); this.extendOf.set(''); this.extendChoice = ''; this.branchReady.set(false);
  }
  /** Restart the engine: close Exatouch + the driver and relaunch them fresh (latest build) — a clean state
   *  to retry a capture or an extend that left the app in the wrong place. */
  async restartEngine() {
    if (!confirm('Restart the capture engine? This closes Exatouch and relaunches it fresh (latest build) so you can retry from a clean state.')) return;
    this.recording.set(false); this.extendOf.set(''); this.extendChoice = '';
    this.steps.set([]); this.driverOnline.set(false); this.visorUrl.set(''); this.launching.set(true);
    try { await fetch('/api/creator/restart', { method: 'POST' }); } catch { }
    await this.waitForDriver();   // the driver relaunches + Exatouch reopens; poll until it answers
    this.launching.set(false); this.loadExtendables();
  }
  async findLayers() { this.showLayers.set(true); await this.post('/layers'); await this.tick(); }
  async resume(handle: number) { this.showLayers.set(false); await this.post(`/resume?handle=${handle}`); }
  async createTest() {
    const name = (this.testName || 'Recorded_flow').trim().replace(/[^A-Za-z0-9_]/g, '_');
    const cls = name.charAt(0).toUpperCase() + name.slice(1) + 'Test';
    const ext = this.extendOf();
    const j = await this.post(`/record/save?class=${encodeURIComponent(cls)}&name=${encodeURIComponent(name)}${ext ? '&extends=' + encodeURIComponent(ext) : ''}`);
    if (j?.ok) {
      // regenerate the whole inheritance chain on the dashboard so every test (incl. the parent) has the
      // shared BuildFlow the reference model needs to compile.
      try { await fetch(`/api/regenerate?test=${encodeURIComponent(name)}`, { method: 'POST' }); } catch { }
      alert(
        (ext
          ? `✅ Branch test created — extends "${ext}" (${j.total} steps total: ${j.steps} new + the trunk):\n`
          : `✅ Real FlaUI test created (${j.steps} steps):\n`) +
        `${j.file}\n\nDeterministic map saved:\n${j.map}\n\n` +
        `To run it: press ⏹ Stop studio to release the app, then pick "${name}" in the suite and Run — it builds automatically.`);
      this.extendOf.set(''); this.extendChoice = ''; this.branchReady.set(false);
      this.testName = '';
      this.loadExtendables();   // the new test becomes immediately available to extend
    }
  }
  async launch() {
    this.launching.set(true);
    try { await fetch('/api/creator/start', { method: 'POST' }); } catch { }
    await this.waitForDriver();
    this.launching.set(false);
  }

  img(file: string) { return this.api.reportUrl(file); }
  firstImg(s: LiveStep): string | null { return (s.images && s.images.length) ? s.images[0] : (s.image || null); }
  trackStep(_i: number, s: LiveStep) { return s.n + '|' + (s.image || ''); }
}
