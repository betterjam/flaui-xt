import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, timer, switchMap, shareReplay } from 'rxjs';

/** A single test as the dashboard knows it. */
export interface TestInfo {
  method: string;
  pretty: string;
  report: string | null;
  status: 'PASS' | 'FAIL' | null;
  when: string | null;
  base?: string;       // base method (version suffix stripped) - for grouping
  version?: number;    // version number (1 = original)
  area?: string;       // area key (items/navigation/other) - top-level grouping
  extends?: string;    // parent test (base) this one branches off - the trunk it runs after
}

/** Overall run status. */
export interface RunStatus {
  running: boolean;
  finishedAt: string;
  lastReport: string;
  tests: TestInfo[];
}

/** A streamed step in a live test run. `images` holds all frames when the step is a burst
 *  album (many granular frames grouped into one step); `image` is the first/representative frame. */
export interface LiveStep { n: number; text: string; image: string; images?: string[]; group?: boolean; }

/**
 * The standard run-state CONTRACT every test follows, surfaced by /api/live.
 *   idle    - nothing running, no fresh result (frontend shows neutral, no false finish)
 *   running - a test is actively executing (show spinner + stream steps)
 *   passed  - the run just finished successfully (show pass banner)
 *   failed  - the run just finished with a failure (show fail banner)
 */
export type RunState = 'idle' | 'running' | 'passed' | 'failed';
export interface LiveRun { runState: RunState; title: string; steps: LiveStep[]; report: string; test: string; debug?: boolean; }

/** One recorded action in a test's captured-flow map (Steps & Actions tab). */
export interface RecordingStep {
  n: number; action: string; by: string; key: string; value: string; label: string; file: string; keyboard: boolean;
  inherited?: boolean;   // true if this step comes from a parent (trunk) test, not this branch
  from?: string;         // the test this step originated in (self or an ancestor)
}

/** A saved run's data (for rendering the Report tab natively in Angular). */
export interface RunData { test: string; status: 'PASS' | 'FAIL' | ''; title: string; finishedAt?: string; steps: LiveStep[]; }

/** AI test-review result. */
export interface ReviewGap {
  area: string; issue: string; severity: 'high' | 'medium' | 'low'; fix: string;
  step?: number;        // which run step this relates to (1-based), if any
  screenshot?: string;  // the screenshot file from that step (shows WHERE the fix applies)
}
export interface ReviewQuality {
  test: string; issue: string; severity: 'high' | 'medium' | 'low'; fix: string;
  step?: number; screenshot?: string;
}
export interface Review {
  test?: string; summary: string; coverageScore: number;
  gaps: ReviewGap[]; qualityIssues: ReviewQuality[]; topRecommendations: string[];
}
export interface ReviewEnvelope { running: boolean; phase: string; review: Review; }

/** One version of a test (method name + version number). */
export interface TestVersion { version: number; method: string; }
export interface VersionList { base: string; latest: number; versions: TestVersion[]; }

/** A staged new-version draft the agent produced from accepted improvements. */
export interface VersionDraftManifest {
  test: string; fromVersion: number; toVersion: number;
  fromFile: string; file: string; newMethod: string; accepted: string[]; status: string;
}
/** The draft envelope: progress + the manifest + old/new code for the diff. */
export interface VersionDraftEnvelope {
  running: boolean; phase: string;
  draft: VersionDraftManifest | null;
  oldCode: string; newCode: string;
}

/** The self-correcting verdict: did the new version ACHIEVE the accepted improvements?
 *  achieved is DETERMINISTIC (decided from the run's real status + step completeness, not AI). */
export interface VersionVerdict {
  method: string; base: string;
  achieved: boolean; confidence: number;
  deterministic?: boolean;          // true = decided by machine facts, not AI opinion
  runStatus?: string;               // PASS | FAIL | '' (the authoritative run outcome)
  steps?: number;                   // how many steps the run recorded
  failReason?: string;              // the deterministic reason it failed (empty if achieved)
  fromVersion?: number;             // the version this one was built on top of
  added?: string;                   // one-line: what THIS version added vs the previous
  acceptedItems?: string[];         // the literal accepted improvement(s) that were added
  reason: string; missing: string[];
  attempt: number; maxAttempts: number; retrying: boolean;
}

/**
 * Shared, reusable API service. All components talk to the backend through this one place
 * (the request Diego made: common services, shared features). Wraps the PowerShell endpoints.
 */
@Injectable({ providedIn: 'root' })
export class TestApiService {
  constructor(private http: HttpClient) {}

  /** Current status of all tests (one-shot). */
  status(): Observable<RunStatus> {
    return this.http.get<RunStatus>('/api/status');
  }

  /** Poll status every `ms` milliseconds (for live dashboards). */
  statusPoll(ms = 2000): Observable<RunStatus> {
    return timer(0, ms).pipe(switchMap(() => this.status()), shareReplay(1));
  }

  /** Start a run: all tests, or one by method name. */
  run(test?: string): Observable<unknown> {
    const url = test ? `/api/run?test=${encodeURIComponent(test)}` : '/api/run';
    return this.http.post(url, {});
  }

