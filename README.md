README.md

Kinect IP Cam (Xbox 360 Kinect)

Turn an old Xbox 360 Kinect into a simple IP camera with night (IR) mode, motion snapshots, short audio clips, and a web UI.

https://github.com/AceArd86/kinect-ip-cam

Features

RGB and Infrared (IR) live view (auto day/night switch optional)

MJPEG stream (/stream) + latest JPEG (/latest.jpg)

Motion-triggered snapshots (saved to captures/)

Short WAV recordings on motion or on demand

Web UI (/ui) with controls (mode, auto-night, tint, smoothing, JPEG quality, tilt, snapshot, record)

Basic Auth (set via environment variables)

Optional tilt control (Kinect motor)

Automatic cleanup of old media

Requirements

Windows + Kinect for Xbox 360 sensor

Kinect for Windows SDK v1.8

.NET Framework 4.7.2 (your project already targets this)

Quick start (90 seconds)

1) Set login (environment variables) — run in PowerShell once:

setx KINECTCAM_USER "kinect"
setx KINECTCAM_PASS "change-me"


Open a new terminal/VS after setting them.

2) Grant the local HTTP URL once (admin PowerShell):

netsh http add urlacl url=http://+:8080/ user=Everyone


3) Build & run

Debug or Release from Visual Studio

Open: http://localhost:8080/ui
(You’ll be prompted for the Basic Auth user/pass you set above.)

Endpoints
Endpoint	Description
/ui	Web control panel
/stream	MJPEG live video
/latest.jpg	Last frame as JPEG
`/captures/*.jpg	*.wav`
/api/status	Get current status (JSON) and/or set options using query params below
/api/status query params (GET)

Mode: mode=rgb or mode=ir

Auto night: auto=toggle or auto=0|1

IR tint: tint=toggle or tint=green|gray

IR smooth: smooth=toggle or smooth=0|1

Quality: jpeg=10..100

Thresholds: night=0..100, day=0..100 (day must be > night)

Snapshot: snap=1

Record: record=1..30 (seconds)

Tilt: tilt=up|down|±N or tiltAbs=N (clamped to Kinect’s min/max)

Examples

# Switch to IR
curl -u kinect:change-me "http://localhost:8080/api/status?mode=ir"

# Toggle auto-night
curl -u kinect:change-me "http://localhost:8080/api/status?auto=toggle"

# Set JPEG quality to 70
curl -u kinect:change-me "http://localhost:8080/api/status?jpeg=70"

# Take a snapshot
curl -u kinect:change-me "http://localhost:8080/api/status?snap=1"

# Record 6 seconds of audio
curl -u kinect:change-me "http://localhost:8080/api/status?record=6"

# Tilt up 2 degrees
curl -u kinect:change-me "http://localhost:8080/api/status?tilt=+2"

# Absolute tilt -7
curl -u kinect:change-me "http://localhost:8080/api/status?tiltAbs=-7"

Hotkeys (console)
I = IR        C = Color
A = AutoNight T = Tint (IR green/gray)
S = IR smooth Q/W = JPEG quality -/+

HTTPS / Remote access (optional)

If you want to access the UI over the internet, put it behind TLS. Easiest is a tunnel (e.g., Cloudflare Tunnel) pointing to http://localhost:8080. Keep Basic Auth enabled and use a strong password.

Dev / Build

Target framework: .NET Framework 4.7.2

Kinect SDK: v1.8

Build: standard Visual Studio build (Debug/Release)

Troubleshooting

Port in use / bind fails: change MJPEG_PORT in code, or free the port.

401 repeatedly: environment variables not loaded? Restart VS/terminal or verify with:

[System.Environment]::GetEnvironmentVariable("KINECTCAM_USER","Machine")


No video: ensure the Kinect is powered (separate power brick) and recognized in Device Manager.

Tilt not moving: some sensors report the angle but can’t move; also there’s a cooldown to protect the motor.

License

MIT (see LICENSE)

Security notes

Uses Basic Auth (username/password). Don’t expose it publicly without HTTPS.

Credentials should be provided via environment variables (see below). Avoid committing secrets.

Environment variables

Environment variables override any defaults in code:

KINECTCAM_USER — Basic Auth username

KINECTCAM_PASS — Basic Auth password

You can also add an _env.example (below) and document it, but don’t commit real credentials.

_env.example
KINECTCAM_USER=kinect
KINECTCAM_PASS=change-me


On Windows, this is just for documentation; the app reads environment variables from the system (set with setx).

.gitignore additions

Add these lines so your repo doesn’t fill with media:

# App media output
captures/

Credits

Xbox 360 Kinect + Kinect SDK v1.8

IR grayscale mapping, UI and motion/audio glue by @AceArd86
