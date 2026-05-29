# R1 verification — is the viewer window capturable?

> **R1 is the make-or-break risk (PLAN.md §10, §8 Phase 1 DoD).** Before any of
> the capture→encode→decode pipeline is built, we must prove that the viewer
> window is **selectable** in Discord/OBS **and shows live, moving content
> (not black)** — on **both** macOS and Windows. This is a **manual, human-in-
> the-loop** check: it needs eyes on real GUI apps. This document is the script.
> Record the results in the table at the bottom (PLAN.md §11 keeps this in the
> release checklist).

## What you are testing

Each OS app currently runs the **R1 probe**: the *real* viewer render surface —
`AVSampleBufferDisplayLayer` on macOS, a **flip-model D3D11 swapchain** in a
dedicated Win32 window on Windows — driven by **synthetic animated content**:

- **macOS:** a yellow box sweeping left↔right and an incrementing `frame N`
  counter on a colour-cycling background.
- **Windows:** the whole window smoothly cycling through the colour wheel.

This is deliberately the same GPU-composited surface the live H.264 pipeline
will use, so proving capturability now de-risks everything built later. No
screen *capture*, encode, or decode runs yet — that arrives only after R1 passes.

**"Live" looks like:** in the captured preview, the macOS box sweeps and its
counter climbs; the Windows window keeps changing colour — smoothly, in step
with the source window.
**"Black/frozen" = R1 fail** for that tool/OS — stop and report (see *If it's
black* below before concluding failure).

## Prerequisites

- **Discord** and **OBS Studio** (28+ recommended) installed on the machine
  under test.
- **macOS:** the *capturing* app (OBS, Discord) needs **Screen Recording**
  permission — System Settings → Privacy & Security → Screen & System Audio
  Recording → enable OBS / Discord, then relaunch them. (The ScreenBridge probe
  itself needs **no** permission here: it generates its own frames; it does not
  capture the screen yet.)
- **Windows:** no special permission. Windows 10 **1903+** or Windows 11 (so
  Windows.Graphics.Capture is available — it is Discord's and OBS's modern
  default).

---

## Run the probe

### macOS
```sh
# from the repo root
MACOSX_DEPLOYMENT_TARGET=13.0 cargo build --manifest-path core/Cargo.toml -p screenbridge-ffi
swift run --package-path apps/macos ScreenBridgeApp
```
A window titled **ScreenBridge** opens showing the animated probe. Leave it
visible (don't minimise it).

### Windows
```pwsh
# from the repo root, on the Windows machine
cargo build --release --manifest-path core/Cargo.toml
dotnet build apps/windows/ScreenBridge/ScreenBridge.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64
# then launch the built app:
.\apps\windows\ScreenBridge\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\ScreenBridge.exe
```
The WinUI shell opens **and** spawns a separate top-level **ScreenBridge —
Viewer (R1)** window showing the animated probe. The *viewer* window is the one
to capture.

---

## Verify in Discord

1. Join any voice channel (or a DM call). Click **Share Your Screen** / the
   screen-share icon.
2. Choose the **Applications / Application Window** tab (not "Screen").
3. Select the **ScreenBridge** window from the list.
   - ✅ **Selectable?** the window appears in the list.
   - ✅ **Live?** the shared preview shows the box sweeping and the counter
     incrementing.
4. Screenshot the Discord preview.

## Verify in OBS

### macOS
1. Sources → **+** → **macOS Screen Capture** (the ScreenCaptureKit source).
2. In its properties set the capture **Method/Type** to **Window** and pick the
   **ScreenBridge** window. (Older OBS: use the **Window Capture** source.)
3. ✅ Selectable in the window list; ✅ live animation in the OBS canvas.
   Screenshot it.

### Windows
1. Sources → **+** → **Window Capture**.
2. Set **Capture Method** to **Windows 10 (1903 and up)** — this is **WGC**, the
   path our flip-model swapchain is built for.
3. Pick the **ScreenBridge — Viewer (R1)** window.
4. ✅ Selectable; ✅ live animation. Screenshot it.

---

## If it's black (triage before declaring R1 failed)

| Symptom | Likely cause | Try |
|---|---|---|
| **OBS Windows** black, but only on **BitBlt** method | Expected — the legacy BitBlt path cannot read a flip-model swapchain | Switch OBS Capture Method to **Windows 10 (1903+) / WGC**. If WGC also shows black → real R1 failure, report. |
| **Discord macOS** black | Discord's *own* hardware-acceleration rendering bug (not ours) | Discord → Settings → Advanced → toggle **Hardware Acceleration** off, restart Discord. |
| **All capture black on macOS** | Capturing app lacks Screen Recording permission | Grant it (see Prerequisites), relaunch OBS/Discord. |
| **Window not in the list at all** | Window minimised, or app not a regular foreground app | Ensure the window is open and focused; on macOS the app sets a regular activation policy automatically. |
| **OBS Windows WGC black even after switching** | Genuine flip-model capture failure on this setup | **Report it** — this is the documented R1 fallback trigger: we switch the swapchain to the BitBlt model (PLAN.md §10). |

A result is only an **R1 failure** if the window shows black under the
**modern** capture path (Discord's default; OBS **WGC** on Windows / **macOS
Screen Capture** on macOS) *after* the triage above.

---

## Results (fill in)

| OS | Tool | Capture mode | Selectable? | Live content? | Screenshot | Notes |
|---|---|---|---|---|---|---|
| macOS | Discord | App window | ☐ | ☐ | | |
| macOS | OBS | macOS Screen Capture (Window) | ☐ | ☐ | | |
| Windows | Discord | App window (WGC) | ☐ | ☐ | | |
| Windows | OBS | Window Capture (WGC) | ☐ | ☐ | | |

**R1 verdict:** ☐ PASS (all four live) ☐ FAIL (which row, and the black-triage
result) — paste here.

> Only once R1 is **PASS on both OSes** do we proceed to build capture → H.264
> encode → decode → ASBDL/D3D11 render and the Opus audio path on top of this
> surface.
