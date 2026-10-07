import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { TestApiService, RunStatus } from '../services/test-api.service';

/**
 * The dashboard: lists every test with status, a Run-all button, and a per-test Run button.
 * Polls the shared service for live status. Clicking Run navigates to the live report view.
 */
@Component({
  selector: 'app-test-list',
  standalone: true,
  imports: [CommonModule],
  template: `
  <header>
    <h1>ElevaUI Suite <span class="tag">Exatouch tests</span></h1>
    <button [disabled]="status?.running" (click)="runAll()">&#9654; Run all tests</button>
  </header>

  <div class="bar">
    <span class="dot" [class.live]="status?.running"></span>
    <span>{{ status?.running ? 'Running tests...' : (status?.finishedAt ? 'Last run finished ' + status?.finishedAt : 'Idle') }}</span>
  </div>

  <table>
    <tr><th>Test</th><th>Status</th><th>Last run</th><th>Report</th><th></th></tr>
    <tr *ngFor="let t of status?.tests" [class.running]="status?.running">
      <td><strong>{{ t.pretty }}</strong></td>
      <td><span class="badge" [ngClass]="status?.running ? 'RUNNING' : (t.status || 'NONE')">
        {{ status?.running ? 'RUNNING' : (t.status || 'NONE') }}</span></td>
      <td>{{ t.when || '-' }}</td>
      <td><a *ngIf="t.report" [href]="api.reportUrl(t.report)" target="_blank">open report</a><span *ngIf="!t.report">-</span></td>
      <td><button class="small" [disabled]="status?.running" (click)="runOne(t.method)">Run</button></td>
    </tr>
  </table>
  `,
  styles: [`
    :host{display:block;font-family:Segoe UI,Arial,sans-serif;background:#0f172a;color:#e2e8f0;min-height:100vh}
    header{background:#1e293b;padding:20px 32px;border-bottom:3px solid #38bdf8;display:flex;justify-content:space-between;align-items:center}
    h1{margin:0;font-size:20px} .tag{font-size:12px;background:#334155;padding:3px 10px;border-radius:6px;margin-left:8px;font-weight:400}
    button{background:#38bdf8;color:#0f172a;border:none;padding:10px 20px;border-radius:8px;font-weight:700;cursor:pointer}
    button:hover{background:#0ea5e9} button:disabled{background:#475569;color:#94a3b8;cursor:not-allowed}
    .small{background:#334155;color:#e2e8f0;padding:6px 12px;font-size:12px}
    .bar{margin:16px 32px;color:#94a3b8;font-size:13px;display:flex;align-items:center}
    .dot{width:9px;height:9px;border-radius:50%;background:#64748b;margin-right:8px} .dot.live{background:#4ade80}
    table{width:calc(100% - 64px);margin:0 32px 32px;border-collapse:collapse;background:#1e293b;border-radius:10px;overflow:hidden}
    th,td{text-align:left;padding:12px 18px;border-bottom:1px solid #334155;font-size:14px} th{background:#334155;color:#cbd5e1}
    a{color:#38bdf8;text-decoration:none} a:hover{text-decoration:underline}
    .badge{padding:2px 12px;border-radius:20px;font-size:12px;font-weight:600}
    .PASS{background:#166534;color:#bbf7d0} .FAIL{background:#991b1b;color:#fecaca}
    .RUNNING{background:#854d0e;color:#fde68a} .NONE{background:#475569;color:#cbd5e1}
    tr.running td{background:#27314a}
  `]
})
export class TestListComponent implements OnInit, OnDestroy {
  status?: RunStatus;
  private sub?: Subscription;

  constructor(public api: TestApiService, private router: Router) {}

  ngOnInit() { this.sub = this.api.statusPoll(2000).subscribe(s => this.status = s); }
  ngOnDestroy() { this.sub?.unsubscribe(); }

  runAll() { this.api.run().subscribe(() => this.router.navigate(['/live'])); }
  runOne(m: string) { this.api.run(m).subscribe(() => this.router.navigate(['/live'])); }
}
