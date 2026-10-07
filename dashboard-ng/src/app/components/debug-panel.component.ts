import { Component, Input, OnInit, OnDestroy, ChangeDetectionStrategy, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TestApiService, LiveStep } from '../services/test-api.service';
import { TimelinePlayerComponent } from './timeline-player.component';

interface DbgStatus { ok: boolean; paused: boolean; step: number; total: number; label: string; screen: string; file?: string; }
interface Match { id: string; name: string; ct: string; parentId: string; parentName: string; x: number; y: number; w: number; h: number; }
interface Layer { handle: number; title: string; w: number; h: number; main: boolean; }

/**
 * Interactive TEST DEBUGGER panel. "Debug run" launches the selected test with the engine's pause/inspect
 * server (:5201). You can PAUSE at a precise instant, read the LIVE status (current step, screen, open
 * layers/modals, and any element by name), STEP one action at a time, then resume - so we can troubleshoot
 * the exact moment a step diverges or a modal blocks it.
 */
@Component({
  selector: 'app-debug-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, FormsModule, TimelinePlayerComponent],
  template: `
  <div class="dbg">
    <div class="row top">
      <button class="run" (click)="debugRun()" [disabled]="online()">▶ Debug run "{{ test }}"</button>
      <div class="conn" [class.on]="online()"><span class="dot"></span>{{ online() ? 'paused engine connected (:5201)' : 'no debug run' }}</div>
    </div>

    <div *ngIf="!online()" class="hint">
      Stop the studio first (it locks the build), then press <b>▶ Debug run</b>. Once it's running, the
      controls below light up — hit <b>⏸ Pause</b> at the instant you want to inspect.
    </div>

    <!-- controls are ALWAYS shown (disabled until a debug run is connected) -->
    <div class="status" *ngIf="online() && st() as s" [class.paused]="s.paused">
      <span class="chip big">{{ s.paused ? '⏸ PAUSED' : '▶ running' }}</span>
      <span class="chip">step <b>{{ s.step }}</b> / {{ s.total }}</span>
      <span class="chip">screen: <b>{{ s.screen }}</b></span>
      <span class="chip wide">next: {{ s.label }}</span>
    </div>

    <div class="row controls">
      <button (click)="cmd('/pause')" [disabled]="!online() || st()?.paused">⏸ Pause</button>
      <button (click)="cmd('/step')" [disabled]="!online()">⤼ Step one</button>
      <button (click)="cmd('/resume')" [disabled]="!online() || !st()?.paused">▶ Resume</button>
      <button class="stop" (click)="cmd('/stop')" [disabled]="!online()">⏹ Stop</button>
    </div>

    <ng-container *ngIf="online() && st() as s">
      <div class="panes">
        <div class="pane">
          <div class="ph">🔎 Inspect an element (live)</div>
          <div class="row">
            <input [(ngModel)]="query" placeholder="name e.g. Pay, or leave blank" (keyup.enter)="inspect()" />
            <button (click)="inspect()">Inspect</button>
            <button (click)="loadLayers()">Layers</button>
            <button [class.on]="pointing()" (click)="togglePoint()" title="Point at an element in the app to identify it">🎯 Point-inspect</button>
          </div>
          <div *ngIf="atEl() as a" class="atcard">
            <span class="lbl">under cursor →</span>
            <b class="nm">{{ a.name || '(no name)' }}</b> <span class="ct">{{ a.ct }}</span>
            <span class="mono">id={{ a.id || '—' }}</span> <span class="mono">parent={{ a.parentId || a.parentName || '—' }}</span>
          </div>
          <div *ngIf="matches().length" class="tbl">
            <div class="tr th"><span>name</span><span>type</span><span>id</span><span>parent</span><span>pos</span></div>
            <div *ngFor="let m of matches()" class="tr" [class.hit]="!!atEl()?.id && m.id === atEl()?.id">
              <span class="nm">{{ m.name || '—' }}</span><span>{{ m.ct }}</span><span class="mono">{{ m.id || '—' }}</span>
              <span class="mono">{{ m.parentId || m.parentName || '—' }}</span><span class="mono">{{ m.x }},{{ m.y }}</span>
            </div>
          </div>
          <div *ngIf="layers().length" class="layers">
            <div class="ph2">Open layers</div>
            <div *ngFor="let w of layers()" class="layer">{{ w.title }} <span class="mono">{{ w.w }}×{{ w.h }}</span>{{ w.main ? ' · main' : '' }}</div>
          </div>
        </div>
        <div class="pane">
          <div class="ph">🎬 Live frames</div>
          <app-timeline-player *ngIf="frames().length; else noframes"
            [steps]="frames()" [followLive]="!st()?.paused" [autoplay]="false">
          </app-timeline-player>
          <ng-template #noframes><div class="empty">Frames appear here as the run steps (fixed player — no scroll jumping).</div></ng-template>
        </div>
      </div>
    </ng-container>
  </div>
  `,
  styles: [`
    :host{display:block}
    .dbg{padding:6px 2px}
    .row{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
    .top{margin-bottom:10px}
    .run{background:#7c3aed;color:#fff;border:none;border-radius:8px;padding:9px 14px;font-weight:700;cursor:pointer}
    .run:disabled{opacity:.45;cursor:default}
    .conn{display:flex;align-items:center;gap:7px;font-size:12px;color:#94a3b8}
    .conn .dot{width:9px;height:9px;border-radius:50%;background:#64748b}
    .conn.on{color:#bbf7d0} .conn.on .dot{background:#22c55e;box-shadow:0 0 8px #22c55e}
    .hint{background:#1e293b;border:1px solid #475569;border-radius:10px;padding:14px;color:#cbd5e1;font-size:13px}
    .status{display:flex;gap:8px;flex-wrap:wrap;align-items:center;background:#0b1220;border:1px solid #334155;border-radius:10px;padding:10px 12px;margin-bottom:10px}
    .status.paused{border-color:#d97706}
    .chip{background:#1e293b;border:1px solid #334155;border-radius:18px;padding:4px 12px;font-size:12px;color:#cbd5e1}
    .chip.big{font-weight:700;color:#fde68a;background:#241c0f;border-color:#d97706}
    .chip.wide{flex:1;min-width:200px;color:#e2e8f0}
    .controls button{background:#334155;color:#e2e8f0;border:none;border-radius:8px;padding:8px 13px;font-size:13px;cursor:pointer}
    .controls button:disabled{opacity:.4;cursor:default}
    .controls .stop{background:#7f1d1d;color:#fecaca}
    .panes{display:grid;grid-template-columns:1.2fr 1fr;gap:14px;margin-top:12px}
    .pane{background:#0b1220;border:1px solid #334155;border-radius:10px;padding:12px}
    .ph{font-size:12px;text-transform:uppercase;letter-spacing:.05em;color:#94a3b8;font-weight:700;margin-bottom:8px}
    .ph2{font-size:11px;color:#64748b;margin:10px 0 4px;text-transform:uppercase}
    .pane input{background:#0f172a;border:1px solid #475569;color:#e2e8f0;border-radius:7px;padding:7px 10px;font-size:13px;flex:1}
    .pane button{background:#334155;color:#e2e8f0;border:none;border-radius:7px;padding:7px 11px;font-size:12px;cursor:pointer}
    .tbl{margin-top:10px;font-size:12px}
    .tr{display:grid;grid-template-columns:1.3fr .9fr 1fr 1fr .7fr;gap:8px;padding:6px 8px;border-radius:6px}
    .tr.th{color:#64748b;text-transform:uppercase;font-size:10px}
    .tr:not(.th){background:#1e293b;margin-bottom:3px}
    .tr .nm{color:#e2e8f0} .mono{font-family:Consolas,monospace;color:#7dd3fc}
    .tr.hit{outline:2px solid #22c55e;background:#16241c !important}
    .pane button.on{background:#7c3aed;color:#fff}
    .atcard{margin-top:10px;padding:8px 12px;background:#241c0f;border:1px solid #d97706;border-radius:8px;font-size:12px;color:#fde68a;display:flex;gap:10px;align-items:center;flex-wrap:wrap}
    .atcard .lbl{color:#94a3b8} .atcard .nm{color:#fff} .atcard .ct{color:#a78bfa}
    .layer{font-size:12px;color:#cbd5e1;padding:4px 8px;background:#1e293b;border-radius:6px;margin-bottom:3px}
    .pane img{max-width:100%;border:1px solid #334155;border-radius:6px;display:block}
    .empty{color:#64748b;font-size:13px;padding:20px}
  `]
})
export class DebugPanelComponent implements OnInit, OnDestroy {
  @Input() test = '';
  private api = inject(TestApiService);
  private readonly DBG = 'http://localhost:5201';

