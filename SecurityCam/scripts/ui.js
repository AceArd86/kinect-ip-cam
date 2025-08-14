// Minimal UI logic (no history lists)

function $(id) { return document.getElementById(id); }

function fetchStatus() {
    fetch('/api/status')
        .then(r => r.json())
        .then(data => {
            const s =
                `Mode: <b>${data.mode}</b> | AutoNight: <b>${data.autoNight}</b> | ` +
                `Tint: <b>${data.tint}</b> | Smooth: <b>${data.smooth}</b> | ` +
                `JPEG: <b>${data.jpeg}</b> | Tilt: <b>${data.tilt}°</b><br>` +
                `Night threshold: <b>${data.night}</b> | Day threshold: <b>${data.day}</b><br>` +
                `Last Capture: <b>${data.lastCapture || '-'}</b> | Last Audio: <b>${data.lastAudio || '-'}</b>`;

            document.getElementById('status').innerHTML = s;

            const jq = document.getElementById('jpegQuality');
            if (jq) jq.value = data.jpeg;
        })
        .catch(console.error);
}


// --- WRITE actions use /api/set ---
function setMode(mode) { fetch('/api/set?mode=' + encodeURIComponent(mode)).then(fetchStatus); }
function toggleAutoNight() { fetch('/api/set?auto=toggle').then(fetchStatus); }
function toggleTint() { fetch('/api/set?tint=toggle').then(fetchStatus); }
function toggleSmooth() { fetch('/api/set?smooth=toggle').then(fetchStatus); }
function setJpegQuality(val) { fetch('/api/set?jpeg=' + encodeURIComponent(val)).then(fetchStatus); }
function takeSnapshot() { fetch('/api/set?snap=1').then(fetchStatus); }
function recordAudio() { fetch('/api/set?record=6').then(fetchStatus); }
function tilt(dir) {fetch('/api/set?tilt=' + encodeURIComponent(dir)).then(fetchStatus); }
// (optional) absolute angle
function setTiltAbs(deg) {fetch('/api/set?tiltAbs=' + encodeURIComponent(deg)).then(fetchStatus); }


// Audio: load & play the newest file
function playLast() {
    const aud = $('aud');
    if (!aud) return;
    aud.src = '/last.wav?ts=' + Date.now();
    aud.load();
    aud.play && aud.play().catch(() => { });
}

// poll status every 2s
document.addEventListener('DOMContentLoaded', () => {
    fetchStatus();
    setInterval(fetchStatus, 2000);
});
