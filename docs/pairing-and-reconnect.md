# Pairing and reconnect

How the plug-in keeps each Zoom Room controlled without anyone sending `repairZoomRoom` or a new
activation code by hand. The timing lives in `src/Controller/ReconnectPolicy.cs` and is covered by
`tests/ReconnectPolicyTests.cs`; `ZrcSdkController` applies it.

## When the repair is sent

The repair is the same request as the `repairZoomRoom` console command: reconnect with the stored
pairing. A successful pair stops the schedule. The console commands still work at any time.

| Situation | When the repair is sent |
|---|---|
| Program start | Each room asks in turn, 5 s apart, in the order the rooms are created (0 s, 5 s, 10 s ...). |
| No answer within 30 s of that first request | At the 30 s mark, then as below. |
| A failure that is not a refusal (room or cloud unreachable, link dropped) | After 5, 10, 20 and 30 s, then every 60 s until it connects. |
| The health check (every 30 s) finds the link dead | Starts the same schedule. It also covers a room that has not connected since the program started. |
| First refusal of the stored pairing (`5002`, `100000401`) | Once more after 60 s, because a first refusal can be false. An unused configured activation code is tried first, once. |
| Second refusal in a row | Every 10 minutes, with one warning that the room needs a new activation code. |

Every request carries the room's offset (5 s per room ahead of it on the shared SDK), so rooms that
drop together never ask Zoom at the same moment.

## Why

- Several rooms asking Zoom at the same moment have produced a false refusal for one room and no
  answer at all for another. Before this change neither case was ever retried, so the room waited for
  someone to send the repair.
- A pairing Zoom has really cancelled does not come back by retrying, and refused requests every
  minute coincided with the other rooms on the same SDK dropping. A refused room therefore slows to
  every 10 minutes and says it needs a new code.
- Activation codes work once, so the plug-in never spends a code that has already paired the room.

## Logs

- `Pairing in N s, after the N Zoom Room(s) ahead of it on this SDK.`
- `No answer from Zoom 30 s after asking to pair; sending the repair now.`
- `Zoom refused the stored pairing (code ...). A first refusal can be false, so it is checked again in 60 s.`
- `Zoom refused the stored pairing twice in a row (code ...). This Zoom Room needs a new activation code ...`
- Later refusals log at Debug only.
