## Area focus — Navigation

(Inherits the BASE rules above; this only adds Navigation-specific knowledge.)

Navigation tests prove the app moves between top-level modules from Home (the MDI shell `frmMain`
+ `frmMenu`). Module nav goes through `Main/frmMenu.cs` btnXClick -> `Lib.ShowForm(typeof(X.frmMenu))`.

Navigation-specific things a reviewer must weigh:
- **Objective proof of screen change**, not a sleep. The proven signal is the button-count delta
  (Home -> Items: 63 -> 74; Item List: 74 -> 82). A nav test that asserts only "no exception" or a
  fixed wait is weak — it can pass without actually navigating.
- **Custom-control gotcha**: Exatouch's `csButton` ignores UIA `Invoke()`; nav MUST use a real
  mouse `.Click()` (window visible / SetForeground first). Flag any nav step relying on Invoke().
- **Round-trip**: "and back" means it must return to Home and prove it (count returns to baseline).
- Coverage here is breadth: which modules from runtime-map have NO navigation test at all.