  /** Stop the currently-running test (graceful debug-stop if possible, else kill the host). */
  stopRun(): Observable<unknown> { return this.http.post('/api/run/stop', {}); }

  /** The live stream for the currently-running test (one-shot). */
  live(): Observable<LiveRun> {
    return this.http.get<LiveRun>('/api/live');
  }

  /** Poll the live stream (for the streaming report view). */
  livePoll(ms = 1200): Observable<LiveRun> {
    return timer(0, ms).pipe(switchMap(() => this.live()));
  }

  /** The Flow Capture studio's OWN stream (separate channel from test runs). */
  flow(): Observable<LiveRun> { return this.http.get<LiveRun>('/api/flow'); }
  flowPoll(ms = 1000): Observable<LiveRun> { return timer(0, ms).pipe(switchMap(() => this.flow())); }

  /** A saved run's data for a test (steps+screenshots) - for the native Report tab. */
  runData(test: string): Observable<RunData> {
    return this.http.get<RunData>(`/api/run-data?test=${encodeURIComponent(test)}`);
  }

  /** The per-test AI review result for one test (coverage gaps + quality issues). */
  review(test: string): Observable<ReviewEnvelope> {
    return this.http.get<ReviewEnvelope>(`/api/review?test=${encodeURIComponent(test)}`);
  }
  /** Trigger the per-test review agent. force=true re-runs that test fresh first. */
  runReview(test: string, force = false): Observable<unknown> {
    const f = force ? '&force=1' : '';
    return this.http.post(`/api/review/run?test=${encodeURIComponent(test)}${f}`, {});
  }

  // --- test versioning --------------------------------------------------------
  /** All versions of a test (for the version picker). */
  versionList(test: string): Observable<VersionList> {
    return this.http.get<VersionList>(`/api/version/list?test=${encodeURIComponent(test)}`);
  }
  /** Ask the agent to draft the NEXT version implementing the accepted improvements. */
  draftVersion(test: string, accepted: string[]): Observable<unknown> {
    return this.http.post(`/api/version/draft?test=${encodeURIComponent(test)}`, { accepted });
  }
  /** The current draft (progress + manifest + old/new code for the diff). */
  versionDraft(test: string): Observable<VersionDraftEnvelope> {
    return this.http.get<VersionDraftEnvelope>(`/api/version/draft?test=${encodeURIComponent(test)}`);
  }
  /** Apply the staged draft -> writes it live, then auto-runs + auto-reviews the new version. */
  applyVersion(test: string): Observable<{ ok: boolean; newMethod: string }> {
    return this.http.post<{ ok: boolean; newMethod: string }>(`/api/version/apply?test=${encodeURIComponent(test)}`, {});
  }
  /** Throw away the staged draft. */
  discardVersion(test: string): Observable<unknown> {
    return this.http.post(`/api/version/discard?test=${encodeURIComponent(test)}`, {});
  }
  /** The self-correcting verdict for a test family (null until the first verify completes). */
  verdict(test: string): Observable<VersionVerdict | null> {
    return this.http.get<VersionVerdict | null>(`/api/version/verdict?test=${encodeURIComponent(test)}`);
  }
  /** Delete a specific version (v1 can't be deleted; a version with a child can't either). */
  deleteVersion(test: string, version: number): Observable<{ ok: boolean; blocked?: string }> {
    return this.http.post<{ ok: boolean; blocked?: string }>(`/api/version/delete?test=${encodeURIComponent(test)}&version=${version}`, {});
  }
  /** Delete a whole test (family) — allowed only when no other test extends it (no children). */
  deleteTest(test: string): Observable<{ ok: boolean; blocked?: string }> {
    return this.http.post<{ ok: boolean; blocked?: string }>(`/api/test/delete?test=${encodeURIComponent(test)}`, {});
  }

  /** The recorded action map for a test (Steps & Actions tab). Empty array if not a recorded test. */
  recording(test: string): Observable<RecordingStep[]> {
    return this.http.get<RecordingStep[]>(`/api/recording?test=${encodeURIComponent(test)}`);
  }

  // --- Ranorex import / export ---
  /** Browse the available Ranorex .rxrec recordings (for the Import menu). */
  ranorexList(): Observable<{ ok: boolean; root: string; recordings: { name: string; full: string; folder: string }[] }> {
    return this.http.get<any>('/api/ranorex/list');
  }
  /** Import selected Ranorex recordings -> our maps -> runnable FlaUI tests. */
  ranorexImport(files: string[]): Observable<{ ok: boolean; imported: string[]; count: number }> {
    return this.http.post<any>('/api/ranorex/import', { files });
  }
  /** Export a test (our recording map) -> Ranorex .rxrec + .rxrep. */
  exportRanorex(test: string): Observable<{ ok: boolean; test?: string; rxrec?: string; rxrep?: string; error?: string }> {
    return this.http.post<any>(`/api/export/ranorex?test=${encodeURIComponent(test)}`, {});
  }
  /** Download URL for an exported Ranorex file. */
  ranorexFileUrl(name: string): string { return `/api/ranorex/file?name=${encodeURIComponent(name)}`; }

  /** URL to resolve a screenshot file. */
  reportUrl(file: string): string { return `/report/${file}`; }
}
