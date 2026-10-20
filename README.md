# Meeting Camera

Controls the OBS camera for a congregation meeting on Zoom: pick shots, follow
whoever is on the platform, cut to the screen when JW Library shows a picture or
video, and run it all from a tablet.

## What you need

- Windows 10 or 11
- [OBS Studio](https://obsproject.com) 28 or later (its remote control and
  virtual camera are built in)
- JW Library from the Microsoft Store, with **Second display** on
- Zoom, with **OBS Virtual Camera** picked as the camera

## Getting started

1. Copy `MeetingCamera.exe` anywhere and double-click it. It puts a
   **Meeting Camera** shortcut on the desktop; use that from then on.
2. The first time, it switches on OBS's remote control (close OBS first if it's
   open) and starts OBS minimised by the clock, with the virtual camera on.
3. Open JW Library and Zoom yourself.

Meeting Camera opens on the holding screen, with a countdown to the meeting.

## OBS scenes

Shots are found by their scene names in OBS (not case-sensitive):

| Shot              | Scene name contains          |
|-------------------|------------------------------|
| Demonstrations    | `demo`                       |
| Platform wide     | `wide` or `platform`         |
| Table             | `table`                      |
| Reader (optional) | `reader` or `lecteur`        |
| Speaker           | `speaker`                    |
| Speaker + Monitor | `speaker` and `monitor`      |
| Monitor           | `monitor` or `screen`        |
| Holding screen    | `holding`, `countdown`, `waiting` or `welcome` |

Any other scenes appear under **Other scenes**. If there's no holding screen,
Meeting Camera makes one.

## During the meeting

- **Shots:** click one, or press `1`–`9`. Picking a shot yourself turns
  *Follow people* off until you switch it back on.
- **Start camera:** turns on the OBS virtual camera (what Zoom sees); the button
  only shows while it's off. `V` turns it on or off.
- **JW Library media:** a picture cuts to *Speaker + Monitor*, a video or song
  to *Monitor*, and closing it goes back to the previous shot. `A` turns this
  on or off.
- **Follow people:** switches between Demonstrations, Speaker, Table and
  Platform wide depending on where people stand on the platform. With a Reader
  scene, someone standing at the table cuts to Reader and someone sitting to
  Table: under Follow people → *Adjust…*, set the reader line between a seated
  head and a standing one.
- **Auto-start:** at the meeting time, turns the camera on and cuts from the
  holding screen to Platform wide. Set the times under *Change times*.
- **Grid:** just the shots, big. Swipe right (or tap the left dot) for the shot
  on air, full screen.
- **Check:** one list of everything that's ready or needs fixing.
- **Timer** (like OnlyT): the card lists the next meeting's parts, the midweek
  one's from that week's workbook on wol.jw.org (kept for offline use). Tap a
  part, press **Start** as the speaker begins, and the speakers' screen counts
  down: green, amber in the last minute, red and counting up once over time.
  **Stop** gets the next part ready. With no part loaded it shows the time of
  day. It sits at the top; switch **Screen** on to show it, and pick which screen
  under Settings → Timer.

## Tablet or phone

1. Join the same Wi-Fi as the PC.
2. On the PC, click **Tablet** and scan the code with the tablet's camera.
3. Once it shows the shots, add it to the home screen (on an iPad: Share →
   **Add to Home Screen**). It then opens full screen, already connected.

If the home-screen app says OBS didn't accept the password, delete it and do
steps 2–3 again.

## Building

Run `build.bat`. It uses the C# compiler that comes with Windows, so there's
nothing to install, and it builds `obs-remote.html`, `timer.html`, `Start-Meeting.ps1`, `lib/`
and `webview2/` into `MeetingCamera.exe`. A copy of `obs-remote.html` placed next
to the exe is used instead of the built-in one, which helps when trying out
changes.

| File               | What it is                                                      |
|--------------------|-----------------------------------------------------------------|
| `MeetingCamera.cs` | The app: its window, the tablet server, JW Library detection    |
| `obs-remote.html`  | The control page, on the PC and on tablets                      |
| `timer.html`       | The timer screen the speakers see                               |
| `Start-Meeting.ps1`| Gets OBS ready (remote control on, started with the virtual camera) |
| `lib/`             | Person detector, QR codes, tablet app icons                     |
| `webview2/`        | Microsoft WebView2, which shows the page in the app's own window |

`Start-Meeting.bat` and `Serve-Remote.bat` are the older way to run it without
the app: the page opens in the browser and tablets are served by a PowerShell
script.
