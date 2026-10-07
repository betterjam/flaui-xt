# Test run-state contract (the guideline every test follows)

So the frontend can reliably detect "running vs idle vs finished" and show the right
animation, **every test must signal its lifecycle the same way**. This is the shared
mechanism — follow it and the live view "just works".

## The lifecycle (identical for every test)

```
1. START   -> new LiveLog(OutDir, "<human title of what this test does>")
2. STEP*   -> _live.AddStep("<human description of this step>", "<screenshot file>")   (repeat)
3. FINISH  -> _live.Finish("PASS")   or   _live.Finish("FAIL")
```

That's it. `LiveLog` writes `live-run.json` after every call, and the run-state authority
(`run-state.json`, set by run-tests.ps1) marks running/idle. The server merges them into one
shape the frontend consumes.

## The single shape the frontend gets (/api/live)

```json
{
  "runState": "idle | running | passed | failed",
  "title": "human title of the test",
  "steps": [ { "n": 1, "text": "human step description", "image": "shot_xxx.png" } ]
}
```

- **runState is the ONLY thing the frontend uses to choose the animation.** It is derived
  server-side: `running` if a run is active; `passed`/`failed` only when there are real
  streamed steps AND the run finished; otherwise **`idle`** (this kills the "stale FAIL with
  0 steps" ghost that showed a false finish).

## Rules every test must obey

1. **Always construct a LiveLog in `[OneTimeSetUp]`** with a clear human title.
2. **Call `AddStep` for each meaningful action**, with a human sentence (not jargon) and a
   screenshot file. The frontend streams these in order.
3. **Always call `Finish("PASS")` or `Finish("FAIL")`** in the test body / catch. Never leave
   a run un-finished.
4. **Describe steps like a person narrating** ("Clicked 'Items'. The module opened — the
   screen now shows 74 buttons.") — this is what makes the live report readable.
5. The runner (`run-tests.ps1`) resets `live-run.json` to a clean `running/0-steps` state at
   the start of every run, so the previous run's steps never leak into the next.

## Why this fixes the bug you saw

Before: the live view trusted `live-run.json` directly. A force-finished leftover
(`status: FAIL, steps: []`) made it show "Test failed" forever even when idle.

After: `runState` is computed from the run-state authority + whether real steps exist. No
active run + no real steps = **idle**, so the frontend shows a neutral "no test running"
message and no false finish animation.

## Template (copy this into a new test)

```csharp
private LiveLog _live;

[OneTimeSetUp] public void Setup() {
    _live = new LiveLog(OutDir, "What this test verifies, in plain words");
    /* launch app ... */
}

[Test] public void My_test() {
    try {
        Step("First, we do X. Here's what happened...");   // Step() calls _live.AddStep
        // ... actions ...
        _live.Finish("PASS");
    } catch {
        Step("It failed here - this is the screen at the error.");
        _live.Finish("FAIL");
        throw;
    }
}
```
