# BtAudioGuard

Tray app that watches Bluetooth headphones on Windows 11. When the headphones connect
(for example after being lifted from the desk), it tests whether audio actually plays.
If the stream is dead, it runs a ladder of fixes and logs which one worked.

## How it works

1. A WinRT `DeviceWatcher` sees the headphones connect (LE and Classic entries).
2. After a short settle delay, the app opens a silent WASAPI stream to the headphones'
   output and checks that the buffer drains. A stalled Bluetooth endpoint never drains it.
   This is the same condition that freezes video players.
3. If the check fails, the enabled fix steps run in order, with a re-check after each:
   - **Wake LE link:** for when only the Classic link came back (the endpoint is missing).
     Forces the LE connection and holds it until Windows rebuilds the audio endpoint. Skipped
     when the endpoint is already active.
   - **Reconnect headphones (KS):** sends disconnect + reconnect to the Bluetooth audio
     driver through `IKsControl`, like Disconnect/Connect in Windows settings. Only on an
     active endpoint (on an unplugged one it would page headphones that are switching off).
   - **Restart LE Audio driver:** disables and re-enables the headphones' LE Audio driver node
     ("Audio Kernel-Mode Proxy Unit Device", `SWD\APXENUM...`). Aimed at a stalled stream on
     an active endpoint; also tried when Wake LE link did not help. Leaves the Bluetooth link
     and adapter alone.
   - **Restart adapter:** disables and re-enables the Bluetooth adapter. Off by default:
     the headphones do not reconnect on their own afterwards.
   - **Restart audio services:** restarts `Audiosrv` and `AudioEndpointBuilder`. Off by default.
   Before each step the ladder checks the headphones are still connected and stops if not,
   so it does not wake headphones that are switching off. Exception: a manual "Fix now"
   started while nothing is connected runs anyway (Wake LE link then connects them, like
   Connect in Windows settings). A failed WASAPI call that hangs skips the ladder: only
   "Restart audio services" or an app restart can clear it.
4. After an automatic fix run fails, further automatic runs wait for a cooldown (90 s by
   default), so a fix that does not work is not repeated in a loop. Successful and manual
   runs do not start the cooldown.
5. Watchdog: while the LE link is up and another app is playing to the headphones, the
   silent stream test re-runs every 60 s (setting, 0 = off), so a stall during use is caught
   too. Nothing is probed while nothing plays, also not while Stalled. While Stalled, the
   probe tries the fix again once the cooldown has passed. It logs only failures and
   recoveries, plus which apps hold streams on the headphones and mic whenever that changes.

6. Headphones advertising while nothing is connected. The headset's extended advertisement
   carries the LE Audio announcement type (service data of 0x1853 and 0x184E, byte after the
   UUID). Seen on this headset:
   - **General (00)** while it lies on the table, before it switches off. Only "available".
     Answering it keeps the headset awake.
   - **Targeted (01)** right after it is switched on: "paired PC, connect now". Windows
     ignores it, so no audio.
   While no link is up the app scans, groups the advertisements into episodes and logs each
   episode's cause, announcement changes and each new payload. Setting "Headphones on, nothing
   connected": **Off**, **Log only** or **Wake LE link** (default). Wake runs Wake LE link on a
   Targeted announcement, never on General. Fallback without one: the LE link dropped while
   audio was audibly playing (after 5 s of advertising, since a headset switched off mid-song
   stops within a second). At most 2 tries per episode, 1 per minute. The scan stops as soon
   as any link is up, so it never competes with the LE Audio stream. The "Advertisements..."
   window shows each distinct payload live.

On a failed check the log adds `Context:` lines (link states, LE Audio driver node state,
apps holding streams) and the stream test reports what the audio engine did (buffer level,
audio clock movement, format).

Before each stream test, the app picks the active "Headphones (...)" endpoint and, if
"Make headphones default output" is on, sets it as the Windows default output for all roles
(console, multimedia, communications). This happens only on connect, startup or a manual
check (and fixes those trigger), never from the watchdog, so a manual switch to other
speakers stays in place until the next connect.

Disabling a device node (LE Audio driver, adapter) writes its ID to
`%AppData%\BtAudioGuard\pending-enable.txt` until it is enabled again. If the app dies in
between, the next start re-enables it.

## Build

Requires the .NET 8 SDK and Windows 11.

From the project folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```

Copy `out\BtAudioGuard.exe` to `C:\Tools\BtAudioGuard\` on the target machine
(Properties > Unblock if it came through a download or share).

## Start at logon without a UAC prompt

The app needs admin rights for the driver, adapter and service restarts. Register it once as a
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

## First days: find the fix that works

Leave auto-fix on with all default steps. After a few failures, search the log for `FIXED by:`.

- If the KS step never succeeds on LE Audio (log shows `KsProperty 0x...`), turn it off to save 5 s.
- If the stream test flags failures while audio is fine, raise "Stream test (s)".
  LE Audio can take a moment to set up its stream.
- If the check runs before the headphones are ready, raise "Wait after connect (s)".
