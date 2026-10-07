import { Injectable, signal, Signal } from '@angular/core';

/** YouTube-style display sizes for a media view. */
export type SizeMode = 'normal' | 'cinema' | 'full';

/**
 * ViewModeService - owns the size-mode state (normal/cinema/fullscreen) and the browser
 * Fullscreen API interaction for one element. Separated from the player so the playback
 * engine stays pure and this DOM concern is isolated and reusable.
 */
@Injectable()
export class ViewModeService {
  private readonly _mode = signal<SizeMode>('normal');
  readonly mode: Signal<SizeMode> = this._mode.asReadonly();

  private element: HTMLElement | null = null;
  private readonly onFsChange = () => {
    if (!document.fullscreenElement && this._mode() === 'full') this._mode.set('normal');
  };

  /** Bind the element that fullscreen acts on. Call once from the host component. */
  attach(el: HTMLElement): void {
    this.element = el;
    document.addEventListener('fullscreenchange', this.onFsChange);
  }
  detach(): void { document.removeEventListener('fullscreenchange', this.onFsChange); }

  /** Toggle cinema mode (re-clicking returns to normal). */
  toggleCinema(): void { this._mode.set(this._mode() === 'cinema' ? 'normal' : 'cinema'); }

  /** Enter/exit browser fullscreen. */
  toggleFullscreen(): void {
    if (!this.element) return;
    if (!document.fullscreenElement) {
      this.element.requestFullscreen?.().then(() => this._mode.set('full')).catch(() => {});
    } else {
      document.exitFullscreen?.();
    }
  }
}
