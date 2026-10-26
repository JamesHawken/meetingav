# Meeting AV

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

1. Copy `MeetingAV.exe` anywhere and double-click it. It puts a
   **Meeting AV** shortcut on the desktop; use that from then on.
2. The first time, it switches on OBS's remote control (close OBS first if it's
   open) and starts OBS minimised by the clock, with the virtual camera on.
   If OBS crashed last time, it starts normally anyway (no Safe Mode question);
   one that's stuck or not answering is closed and started again.
3. It opens JW Library too. Put the congregation's Zoom invite link in
   Settings → This PC → **Zoom meeting link** and it joins that meeting as well,
   each time it starts (or on **Get ready again** in the tray menu), then
   arranges the windows as **Arrange** does.
4. The first time, a **setup guide** walks through the rest: OBS, the camera
   shots, meeting times, JW Library, the timer screen, following people and the
   tablet. Settings → This PC runs it again.

From half an hour before each meeting, a bar at the top says **Ready** - or
what to sort out, with a button to fix it where Meeting AV can. Tap **?** next
to anything for a quick explanation.

Meeting AV opens on **Monitor**: Zoom sees JW Library's screen (the yeartext, and the
5-minute countdown before the meeting). It
starts with Windows and keeps itself up to date from this repository: when a
newer `MeetingAV.exe` is committed, it downloads it and a **Restart to update**
button appears (both can be switched off under Settings → This PC).

It opens in **Simple** mode: the timer, the picture and the shots. Everything
automatic keeps running; **Simple** switches to the full view (sound, settings); **Menu** has the grid view, tablet, checks and settings.

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
| Speaker + Reader (optional) | `speaker` and `reader` |
| Monitor           | `monitor` or `screen`        |
| Holding screen (optional) | `holding`, `countdown`, `waiting` or `welcome` |

Any other scenes appear under **Other scenes**. A holding screen isn't needed:
Monitor is the shot before the meeting.

## During the meeting

- **Shots:** click one, or press `1`–`9`. Picking a shot yourself turns
  *Follow people* off until you switch it back on.
- **Start camera:** turns on the OBS virtual camera (what Zoom sees); the button
  only shows while it's off. `V` turns it on or off.
- **Auto media** (a tick on the shots): a JW Library picture cuts to
  *Speaker + Monitor*, a video or song to *Monitor*, and closing it goes back to
  the previous shot. `A` ticks or unticks it.
- **Auto camera** (a tick on the shots): looks twice a second and cuts once a
  new shot has held for about 1.5 seconds (at least 4 seconds on each). It follows people, switching between
  Demonstrations, Speaker, Table and Platform wide depending on where they stand
  on the platform. Picking a shot yourself unticks it. With a Reader
  scene, someone standing at the table cuts to Reader and someone sitting to
  Table: under Settings → Shots → *Follow people* (drag the lines on the picture), set the reader line between a seated
  head and a standing one.
- **Which meeting:** Saturday and Sunday it's the weekend meeting (its times, its
  parts, its countdown, its media in JW Library), Monday to Friday the midweek one.
- **Hall TV: JW Library | Zoom** (by the shots): picks what the hall TV shows, JW
  Library's screen or Zoom's second window (Zoom's "dual monitors" mode keeps it
  on that screen). It takes a few seconds, and leaves JW Library on the meeting's
  media.
- **Arrange** (top): Meeting AV on the left half of the PC's screen, JW Library
  top right, Zoom's meeting bottom right (Zoom's home window is minimised). JW Library's TV and Zoom's window on
  it are left alone.
- **Meeting media** and **Playlists** (by the Auto ticks): open this week's
  meeting in JW Library, on its pictures and videos, or JW Library's playlists.
- **Auto-start:** at the meeting time, turns the camera on and cuts to
  Speaker. Set the times under *Change times*.
- **Shot changes:** a shot you pick cuts straight away; when Meeting AV changes
  the shot by itself it fades (a third of a second). Settings → Shots switches the
  fade off.
- **Notes:** a short note at the bottom says what just happened ("Table on air",
  "Auto camera off").
- **Grid:** just the shots, big. Swipe right (or tap the left dot) for the shot
  on air, full screen.
- **Studies:** starting the Watchtower Study or the Congregation Bible Study on
  the timer puts the *Speaker + Reader* scene on air, and Auto camera keeps to
  it while that part runs.
