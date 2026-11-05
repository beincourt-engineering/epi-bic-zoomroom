# Zoom Room — Silent Disconnect Auto-Repair (Connection Watchdog)

**Status:** Fixed & lab-validated (2026-08-11)
**Branch:** `feat/repair-zoomroom-connection-watchdog` → merged into `feature/v3-migration`
**Commit:** `5e74071` — _feat: auto-repair Zoom Room on silent connection loss_

---

## 1. The issue

After a network outage, the Zoom Room device would **fail to reconnect on its own** even once the
network came back. The only recovery was a **program restart**.

Observed symptoms:

- The device kept showing **`IsOk` in devcomm** while it was actually **not connected or functional**.
- Outbound commands failed. In the field this first surfaced as a failed "start meeting" action:

  ```
  [zoomRoom] Starting new meeting with 1 contact(s)
  [zoomRoom-zrc] SDK call MeetWithIMUsers returned failure
  ```

- After a ~10-minute disconnect, restoring the network did **not** bring Zoom back. A restart did.

## 2. Root cause

Reconnect logic was **purely event-driven** and had a hard give-up cap:

- `ZrcSdkController.ScheduleReconnect()` only ran in response to the SDK's
  `ConnectionStateChanged(Disconnected)` event, and gave up permanently after **10 attempts**.
- The comms monitor (`SdkConnectionMonitor`) **did not poll** — its `IsOnline` was set only from
  those SDK events.

On a **silent / half-open drop** (the cloud session dies without a clean TCP close — e.g. WAN outage
on a cloud-paired room), the SDK **never fires the `Disconnected` event**. Result:

- `_isConnected` stayed **stale-true**, so devcomm stayed `IsOk`.
- Because the disconnect event never fired, `ScheduleReconnect()` was **never even called** — the old
  code made **zero** reconnect attempts.
- The device sat dead until a human restarted the program.

Key insight that enabled detection: while the SDK's connection *flag/events* went stale, the SDK's
**command return codes stayed truthful** — `MeetWithIMUsers` returned failure even though
`_isConnected` was still true.

## 3. How we replicated it (old code)

This Zoom Room is **cloud-paired** (activation code → ZRC SDK → Zoom cloud), so there is no local
appliance to unplug. We reproduced the outage by taking the **network down for ~10 minutes**.

Test command used to exercise the failing command path from the console (idle room → `MeetWithIMUsers`):

```
devjson {"deviceKey":"zoomRoom","methodName":"InviteContactById","params":["testContact123"]}
```

Reply interpretation:

- `MeetWithIMUsers ignored: SDK not connected` → known-disconnected (Guard blocked it).
- `SDK call MeetWithIMUsers returned failure` → the stale case (Guard passed on stale state, SDK call
  then failed) — this matches the original incident signature.

**Result (old code): PROVEN.** After a 10-minute disconnect, the network came back and Zoom **did not
reconnect**. **Restarting the program brought it back**, confirming the reconnect logic — not the
hardware or credentials — was the failure.

## 4. The fix

A health **watchdog** that detects silent drops functionally and self-heals, added across
`SdkConnectionMonitor`, `ZrcSdkController`, `IZoomRoomController`, and `ZoomRoom`.

Two detection signals feed one repair path:

1. **Command-failure strikes (fast):** every SDK command result flows through the controller's `Rc`
   helpers into `NoteCommandResult`. **2** consecutive failures while `_isConnected` is true trigger a
   liveness probe.
2. **Liveness poll (safety net):** `SdkConnectionMonitor` polls every **30 s** and runs the same
   health check for the idle case where no command is issued.

**Probe:** `GetMeetingStatus()` — a real SDK round-trip that returns `null` when the link is dead even
if the connection flag is stale.

- Probe **succeeds** → false alarm (e.g. a bad command); strikes reset, stays online.
- Probe **fails** → `DeclareOffline`: flip devcomm to `InError` (via `HealthStateChanged`) and start
  auto-repair.

**Repair policy — never give up:** self-perpetuating reconnect with escalating backoff
`5 → 10 → 20 → 30 → 60 s`, then holds at **60 s forever** until a real `Connected` event returns. The
old 10-attempt cap was removed so an unattended room self-heals whenever the room is reachable
again.

**devcomm truth:** online/offline is now driven by *actual* probe/command results, not just SDK
events — so it can no longer sit stuck at `IsOk` while dead.

### Files changed

| File | Change |
| --- | --- |
| `src/Controller/SdkConnectionMonitor.cs` | Real 30 s polling watchdog invoking a health-check delegate |
| `src/Controller/ZrcSdkController.cs` | Command-failure strikes, `RunHealthCheck` probe, `DeclareOffline`, self-perpetuating never-give-up reconnect (removed 10-attempt cap) |
| `src/Controller/IZoomRoomController.cs` | Added `HealthStateChanged` event + `RunHealthCheck` |
| `src/ZoomRoom.cs` | Wire watchdog, drive devcomm from health truth, start `CommunicationMonitor` |

## 5. How we re-tested (new code)

Deploy note / gotcha encountered: the fresh plugin build was linked to **Essentials
`3.0.0-dev-v3-routing.60`** while the processor was running **`.59`**, causing a type-load failure:

```
Unable to get types for assembly PepperDash.Essentials.Plugins.Zoom.Room:
Declaration referenced in a method implementation cannot be a final method. Type: 'ZoomRoom'.
```

This is a compiled-against-vs-runtime version skew (a base method finalized between `.59` and `.60`),
**not** a watchdog bug. Resolved by **upgrading the processor's Essentials runtime to `.60`** to match
the plugin.

Re-ran the same ~10-minute network-down test. On network restore the device recovered **on its own,
with no restart**:

```
[zoomRoom] Health watchdog reports online
[zoomRoom] SDK connection state changed: Established (0)
```

**Result (new code): PASS.** The watchdog detected the dead link, retried on the never-give-up
backoff, flipped devcomm to reflect reality, and reconnected automatically when the network returned —
eliminating the manual restart.

## 6. Notes & caveats

- **Bogus-contact test caveat:** `InviteContactById` with a fake contact ID returns
  `MeetWithIMUsers returned failure` even when genuinely connected (the SDK rejects the ID). On the new
  code this does **not** cause a false offline: the failure triggers a probe, `GetMeetingStatus()`
  succeeds, strikes reset, and it stays online. Only a *failed probe* (real dead link) declares offline.
- **Version alignment:** worth aligning all epis + processor on one Essentials dev version to avoid the
  `.57`/`.59`/`.60` skew that produced the type-load error above.
- **Silent vs clean drop:** a clean disconnect (reboot/unpair) fires the SDK event and the old
  event-driven path handled it; the bug was specifically the **silent/half-open** drop where the event
  never fires. The watchdog covers both.
