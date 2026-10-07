import { Component, Input, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';

/**
 * StepGalleryComponent - renders a step's screenshot(s). One image behaves exactly like before
 * (thumbnail -> fullscreen lightbox). MANY images (a "burst album": a flurry of inputs captured
 * granularly and grouped into one step) render Messenger-style: a small mosaic you click to open a
 * prev/next gallery, instead of flooding the report with near-identical thumbnails.
 *
 * Reusable: feed it `[images]` (+ optional `imgBase`). Used by both the live stream and the saved
 * report views so grouping looks identical everywhere.
 */
@Component({
  selector: 'app-step-gallery',
  standalone: true,
  imports: [CommonModule],
  template: `
  <!-- single frame: thumbnail -> lightbox (unchanged behaviour) -->
  <img *ngIf="list.length === 1" class="single" [src]="url(list[0])" loading="lazy" (click)="openAt(0)" />

  <!-- album: collapsed mosaic of up to 4 tiles with a +N badge -->
  <ng-container *ngIf="list.length > 1">
    <div class="mosaic">
      <div class="cell" *ngFor="let im of visible; let i = index"
           [class.solo]="list.length === 3 && i === 0" (click)="openAt(i)">
        <img [src]="url(im)" loading="lazy" />
        <div class="more" *ngIf="i === visible.length - 1 && list.length > 4">+{{ list.length - 4 }}</div>
      </div>
    </div>
    <div class="album-label">&#128247; {{ list.length }} frames &middot; click to view all</div>
  </ng-container>

  <!-- lightbox / gallery overlay: backdrop or × closes; arrows / keys step through frames -->
  <div class="lb" *ngIf="zoomOpen" (click)="close()">
    <a class="x" (click)="close(); $event.stopPropagation()">&times;</a>
    <button *ngIf="list.length > 1" class="nav prev" (click)="prev($event)" aria-label="Previous">&#8249;</button>
    <img class="big" [src]="url(list[idx])" (click)="$event.stopPropagation()" />
    <button *ngIf="list.length > 1" class="nav next" (click)="next($event)" aria-label="Next">&#8250;</button>
    <div *ngIf="list.length > 1" class="counter" (click)="$event.stopPropagation()">{{ idx + 1 }} / {{ list.length }}</div>
  </div>
  `,
  styles: [`
    :host{display:block}
    .single{max-width:520px;border:1px solid #475569;border-radius:6px;display:block;cursor:zoom-in}
    .mosaic{display:grid;grid-template-columns:1fr 1fr;gap:3px;width:264px;border-radius:8px;overflow:hidden;border:1px solid #475569}
    .mosaic .cell{position:relative;height:96px;background:#0b1220;overflow:hidden;cursor:zoom-in}
    .mosaic .cell img{width:100%;height:100%;object-fit:cover;display:block}
    .mosaic .cell.solo{grid-column:1/3;height:150px}
    .mosaic .more{position:absolute;inset:0;background:rgba(2,6,23,.66);display:flex;align-items:center;justify-content:center;color:#e2e8f0;font-size:22px;font-weight:700}
    .album-label{font-size:12px;color:#7dd3fc;margin-top:6px}
    .lb{position:fixed;inset:0;background:rgba(0,0,0,.9);display:flex;align-items:center;justify-content:center;cursor:zoom-out;z-index:120}
    .lb .big{max-width:90vw;max-height:92vh;border:2px solid #38bdf8;border-radius:8px;cursor:default}
    .lb .x{position:fixed;top:14px;right:26px;color:#fff;font-size:34px;text-decoration:none;cursor:pointer;line-height:1}
    .lb .nav{position:fixed;top:50%;transform:translateY(-50%);background:rgba(15,23,42,.7);color:#e2e8f0;border:1px solid #475569;
      width:48px;height:64px;border-radius:10px;font-size:30px;cursor:pointer;display:flex;align-items:center;justify-content:center}
    .lb .nav:hover{background:#1e293b} .lb .nav.prev{left:20px} .lb .nav.next{right:20px}
    .lb .counter{position:fixed;bottom:22px;left:50%;transform:translateX(-50%);background:rgba(15,23,42,.85);
      color:#e2e8f0;padding:6px 14px;border-radius:20px;font-size:13px;font-family:Consolas,monospace}
  `]
})
export class StepGalleryComponent {
  @Input() set images(v: string[] | undefined | null) { this._imgs = (v || []).filter(Boolean); }
  get list(): string[] { return this._imgs; }
  private _imgs: string[] = [];

  @Input() imgBase = '/report/';

  zoomOpen = false;
  idx = 0;

  get visible(): string[] { return this.list.slice(0, 4); }   // mosaic shows at most 4 tiles

  url(file: string): string { return this.imgBase + file; }

  openAt(i: number): void { this.idx = Math.max(0, Math.min(i, this.list.length - 1)); this.zoomOpen = true; }
  close(): void { this.zoomOpen = false; }
  next(e: Event): void { e.stopPropagation(); if (this.list.length) this.idx = (this.idx + 1) % this.list.length; }
  prev(e: Event): void { e.stopPropagation(); if (this.list.length) this.idx = (this.idx - 1 + this.list.length) % this.list.length; }

  @HostListener('document:keydown', ['$event'])
  onKey(e: KeyboardEvent): void {
    if (!this.zoomOpen) return;
    if (e.key === 'Escape') this.close();
    else if (e.key === 'ArrowRight') this.next(e);
    else if (e.key === 'ArrowLeft') this.prev(e);
  }
}
