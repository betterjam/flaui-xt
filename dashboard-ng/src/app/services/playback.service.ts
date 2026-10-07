import { Injectable, signal, computed, Signal } from '@angular/core';

/**
 * PlaybackEngine - a reusable, framework-agnostic playback state machine for stepping through
 * a sequence (like a video). Owns ONLY playback concerns: current index, play/pause, speed,
 * seeking. It does not know about screenshots, the DOM, or HTTP - that separation keeps it
 * testable and reusable for any "play through N frames" UI.
 *
 * Built with Angular signals so the view reacts without manual change detection.
 */
@Injectable()
export class PlaybackEngine {
  private readonly _index = signal(0);
  private readonly _playing = signal(false);
  private readonly _count = signal(0);
  private readonly _speedMs = signal(2500);
  private timer: ReturnType<typeof setInterval> | null = null;

  /** Current frame index (0-based). */
  readonly index: Signal<number> = this._index.asReadonly();
  /** Whether playback is active. */
  readonly playing: Signal<boolean> = this._playing.asReadonly();
  /** Progress 0..1 across the sequence. */
  readonly progress = computed(() => {
    const n = this._count();
    return n > 1 ? this._index() / (n - 1) : 0;
  });
  /** Seconds per frame (derived for display). */
  readonly speedSeconds = computed(() => this._speedMs() / 1000);

  /** Tell the engine how many frames exist (call when the sequence length changes). */
  setCount(n: number): void {
    this._count.set(n);
    if (this._index() > n - 1) this._index.set(Math.max(0, n - 1));
  }

  play(): void {
    if (this._count() === 0) return;
    if (this._index() >= this._count() - 1) this._index.set(0); // replay from start
    this._playing.set(true);
    this.timer = setInterval(() => this.advance(), this._speedMs());
  }

  pause(): void {
    this._playing.set(false);
    if (this.timer) { clearInterval(this.timer); this.timer = null; }
  }

  toggle(): void { this._playing() ? this.pause() : this.play(); }

  /** Jump to the newest frame (used in live mode as frames stream in). */
  jumpToEnd(): void { this._index.set(Math.max(0, this._count() - 1)); }

  seek(i: number): void {
    this._index.set(Math.max(0, Math.min(i, this._count() - 1)));
  }

  /** Cycle through a small set of playback speeds. */
  cycleSpeed(): void {
    const speeds = [1000, 1500, 2500, 4000];
    const next = speeds[(speeds.indexOf(this._speedMs()) + 1) % speeds.length];
    this._speedMs.set(next);
    if (this._playing()) { this.pause(); this.play(); } // re-arm timer at the new speed
  }

  destroy(): void { this.pause(); }

  private advance(): void {
    if (this._index() < this._count() - 1) this._index.update(i => i + 1);
    else this.pause(); // reached the end
  }
}
