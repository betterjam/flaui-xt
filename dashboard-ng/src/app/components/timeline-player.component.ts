import {
  Component, Input, OnChanges, OnDestroy, AfterViewInit,
  ViewChild, ElementRef, SimpleChanges, computed,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { LiveStep } from '../services/test-api.service';
import { PlaybackEngine } from '../services/playback.service';
import { ViewModeService } from '../services/view-mode.service';

/**
 * TimelinePlayerComponent - a video-style player for a test run. Each step's screenshot is a
 * "frame"; the step text shows as a SUBTITLE bar (like video captions). Composes two focused
 * services so this component stays a thin VIEW:
 *   - PlaybackEngine   : play/pause/seek/speed (pure state machine)
 *   - ViewModeService  : normal / cinema / fullscreen sizing
 *
 * Reusable: feed it `steps` + `imgBase`. `followLive` makes it track the newest step while a
 * run streams; `autoplay` starts playback (e.g. when a finished run is shown for replay).
 */
@Component({
  selector: 'app-timeline-player',
  standalone: true,
  imports: [CommonModule],
  providers: [PlaybackEngine, ViewModeService],   // one instance per player
  template: `
  <ng-container *ngIf="steps?.length; else empty">
  <div class="player" [class.cinema]="mode()==='cinema'" [class.full]="mode()==='full'" #playerEl>
    <div class="frame">
      <!-- All images are stacked; only the current one is opaque. CSS opacity transition gives
           a TRUE image-to-image crossfade (old fades out while new fades in) - never through black. -->
      <img *ngFor="let s of steps; let i = index; trackBy: trackStep"
           [src]="url(s.image)" [alt]="'Step ' + s.n"
           class="layer" [class.show]="i === play.index() && !!s.image" />

      <!-- no image for the current step -->
      <div *ngIf="!current()?.image && isInterruptedHere()" class="interrupted layer show">
        <div class="icon">&#9888;</div>
        <div class="big">Interrupted here</div>
        <div class="sub">The app crashed or the test stopped — no screenshot was captured.</div>
      </div>
      <div *ngIf="!current()?.image && !isInterruptedHere()" class="noimg layer show">No screenshot for this step.</div>

      <div class="subtitle" [class.group]="current()?.group" *ngIf="current()?.image || !isInterruptedHere()">
        <span class="stepno">Step {{ current()?.n }}</span>{{ current()?.text }}
      </div>
    </div>

    <div class="controls">
      <button class="play" (click)="play.toggle()" [attr.aria-label]="play.playing() ? 'Pause' : 'Play'">
        {{ play.playing() ? '⏸' : '▶' }}
      </button>
      <span class="pos">{{ play.index() + 1 }} / {{ steps.length }}</span>

      <div class="scrub" (click)="seekAt($event)" role="slider"
           [attr.aria-valuenow]="play.index() + 1" [attr.aria-valuemax]="steps.length">
        <div class="track"></div>
        <div class="fill" [style.width.%]="play.progress() * 100"></div>
        <div class="tick" *ngFor="let s of steps; let i = index; trackBy: trackStep"
             [style.left.%]="tickPct(i)" [class.done]="i <= play.index()" [class.group]="s.group"
             (click)="play.seek(i); $event.stopPropagation()" [title]="'Step ' + (i + 1)"></div>
      </div>

      <button class="speed" (click)="play.cycleSpeed()">{{ play.speedSeconds() }}s/step</button>
      <button class="mode" [class.on]="mode()==='cinema'" (click)="view.toggleCinema()" title="Cinema mode">▭</button>
      <button class="mode" (click)="view.toggleFullscreen()" title="Fullscreen">⛶</button>
    </div>
  </div>
  </ng-container>
  <ng-template #empty><div class="empty">No steps to play yet.</div></ng-template>
  `,
  styles: [`
    :host{display:block}
    .player{margin:0 32px 24px;background:#000;border-radius:12px;overflow:hidden;border:1px solid #334155}
    .player.cinema{margin:0;border-radius:0;border-left:none;border-right:none}
    .player.cinema .frame{height:80vh} .player.cinema .frame img{max-height:80vh}
    .player.full{margin:0;border-radius:0;height:100vh;display:flex;flex-direction:column}
    .player.full .frame{flex:1;height:auto} .player.full .frame img.layer{max-height:none;max-width:100vw;height:100%;object-fit:contain}
    /* fixed frame height so a missing screenshot occupies the SAME space - no jump */
    .frame{position:relative;background:#000;height:62vh;min-height:300px;overflow:hidden}
    /* every image is an absolutely-positioned LAYER, faded by opacity. The current one is
       opaque; others are transparent. Changing index crossfades old->new, never via black. */
    .layer{position:absolute;inset:0;margin:auto;max-width:100%;max-height:100%;
      display:block;opacity:0;transition:opacity .45s ease-in-out;pointer-events:none}
    .layer.show{opacity:1;pointer-events:auto}
    /* no-image states fill the whole frame so the player keeps its dimensions */
    .noimg,.interrupted{display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center}
    .noimg{color:#64748b}
    .interrupted{color:#fecaca}
    .interrupted .icon{font-size:54px;line-height:1;margin-bottom:14px;color:#f87171}
    .interrupted .big{font-size:24px;font-weight:700;color:#fca5a5;margin-bottom:8px}
    .interrupted .sub{font-size:14px;color:#94a3b8;max-width:420px;margin:0 auto}
    .subtitle{position:absolute;left:0;right:0;bottom:0;padding:46px 22px 16px;
      background:linear-gradient(to top, rgba(0,0,0,.92) 0%, rgba(0,0,0,.75) 35%, rgba(0,0,0,.35) 70%, transparent 100%);
      color:#fff;font-size:17px;line-height:1.45;text-shadow:0 1px 4px rgba(0,0,0,.9)}
    .subtitle .stepno{display:inline-block;background:#38bdf8;color:#0f172a;font-weight:700;font-size:12px;
      padding:2px 9px;border-radius:6px;margin-right:10px;vertical-align:middle}
    /* a frame that is part of a multi-frame (album) step is tinted so it's obvious in the video */
    .subtitle.group{background:linear-gradient(to top, rgba(76,29,149,.92) 0%, rgba(76,29,149,.7) 35%, rgba(91,33,182,.3) 70%, transparent 100%)}
    .subtitle.group .stepno{background:#a78bfa}
    .subtitle.group::after{content:'GROUP';position:absolute;top:-22px;left:22px;background:#a78bfa;color:#1e1b4b;
      font-size:10px;font-weight:800;letter-spacing:.5px;padding:2px 8px;border-radius:5px}
    .controls{display:flex;align-items:center;gap:14px;padding:12px 18px;background:#0f172a}
    .play{background:#38bdf8;color:#0f172a;border:none;width:42px;height:42px;border-radius:50%;font-size:16px;cursor:pointer}
    .play:hover{background:#0ea5e9}
    .pos{color:#94a3b8;font-size:13px;min-width:54px}
    .speed{background:#334155;color:#e2e8f0;border:none;padding:7px 12px;border-radius:8px;font-size:12px;cursor:pointer}
    .mode{background:#334155;color:#e2e8f0;border:none;width:34px;height:34px;border-radius:8px;font-size:15px;cursor:pointer}
    .mode:hover{background:#475569} .mode.on{background:#38bdf8;color:#0f172a}
    .scrub{position:relative;flex:1;height:26px;cursor:pointer;display:flex;align-items:center}
    .track{position:absolute;left:0;right:0;height:5px;background:#334155;border-radius:3px}
    .fill{position:absolute;left:0;height:5px;background:#38bdf8;border-radius:3px;transition:width .25s linear}
    .tick{position:absolute;width:11px;height:11px;border-radius:50%;background:#475569;transform:translateX(-50%);cursor:pointer;border:2px solid #0f172a}
    .tick.done{background:#38bdf8}
    .tick.group{background:#6d28d9} .tick.group.done{background:#a78bfa}
    .empty{margin:0 32px;color:#94a3b8}
  `]
})
export class TimelinePlayerComponent implements OnChanges, AfterViewInit, OnDestroy {
  @Input() steps: LiveStep[] = [];
  @Input() imgBase = '/report/';
  @Input() autoplay = false;
  @Input() followLive = false;
  @Input() failed = false;          // the run ended in failure (last step = interruption point)

  @ViewChild('playerEl') playerEl?: ElementRef<HTMLElement>;

  /** The frame currently shown, derived from the engine's index. */
  readonly current = computed<LiveStep | undefined>(() => this.steps[this.play.index()]);

  constructor(public play: PlaybackEngine, public view: ViewModeService) {}

  /** Current size mode (normal/cinema/full), exposed for the template. */
  get mode() { return this.view.mode; }

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['steps']) return;
    this.play.setCount(this.steps.length);
    if (this.followLive) this.play.jumpToEnd();
    else if (this.autoplay && !this.play.playing() && this.steps.length) this.play.play();
  }

  ngAfterViewInit(): void {
    if (this.playerEl) this.view.attach(this.playerEl.nativeElement);
  }

  ngOnDestroy(): void { this.play.destroy(); this.view.detach(); }

  url(file: string): string { return this.imgBase + file; }
  // Track by position, not step number: an album step is expanded into many frames that share the
  // same step number, so n is no longer unique - the index is.
  trackStep(i: number, _s: LiveStep): number { return i; }

  /** True when the current frame is the failure point with no screenshot (the crash spot). */
  isInterruptedHere(): boolean {
    const isLast = this.play.index() === this.steps.length - 1;
    return this.failed && isLast && !this.current()?.image;
  }
  tickPct(i: number): number { return this.steps.length > 1 ? (i / (this.steps.length - 1)) * 100 : 0; }

  seekAt(ev: MouseEvent): void {
    const rect = (ev.currentTarget as HTMLElement).getBoundingClientRect();
    const ratio = (ev.clientX - rect.left) / rect.width;
    this.play.seek(Math.round(ratio * (this.steps.length - 1)));
  }
}
