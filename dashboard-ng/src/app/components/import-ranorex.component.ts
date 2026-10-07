import { Component, OnInit, Output, EventEmitter, ChangeDetectionStrategy, signal, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TestApiService } from '../services/test-api.service';

interface Rec { name: string; full: string; folder: string; }

/**
 * Import Ranorex tests: browse the .rxrec recordings (grouped by folder), tick one or many, and Import.
 * Each is deterministically converted (RxPath @controlname -> our id, chain -> parent, tag -> type) into
 * our flow map AND a runnable FlaUI test - so they appear in the suite with all the engine resilience.
 */
@Component({
  selector: 'app-import-ranorex',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
  <div class="imp">
    <header>
      <h2>📥 Import Ranorex tests <span class="sub">{{ recs().length }} recordings</span></h2>
      <div class="acts">
        <button (click)="all()">Select all</button>
        <button (click)="none()">Clear</button>
        <button class="go" [disabled]="!selected().size || busy()" (click)="run()">
          {{ busy() ? 'Importing…' : 'Import ' + selected().size + ' selected' }}</button>
      </div>
    </header>

    <div *ngIf="result() as r" class="result">✅ Imported <b>{{ r.count }}</b> test(s): {{ r.imported.join(', ') }} — they're now in the suite (run-ready).</div>

    <div class="groups">
      <div *ngFor="let g of groups()" class="grp">
        <div class="grp-h" (click)="toggleFolder(g.folder)">
          <input type="checkbox" [checked]="folderAll(g)" (click)="$event.stopPropagation()" (change)="setFolder(g, $any($event.target).checked)" />
          {{ g.folder || '(root)' }} <span class="gc">{{ g.recs.length }}</span>
        </div>
        <div class="recs">
          <label *ngFor="let r of g.recs" class="rec" [class.on]="selected().has(r.full)">
            <input type="checkbox" [checked]="selected().has(r.full)" (change)="toggle(r.full)" />
            {{ r.name }}
          </label>
        </div>
      </div>
    </div>
  </div>
  `,
  styles: [`
    :host{display:block}
    .imp{padding:6px 2px}
    header{display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;flex-wrap:wrap;gap:10px}
    h2{margin:0;font-size:20px} .sub{font-size:12px;color:#94a3b8;background:#334155;padding:2px 8px;border-radius:6px;margin-left:8px;font-weight:400}
    .acts{display:flex;gap:8px}
    .acts button{background:#334155;color:#e2e8f0;border:none;border-radius:8px;padding:8px 13px;font-size:13px;cursor:pointer}
    .acts .go{background:#7c3aed;color:#fff;font-weight:700} .acts .go:disabled{opacity:.45;cursor:default}
    .result{background:#16241c;border:1px solid #22c55e;border-radius:10px;padding:12px 16px;color:#bbf7d0;font-size:13px;margin-bottom:12px}
    .groups{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:12px}
    .grp{background:#0b1220;border:1px solid #334155;border-radius:10px;padding:10px}
    .grp-h{display:flex;align-items:center;gap:8px;font-size:13px;color:#cbd5e1;font-weight:700;cursor:pointer;padding-bottom:6px;border-bottom:1px solid #1e293b;margin-bottom:6px}
    .grp-h .gc{margin-left:auto;background:#1e293b;color:#94a3b8;border-radius:10px;padding:0 7px;font-size:11px}
    .recs{display:flex;flex-direction:column;gap:2px;max-height:260px;overflow:auto}
    .rec{display:flex;align-items:center;gap:8px;padding:5px 7px;border-radius:6px;font-size:12px;color:#cbd5e1;cursor:pointer}
    .rec:hover{background:#1e293b} .rec.on{background:#16241c;color:#bbf7d0}
  `]
})
export class ImportRanorexComponent implements OnInit {
  @Output() imported = new EventEmitter<void>();
  private api = inject(TestApiService);
  readonly recs = signal<Rec[]>([]);
  readonly selected = signal<Set<string>>(new Set());
  readonly busy = signal(false);
  readonly result = signal<{ imported: string[]; count: number } | null>(null);

  readonly groups = computed(() => {
    const by = new Map<string, Rec[]>();
    for (const r of this.recs()) { const f = r.folder || ''; if (!by.has(f)) by.set(f, []); by.get(f)!.push(r); }
    return [...by.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([folder, recs]) => ({ folder, recs }));
  });

  ngOnInit() { this.api.ranorexList().subscribe(r => this.recs.set(r.recordings || [])); }

  toggle(full: string) { const s = new Set(this.selected()); s.has(full) ? s.delete(full) : s.add(full); this.selected.set(s); }
  all() { this.selected.set(new Set(this.recs().map(r => r.full))); }
  none() { this.selected.set(new Set()); }
  folderAll(g: { recs: Rec[] }) { return g.recs.length > 0 && g.recs.every(r => this.selected().has(r.full)); }
  setFolder(g: { recs: Rec[] }, on: boolean) { const s = new Set(this.selected()); g.recs.forEach(r => on ? s.add(r.full) : s.delete(r.full)); this.selected.set(s); }
  toggleFolder(folder: string) { const g = this.groups().find(x => x.folder === folder); if (g) this.setFolder(g, !this.folderAll(g)); }

  run() {
    this.busy.set(true);
    this.api.ranorexImport([...this.selected()]).subscribe(r => {
      this.busy.set(false);
      this.result.set({ imported: r.imported || [], count: r.count || 0 });
      this.imported.emit();   // tell the suite to refresh its test list
    });
  }
}