- **Check:** one list of everything that's ready or needs fixing.
- **Timer** (like OnlyT): the card lists the next meeting's parts, the midweek
  one's from that week's workbook on wol.jw.org (kept for offline use). Tap a
  part, press **Start** as the speaker begins, and the speakers' screen counts
  down: green, amber in the last minute, red and counting up once over time.
  Under the time, *Next:* shows the part after it.
  The **space bar** does the same as Start/Stop while Meeting AV is the window in
  front.
  **Stop** gets the next part ready. With no part loaded it shows the time of
  day. It sits at the top. Until you press **Start**, which begins the meeting's
  first part, the speakers see the time of day; switch **Screen** on to show it, and pick which screen under
  Settings → Meeting. In the grid view, a fourth page (swipe left past Sound)
  controls it from a tablet.
- **Sound:** speakers, Zoom, JW Library and the **microphone** (Windows' default
  recording device), each with a volume, mute and level meter. **Hold microphone
  steady** keeps it at its level: if Zoom or Windows turns it up or down, Meeting AV
  puts it back within half a second (it stays held after a restart).
- **Sound to Zoom** (Settings → Sound): OBS takes the hall mixer's feed, levels it
  (quiet readers lifted, loud speakers and songs held back, a limiter so it never
  clips) and plays it into **VB-CABLE** (free, vb-audio.com), which Zoom uses as its
  microphone ("CABLE Output"). The hall speakers aren't affected. A **To Zoom** meter
  in Sound shows what participants hear. Turn off Zoom's automatic microphone volume.
- **Zoom muted:** 30 seconds before the start, Meeting AV clicks **Mute all** in
  Zoom's Participants panel, unticks "Allow participants to unmute themselves" and
  confirms: everyone but you is muted and can't unmute themselves. It moves the
  mouse for a moment, then puts it back. You need to be host or co-host.
  **Zoom mics: Muted | Can unmute** (by the shots) does the same at any time, or
  lets everyone unmute themselves again (nobody is unmuted for them).
  Settings → Meeting switches it off.
- **Countdown:** in the last 5 minutes before the meeting, the speakers' screen
  and JW Library's screen (the hall TV) show "Meeting starts in 4:59…". For it,
  Meeting AV switches JW Library's second display off, and back on as it reaches
  0:00, leaving JW Library on the meeting's media. Until then pictures
  and videos don't reach the TV; **JW Library on the TV** (by the timer) gives it
  back sooner. Settings → Meeting switches the countdown on the TV off.
- **JW Library:** opened while Meeting AV is running, it goes straight to the
  meeting's media. If its second display opened as a window on the PC (the TV
  was off or not plugged in yet), Meeting AV moves it onto the TV once it's there.
- **Zoom on the hall TV** needs Zoom's "Use dual monitors" setting, with the TV
  connected when you join: Zoom only sets up its TV window on joining.

## Tablet or phone

1. Join the same Wi-Fi as the PC.
2. On the PC, click **Menu** → **Connect a tablet** and scan the code with the tablet's camera.
3. Once it shows the shots, add it to the home screen (on an iPad: Share →
   **Add to Home Screen**). It then opens full screen, already connected.

If the home-screen app says OBS didn't accept the password, delete it and do
steps 2–3 again.

## Look

Settings → This PC → **Look**: the same as Windows, light, dark, or **dim** for a
darkened hall. **Size** makes everything smaller to fit more on the screen
(Compact, More compact, Smallest), or larger. Each PC and tablet keeps its own.

## Moving to another PC

Settings → This PC → **Save settings to a file** puts the meeting times, shots, following people, sound and
timer choices in `Meeting AV settings.json` (in Downloads). On the other PC, **Load settings from a file**.
The OBS scenes move separately: in OBS, **Scene Collection → Export**, then **Import** on the other PC.

## Building

Run `build.bat`. It uses the C# compiler that comes with Windows, so there's
nothing to install, and it builds `obs-remote.html`, `timer.html`, `Start-Meeting.ps1`, `lib/`
and `webview2/` into `MeetingAV.exe`. A copy of `obs-remote.html` placed next
to the exe is used instead of the built-in one, which helps when trying out
changes.

| File               | What it is                                                      |
|--------------------|-----------------------------------------------------------------|
| `MeetingAV.cs` | The app: its window, the tablet server, JW Library detection    |
| `MeetingAV.manifest` | Built into the exe: measures every screen in real pixels, whatever its scaling |
| `obs-remote.html`  | The control page, on the PC and on tablets                      |
| `timer.html`       | The timer screen the speakers see                               |
| `Start-Meeting.ps1`| Gets OBS ready (remote control on, started with the virtual camera), opens JW Library, joins Zoom |
| `lib/`             | Person detector, QR codes, tablet app icons                     |
| `webview2/`        | Microsoft WebView2, which shows the page in the app's own window |

`Start-Meeting.bat` and `Serve-Remote.bat` are the older way to run it without
the app: the page opens in the browser and tablets are served by a PowerShell
script.
