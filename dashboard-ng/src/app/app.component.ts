import { Component, OnInit, OnDestroy } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet></router-outlet>',
})
export class AppComponent implements OnInit, OnDestroy {
  // Auto-reload: poll the served UI build id; when a new build is deployed, reload once so you never
  // have to hard-refresh during iteration.
  private baseline: string | null = null;
  private timer?: any;

  ngOnInit() {
    this.timer = setInterval(() => this.check(), 3000);
    this.check();
  }
  ngOnDestroy() { clearInterval(this.timer); }

  private async check() {
    try {
      const v = (await (await fetch('/api/uiversion?t=' + Date.now())).json()).v as string;
      if (!v) return;
      if (this.baseline === null) { this.baseline = v; return; }   // remember the version we booted on
      if (v !== this.baseline) { this.baseline = v; location.reload(); }   // new build -> refresh once
    } catch { }
  }
}
