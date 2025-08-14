# SecurityCam

Turn a **Kinect for Xbox 360** into a simple IP camera with a web UI, IR "night mode", motion snapshots, audio clips, and tilt control — all in a single .NET Framework console app.

> ⚠️ This targets **.NET Framework 4.7.2** and **Kinect SDK v1.8** (Microsoft.Kinect). Windows only.

---

## Features

- **Web UI** (`/ui`) with buttons & slider (Color / IR, Auto Night, Tint, Smooth, JPEG quality)
- **Live view** as **MJPEG** (`/stream`) or single JPG (`/latest.jpg`)
- **Auto Night**: switches RGB ↔ IR based on scene brightness
- **IR rendering**: grayscale (default) with optional green tint + smoothing
- **Motorized tilt**: ▲/▼ buttons or API control (`tilt`, `tiltAbs`)
- **Motion-triggered snapshots** using the depth stream
- **Audio recorder**: capture 6–30s WAV from the Kinect mic array
- **Basic Auth** on the web endpoints (username/password via environment variables)
- **Snapshot & audio history** served from the `captures/` folder

---

## Requirements

- **Hardware**: Kinect for Xbox 360 (v1) + USB/power adapter
- **OS**: Windows 10/11
- **SDK**: [Kinect for Windows SDK v1.8](https://www.microsoft.com/en-us/download/details.aspx?id=40278)
- **Dev**: Visual Studio 2019/2022 with **.NET Framework 4.7.2** targeting pack

> This app uses the Microsoft.Kinect library from the SDK; do **not** commit the SDK DLLs to the repo.

---

## Quick Start

1. Install **Kinect SDK v1.8**. Plug in Kinect (motor light briefly blinks; drivers should load).
2. Clone this repo, open the solution in **Visual Studio**, make sure the project targets **.NET Framework 4.7.2**.
3. Set credentials as **environment variables** (so you don’t commit secrets):

   **PowerShell (user scope)**
   ```powershell
   [System.Environment]::SetEnvironmentVariable("KINECTCAM_USER","myuser","User")
   [System.Environment]::SetEnvironmentVariable("KINECTCAM_PASS","mypassword","User")

Endpoints
Path	Method	Description:
  /ui	GET	Web control panel (HTML/JS)
  /stream	GET	MJPEG live stream
  /latest.jpg	GET	Latest frame as JPEG
  /last.wav	GET	Last recorded audio clip
  /captures/*.jpg/.wav	GET	Saved snapshots and audio files
  /api/status	GET	JSON with current state (mode, flags, thresholds, JPEG, last files, tilt)
  /api/set?...	GET	Apply settings (see below)
  /api/set query parameters

Mode & flags:
  mode=rgb|ir
  auto=toggle|true|false
  tint=toggle|green|gray
  smooth=toggle|true|false

Quality & thresholds:
  jpeg=10..100
  night=0..100 (IR switch-on luma)
  day=0..100 (IR→RGB switch-back luma; must be > night)

Capture:
  snap=1 (save snapshot immediately)
  record=1..30 (record N seconds of audio)

Tilt:
  tilt=up|down|+2|-2 (relative; step is clamped and rate-limited)
  tiltAbs=-7 (absolute degrees; clamped to Kinect range, typically -27..+27)

Examples:
  /api/set?mode=ir&jpeg=70
  /api/set?auto=toggle
  /api/set?snap=1
  /api/set?record=6
  /api/set?tilt=down
  /api/set?tiltAbs=10

Web UI:
  Color / IR – switch video source
  Auto Night – auto-switch based on brightness
  Tint / Smooth – IR rendering controls
  JPEG Quality – trade bandwidth for clarity
  Take Snapshot – saves to captures/
  Record Audio – captures WAV to captures/
  Tilt ▲/▼ – moves the Kinect motor (cooldown ~800ms). Status shows Tilt: N°

I'f the UI doesn’t reflect new JS, force refresh: /ui?v=2, Ctrl+F5, or clear cache.

Hotkeys (console window):
  I – switch to IR
  C – switch to Color
  A – toggle Auto Night
  T – toggle IR Tint
  S – toggle IR smoothing
  Q/W – JPEG quality down/up

Port Forwarding / Remote Access (optional):
  LAN only: open http://<PC-IP>:8080/ui from your phone on Wi-Fi.
  Remote (simplest): use Cloudflare Tunnel to expose http://localhost:8080 to a public hostname with auth in front.
  Router port forward: forward TCP 8080 to your PC’s LAN IP, and allow in Windows Firewall. Strongly recommended to keep Basic Auth enabled and use HTTPS/CF tunnel if exposing to the internet.

Troubleshooting:
  “HTTP bind failed on http://+:8080/” → run:
  netsh http add urlacl url=http://+:8080/ user=Everyone
  401 / auth popup loops → check KINECTCAM_USER/PASS env vars; restart VS/console after setting.
  Black image or no frames → ensure Kinect SDK 1.8 is installed; check Device Manager; try another USB port/power.
  IR looks noisy → enable Smooth in UI; try Tint off (grayscale).
  UI not updating → reload with /ui?v=2 (cache bust).
  Tilt not moving → Kinect v1 range is ~-27..+27; there’s a short cooldown between moves.

Security notes:
  Credentials are not stored in code/repo; set via env vars.
  When exposing publicly, prefer Cloudflare Tunnel or a TLS reverse proxy.
  Keep captures/ in .gitignore — it can contain sensitive imagery/audio.
