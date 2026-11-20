# BtAudioGuard

Tray app that keeps Bose QC Ultra 2 headphones working over Bluetooth LE Audio on Windows 11.
Its 100% vibe coded using Claude.

## The problem

When using the Bose On-head detection with Bluetooth LE putting down the headset will make it snooze.
When the headset is used again Windows will only manage to reconnect to it some of the time, other times it just breaks and its impossible to reconnect even manually..

All audio goes over the LE Audio link; the Classic link only carries the Bose app's control
channel. Two failures:

1. **LE link not reconnected.** After the LE link drops, Windows does not reconnect it, so
   there is no audio. Picked up after sleeping on the table, the headset advertises a
   *Targeted announcement* ("paired PC, connect now") that Windows ignores. Sometimes the
   headset falls back to Classic after about 20 s, sometimes it does nothing. What it
   advertises after a drop while worn has not been captured yet.
2. **Stalled stream.** LE is up and the output is active, but audio stops (video freezes,
   Spotify silent). Seen rarely; the fix is not confirmed yet.

## How it works

**Failure 1: answer the headset.** While no link is up, the app scans for the headphones'
Bluetooth LE advertisements. Their extended advertisement carries the LE Audio announcement
type (service data of 0x1853 / 0x184E, byte after the UUID):

- **General (00)**: "available". Sent for 10–20 min while lying on the table. Never
  answered: that would keep the headset awake.
- **Targeted (01)**: "connect now". Sent for a few seconds when picked up after sleeping
  (and after being switched on). Answered with **Wake LE link**.

Nothing else wakes the headset from an advertisement: any other rule risks waking one that
is going to sleep. At most 3 wakes per 10 minutes, 60 s apart. The scan needs extended
advertising support (the announcement is only in extended advertisements) and stops as soon
as any link is up, so it never competes with the LE Audio stream for radio time.

Also caught by link events: a Classic connect with LE down, and an LE drop while Classic
stays up (both: check, then Wake LE link). The log records whether audio was playing when LE
dropped, and each advertising run's announcement and distinct payloads (capped), as
evidence for cases not yet understood.

**Wake LE link** forces the LE connection (uncached GATT query) and holds a `GattSession`
with `MaintainConnection` until Windows has built the audio endpoint (usually 2–5 s), then
releases it, so the headset can still switch itself off later.

**Check.** A silent WASAPI stream to the headphones' output; a working endpoint drains the
buffer within milliseconds, a stalled one never does (the condition that freezes video
players). Runs after every connect and fix.

**Failure 2: watchdog.** While audio plays to the headphones, the check repeats every 60 s
(setting, 0 = off). On failure the fix steps run in order, each followed by a check:

1. **Wake LE link** (skipped when the endpoint is already active)
2. **Reconnect headphones (KS):** disconnect + reconnect through the Bluetooth audio driver
3. **Restart LE Audio driver:** disable/enable the headphones' LE Audio driver node
   (`SWD\APXENUM...`), leaving the Bluetooth link alone

A failed automatic fix starts a cooldown (90 s) so it does not loop. A WASAPI call that
hangs skips the steps: only **Restart audio services** (button) or an app restart clears it.

**Safety.**
- Fix steps stop when the headphones disconnect (taken off, switched off), so they do not
  keep waking a headset that is going away. A manual **Fix now** with nothing connected still
  runs (Wake LE link then connects, like Connect in Windows settings).
- A disabled driver node is written to `%AppData%\BtAudioGuard\pending-enable.txt` until it
  is enabled again; the app re-enables it on the next connect or start.
- "Make headphones default output" applies on connect and manual checks only, never from the
  watchdog, so a manual switch to other speakers sticks.

The tag `v0.1-full-diagnostics` has the earlier version with the advertisement payload
window, payload logging, episode-cause guessing and the adapter restart step.

## Download

Get `BtAudioGuard.exe` from the repository's **Releases** page (self-contained, no .NET install
needed). Copy it to `C:\Tools\BtAudioGuard\` and, if Windows marks it as downloaded,
Properties > Unblock.

## Release a new version

Push a version tag; GitHub Actions builds the exe and publishes the release
(`.github/workflows/release.yml`). The tag sets the version shown in the app.

```powershell
git tag v1.0.1
git push origin v1.0.1
```

## Build locally

Requires the .NET 8 SDK and Windows 11. From the project folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```

Copy `out\BtAudioGuard.exe` to `C:\Tools\BtAudioGuard\` on the target machine
(Properties > Unblock if it came through a download or share).

## Start at logon without a UAC prompt

The app needs admin rights for the driver and service restarts. Register it once as a
scheduled task from an elevated PowerShell:

```powershell
$action    = New-ScheduledTaskAction -Execute "C:\Tools\BtAudioGuard\BtAudioGuard.exe"
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive
$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
             -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName "BtAudioGuard" -Action $action -Trigger $trigger `
             -Principal $principal -Settings $settings -Force
```

Start it now without logging off: `Start-ScheduledTask -TaskName "BtAudioGuard"`.

## Use

- Tray dot: grey = off / starting, yellow = checking, green = OK, orange = fixing, red = stalled.
- Double-click the tray icon for the window: device list, manual actions, settings and live log.
- Log file: `%AppData%\BtAudioGuard\log.txt`. Settings: `%AppData%\BtAudioGuard\settings.json`.
- `diag.ps1`: snapshot of the headphones' device nodes and audio endpoints (run once while
  audio works and once while it is broken, to compare).
