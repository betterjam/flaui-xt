# Running the Exatouch UI tests on AWS (EC2 Windows + virtual screen)

Yes — these FlaUI tests run on AWS. The "virtual screen + screenshots" problem that blocks
headless WinForms locally is **solved on EC2** because the remote desktop session provides a
real rendered desktop. This is the standard way teams run Windows UI automation in the cloud.

## Why it works (the key fact)
WinForms + UI Automation needs a real, rendered desktop (we proved it can't run truly
headless). On EC2 Windows, an active **RDP or NICE DCV session IS a real rendered desktop** —
so FlaUI sees controls, `Capture` screenshots work, and mouse clicks land. No special trick
needed *while a session is connected*. For unattended runs, keep a session alive (below).

## Architecture
```
EC2 Windows Server 2022 instance (t3.large ~$0.08/hr, or t3.xlarge for headroom)
  ├─ .NET Framework 4.7.2  (Exatouch's runtime)
  ├─ SQL Server Express + XTTDB   (the DB; same as local - Integrated Security + lpc:)
  ├─ Exatouch (built)             (the app under test)
  ├─ qa-ai\flaui-poc              (the tests + dashboard server)
  ├─ .NET SDK + dotnet test       (test runner)
  └─ NICE DCV (free on EC2)       (the rendered desktop / "virtual screen")
```

## One-time setup

### 1. Launch the instance
- AMI: **Microsoft Windows Server 2022 Base**
- Type: **t3.large** (2 vCPU / 8 GB) minimum; t3.xlarge if Exatouch + SQL + builds are heavy
- Storage: 50+ GB gp3
- Security group: allow **RDP 3389** (and **8443** if you use NICE DCV) from your IP only

### 2. Connect + install the stack (via RDP)
```powershell
# .NET Framework 4.7.2 is usually present on Server 2022; if not, install it.
# Install .NET SDK (for dotnet test):
winget install Microsoft.DotNet.SDK.8
# Install SQL Server Express:
winget install Microsoft.SQLServer.2022.Express
# (then create/restore XTTDB exactly like the local machine)
# Copy over: the built Exatouch (2.0.x\bin) and the qa-ai folder.
```

### 3. The virtual screen for UNATTENDED runs
Two proven options:

**A. NICE DCV (recommended on EC2)** — AWS's own remote display, free on EC2. It maintains a
virtual GPU-backed desktop that renders even when no one is "watching". Install the DCV server,
start a virtual session:
```powershell
# after installing the DCV server MSI:
dcv create-session --type virtual qa
```
Tests run against that session's desktop. Screenshots and clicks work.

**B. Keep an RDP console session alive** — connect via RDP, then disconnect *without logging
off* while keeping the session active:
```powershell
# run on the instance to redirect the console session so it stays rendered:
tscon 1 /dest:console
```
Cheaper (no DCV), but more fragile than DCV for long unattended runs.

**C. (Fallback) IddSampleDriver virtual monitor** — the same driver from
`setup-virtual-display.md`. Works on EC2 too, but DCV is cleaner there.

## Running the tests
Once a desktop session exists, it's identical to local:
```powershell
cd C:\Dev\exatouch-qa\flaui-poc
dotnet test -c Debug                         # all tests
# or the dashboard server (reach it via the instance's IP over the DCV/RDP browser):
powershell -File dashboard-server.ps1 -Port 5099
```
The reports + screenshots land in `TestResults\` exactly as locally.

## CI / scheduled runs
- **Scheduled Task** on the instance: run `dotnet test` nightly, push reports to S3.
- **Or** GitHub Actions / Azure DevOps with a **self-hosted Windows runner** on the EC2 box.
- Publish `TestResults\*.html` (self-contained, screenshots embedded) to an **S3 static site**
  so the team sees reports without touching the instance.

## Cost control
- t3.large ≈ **$0.083/hr** on-demand (~$2/day if left on; pennies if you stop it between runs).
- **Stop** (not terminate) the instance when idle — you keep the disk, pay only for storage.
- A Lambda + EventBridge can start the instance before a nightly run and stop it after.

## Gotchas
- Exatouch needs its **SQL Server** reachable - install SQLEXPRESS on the same instance and use
  the `lpc:` local connection we already configured (works on one box).
- Exatouch's **single-instance check** + the full-screen build behavior apply here too - use the
  windowed build for tests (the resolution on the DCV session is configurable).
- License/activation: Exatouch may behave differently in a fresh environment - use the same
  `Exatouch.cfg` (FirstTimeLogin=false etc.) you set up locally.

## Bottom line
EC2 Windows + NICE DCV gives you a real virtual screen, so the entire suite (tests, screenshots,
dashboard, live reports) runs in the cloud unchanged. Spin up for runs, stop when done.