  readonly online = signal(false);
  readonly st = signal<DbgStatus | null>(null);
  readonly matches = signal<Match[]>([]);
  readonly layers = signal<Layer[]>([]);
  readonly frames = signal<LiveStep[]>([]);   // the run's frames, shown in the fixed video player
  readonly atEl = signal<Match | null>(null);  // element under the cursor (point-inspect)
  readonly pointing = signal(false);
  query = '';
  private timer: any;
  private liveSub: any;
  private atTimer: any;

  ngOnInit() {
    this.poll();
    this.timer = setInterval(() => this.poll(), 1000);
    // feed the timeline player from the run's live stream (fixed viewport - no growing image list)
    this.liveSub = this.api.livePoll(1000).subscribe(l => this.frames.set(l.steps || []));
  }
  ngOnDestroy() { clearInterval(this.timer); clearInterval(this.atTimer); this.liveSub?.unsubscribe(); }

  togglePoint() {
    const on = !this.pointing(); this.pointing.set(on);
    if (on) this.atTimer = setInterval(() => this.atPoll(), 350);
    else { clearInterval(this.atTimer); this.atEl.set(null); }
  }
  private async atPoll() {
    try { const r = await fetch(`${this.DBG}/at`); const j = await r.json(); this.atEl.set(j.ok ? j : null); } catch { this.atEl.set(null); }
  }

  private async poll() {
    try { const r = await fetch(`${this.DBG}/status`); this.st.set(await r.json()); this.online.set(true); }
    catch { this.online.set(false); this.st.set(null); }
  }
  async debugRun() { try { await fetch(`/api/debug/run?test=${encodeURIComponent(this.test)}`, { method: 'POST' }); } catch { } }
  async cmd(p: string) { try { await fetch(`${this.DBG}${p}`, { method: 'POST' }); await this.poll(); } catch { } }
  async inspect() {
    try { const r = await fetch(`${this.DBG}/inspect?name=${encodeURIComponent(this.query)}`); this.matches.set((await r.json()).matches || []); this.layers.set([]); } catch { }
  }
  async loadLayers() { try { const r = await fetch(`${this.DBG}/layers`); this.layers.set((await r.json()).windows || []); this.matches.set([]); } catch { } }
}
