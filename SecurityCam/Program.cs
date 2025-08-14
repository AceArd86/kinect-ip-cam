using Microsoft.Kinect;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SecurityCam
{
    internal static class Program
    {
        // ===== Constants =====
        private const int COLOR_WIDTH = 640;
        private const int COLOR_HEIGHT = 480;
        private const int IR_BYTES_PER_PIXEL = 2;
        private const int BGRA_BYTES_PER_PIXEL = 4;
        private const int MJPEG_PORT = 8080;
        private const int MOTION_PIXELS_THRESHOLD = 1200;
        private const int DEPTH_DIFF_THRESHOLD_MM = 80;
        private const float BG_ALPHA = 0.01f;
        private const int SNAPSHOT_COOLDOWN_MS = 8000;

        // ===== Auth =====
        private static readonly string AUTH_USER = Environment.GetEnvironmentVariable("KINECTCAM_USER");
        private static readonly string AUTH_PASS = Environment.GetEnvironmentVariable("KINECTCAM_PASS");

        // ===== Night/Day & rendering settings =====
        private static bool AUTO_NIGHT = true;
        private static float NIGHT_LUMA_THRESHOLD = 36f;
        private static float DAY_LUMA_HYSTERESIS = 44f;
        private static bool IR_GREEN_TINT = false;
        private static bool IR_SMOOTH = true;
        private static long JPEG_QUALITY = 60;

        // --- Tilt control ---
        private static readonly int TILT_STEP = 2;                 // degrees per click
        private static readonly int TILT_COOLDOWN_MS = 800;        // don't spam the motor
        private static DateTime lastTiltSet = DateTime.MinValue;

        // --- Audio ---
        private static string lastAudioPath = "";
        private static volatile bool audioRecording = false;
        private static readonly object audioLock = new object();
        private static Task audioTask = Task.CompletedTask;

        // ===== HTTP / motion =====
        private static HttpListener http;
        private static volatile bool running = true;

        // ===== Kinect state =====
        private static KinectSensor sensor;
        private static bool irMode = false;

        // Color/IR buffers
        private static byte[] colorPixels;
        private static byte[] irRgb32;

        // Latest frame for HTTP
        private static Bitmap latestColorBitmap;
        private static readonly object frameLock = new object();

        // Skeleton tracking
        private static Skeleton[] skelBuffer;
        private static Skeleton[] lastSkels;
        private static readonly object skelLock = new object();
        private static bool SHOW_SKELETON = true;

        // Depth for motion
        private static short[] depthPixels;
        private static float[] bgDepth;
        private static DateTime lastMotion = DateTime.MinValue;

        // UI status
        private static string lastCapturePath = "";

        // Throttle audio recordings
        private static DateTime lastAudioRecord = DateTime.MinValue;
        private const int MAX_AUDIO_FILES = 100;
        private const int MAX_SNAPSHOT_FILES = 100;

        [STAThread]
        private static void Main(string[] args)
        {
            Directory.CreateDirectory("captures");

            sensor = KinectSensor.KinectSensors.FirstOrDefault(s => s.Status == KinectStatus.Connected);
            if (sensor == null)
            {
                Console.WriteLine("No Kinect found.");
                return;
            }
            if (string.IsNullOrEmpty(AUTH_USER) || string.IsNullOrEmpty(AUTH_PASS))
            {
                Console.WriteLine(
                    "Missing credentials. Please set environment variables KINECTCAM_USER and KINECTCAM_PASS.");
                return;
            }


            // Wire events
            sensor.ColorFrameReady += OnColorFrameReady;
            sensor.DepthFrameReady += OnDepthFrameReady;

            // Enable streams
            sensor.DepthStream.Enable(DepthImageFormat.Resolution320x240Fps30);
            sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
            colorPixels = new byte[sensor.ColorStream.FramePixelDataLength];

            // Allocate depth buffers
            depthPixels = new short[sensor.DepthStream.FramePixelDataLength];
            bgDepth = new float[depthPixels.Length];

            sensor.SkeletonStream.Enable(new TransformSmoothParameters
            {
                Smoothing = 0.5f,
                Correction = 0.5f,
                Prediction = 0.5f,
                JitterRadius = 0.05f,
                MaxDeviationRadius = 0.04f
            });
            sensor.SkeletonStream.TrackingMode = SkeletonTrackingMode.Default;
            sensor.SkeletonFrameReady += OnSkeletonFrameReady;

            sensor.Start();
            Console.WriteLine("Kinect started.");

            PublishPlaceholder();

            new Thread(HotkeyLoop) { IsBackground = true }.Start();

            StartHttp();
            StartCleanupThread(); // Start cleanup thread
            Console.WriteLine($"HTTP listening on http://+:{MJPEG_PORT}/");
            Console.WriteLine($"HTTP on http://localhost:{MJPEG_PORT}/   (root → links)");
            Console.WriteLine("Press ENTER to quit.");
            Console.ReadLine();

            running = false;
            try { http?.Stop(); } catch (Exception ex) { Console.WriteLine($"HTTP stop error: {ex}"); }
            try { sensor.Stop(); } catch (Exception ex) { Console.WriteLine($"Sensor stop error: {ex}"); }
        }

        private static void HotkeyLoop()
        {
            Console.WriteLine("Hotkeys → I:IR  C:Color  A:AutoNight  T:Tint  S:Smooth  Q/W:JPEG -/+");
            while (running)
            {
                if (!Console.KeyAvailable) { Thread.Sleep(50); continue; }
                var k = Console.ReadKey(true).Key;
                if (k == ConsoleKey.I) { AUTO_NIGHT = false; SwitchToIr(); }
                if (k == ConsoleKey.C) { AUTO_NIGHT = false; SwitchToRgb(); }
                if (k == ConsoleKey.A) { AUTO_NIGHT = !AUTO_NIGHT; Console.WriteLine("AutoNight: " + (AUTO_NIGHT ? "ON" : "OFF")); }
                if (k == ConsoleKey.T) { IR_GREEN_TINT = !IR_GREEN_TINT; Console.WriteLine("Tint: " + (IR_GREEN_TINT ? "Green" : "Gray")); }
                if (k == ConsoleKey.S) { IR_SMOOTH = !IR_SMOOTH; Console.WriteLine("Smooth: " + (IR_SMOOTH ? "ON" : "OFF")); }
                if (k == ConsoleKey.Q) { JPEG_QUALITY = Math.Max(10, JPEG_QUALITY - 5); Console.WriteLine("JPEG: " + JPEG_QUALITY); }
                if (k == ConsoleKey.W) { JPEG_QUALITY = Math.Min(100, JPEG_QUALITY + 5); Console.WriteLine("JPEG: " + JPEG_QUALITY); }
            }
        }

        // ---- Helpers for clamping (no Math.Clamp on 4.7.2) ----
        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min; if (v > max) return max; return v;
        }
        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min; if (v > max) return max; return v;
        }

        private static bool RequireAuth(HttpListenerContext ctx)
        {
            var p = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
            bool needsAuth =
                p == "/stream" || p == "/latest.jpg" || p == "/stream-jpg" ||
                p == "/ui" || p.StartsWith("/api/") || p == "/last.wav";

            if (!needsAuth) return false;

            var hdr = ctx.Request.Headers["Authorization"];
            if (!string.IsNullOrEmpty(hdr) && hdr.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var token = hdr.Substring(6).Trim();
                    var raw = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                    if (raw == AUTH_USER + ":" + AUTH_PASS) return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RequireAuth] Exception: {ex}");
                }
            }

            try
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.AddHeader("WWW-Authenticate", "Basic realm=\"KinectCam\"");
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RequireAuth] Challenge Exception: {ex}");
            }
            return true;
        }

        private static void SwitchToRgb()
        {
            try
            {
                if (sensor.ColorStream.IsEnabled) sensor.ColorStream.Disable();
                sensor.ColorStream.Enable(ColorImageFormat.RgbResolution640x480Fps30);
                colorPixels = new byte[sensor.ColorStream.FramePixelDataLength];
                irMode = false;
                Console.WriteLine("COLOR mode.");
            }
            catch (Exception ex) { Console.WriteLine("SwitchToRgb: " + ex.Message); }
        }

        private static void SwitchToIr()
        {
            try
            {
                if (sensor.ColorStream.IsEnabled) sensor.ColorStream.Disable();
                sensor.ColorStream.Enable(ColorImageFormat.InfraredResolution640x480Fps30);
                colorPixels = new byte[sensor.ColorStream.FramePixelDataLength];
                if (irRgb32 == null || irRgb32.Length != COLOR_WIDTH * COLOR_HEIGHT * BGRA_BYTES_PER_PIXEL)
                    irRgb32 = new byte[COLOR_WIDTH * COLOR_HEIGHT * BGRA_BYTES_PER_PIXEL];
                irMode = true;
                Console.WriteLine("INFRARED mode.");
            }
            catch (Exception ex) { Console.WriteLine("SwitchToIr: " + ex.Message); }
        }

        /// <summary>
        /// Handles incoming color frames from the Kinect sensor.
        /// </summary>
        private static void OnColorFrameReady(object sender, ColorImageFrameReadyEventArgs e)
        {
            using (var f = e.OpenColorImageFrame())
            {
                if (f == null) return;

                if (f.Format == ColorImageFormat.InfraredResolution640x480Fps30)
                {
                    ProcessInfraredFrame(f);
                    return;
                }

                ProcessColorFrame(f);
            }
        }

        private static void ProcessInfraredFrame(ColorImageFrame f)
        {
            if (colorPixels.Length != f.PixelDataLength)
                colorPixels = new byte[f.PixelDataLength];
            f.CopyPixelDataTo(colorPixels);

            if (irRgb32 == null || irRgb32.Length != f.Width * f.Height * BGRA_BYTES_PER_PIXEL)
                irRgb32 = new byte[f.Width * f.Height * BGRA_BYTES_PER_PIXEL];

            IR16LE_To_BGRA(colorPixels, irRgb32, f.Width, f.Height);

            using (var bmp = CreateBitmapFromFrame(f.Width, f.Height, irRgb32))
            {
                AddInfraredOverlay(bmp);
                SaveLatestBitmap(bmp);
            }
        }

        private static void ProcessColorFrame(ColorImageFrame f)
        {
            f.CopyPixelDataTo(colorPixels);

            if (AUTO_NIGHT)
            {
                float luma = AvgLumaFromBGRA(colorPixels, f.Width, f.Height, 8);
                if (!irMode && luma < NIGHT_LUMA_THRESHOLD) { SwitchToIr(); return; }
                if (irMode && luma > DAY_LUMA_HYSTERESIS) { SwitchToRgb(); return; }
            }

            using (var bmp = CreateBitmapFromFrame(f.Width, f.Height, colorPixels))
            {
                AddColorOverlay(bmp);
                SaveLatestBitmap(bmp);
            }
        }

        private static Bitmap CreateBitmapFromFrame(int width, int height, byte[] pixelData)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                  ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                int rowBytes = width * BGRA_BYTES_PER_PIXEL;
                IntPtr dest = bd.Scan0;
                int srcOff = 0;
                for (int y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(pixelData, srcOff, dest, rowBytes);
                    dest = IntPtr.Add(dest, bd.Stride);
                    srcOff += rowBytes;
                }
            }
            finally { bmp.UnlockBits(bd); }

            return bmp;
        }

        private static void AddInfraredOverlay(Bitmap bmp)
        {
            using (var g = Graphics.FromImage(bmp))
            {
                var text = $"Night (IR)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                var sz = g.MeasureString(text, SystemFonts.DefaultFont);
                g.FillRectangle(Brushes.Black, 0, 0, sz.Width + 10, sz.Height + 6);
                g.DrawString(text, SystemFonts.DefaultFont, Brushes.LawnGreen, 5, 3);
            }
            if (SHOW_SKELETON)
            {
                using (var g2 = Graphics.FromImage(bmp))
                    DrawSkeletons(g2, ColorImageFormat.InfraredResolution640x480Fps30, bmp.Width, bmp.Height);
            }
        }

        private static void AddColorOverlay(Bitmap bmp)
        {
            using (var g = Graphics.FromImage(bmp))
            {
                var since = (DateTime.UtcNow - lastMotion).TotalSeconds;
                var label = since < 3 ? "MOTION" : "Day";
                var text = $"{label}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                var sz = g.MeasureString(text, SystemFonts.DefaultFont);
                g.FillRectangle(Brushes.Black, 0, 0, sz.Width + 10, sz.Height + 6);
                g.DrawString(text, SystemFonts.DefaultFont, Brushes.LawnGreen, 5, 3);
            }
            if (SHOW_SKELETON)
            {
                using (var g2 = Graphics.FromImage(bmp))
                    DrawSkeletons(g2, ColorImageFormat.RgbResolution640x480Fps30, bmp.Width, bmp.Height);
            }
        }

        private static void SaveLatestBitmap(Bitmap bmp)
        {
            lock (frameLock)
            {
                latestColorBitmap?.Dispose();
                latestColorBitmap = (Bitmap)bmp.Clone();
            }
        }

        private static void OnDepthFrameReady(object sender, DepthImageFrameReadyEventArgs e)
        {
            using (var frame = e.OpenDepthImageFrame())
            {
                if (frame == null) return;
                frame.CopyPixelDataTo(depthPixels);

                int changes = 0;
                for (int i = 0; i < depthPixels.Length; i++)
                {
                    int depth = depthPixels[i] >> DepthImageFrame.PlayerIndexBitmaskWidth;
                    if (depth == 0 || depth > 4000) continue;

                    float bg = bgDepth[i];
                    if (bg == 0) bg = depth;
                    float diff = Math.Abs(depth - bg);
                    if (diff > DEPTH_DIFF_THRESHOLD_MM) changes++;
                    bgDepth[i] = bg + BG_ALPHA * (depth - bg);
                }

                if (changes > MOTION_PIXELS_THRESHOLD)
                {
                    var now = DateTime.UtcNow;
                    bool cooled = (now - lastMotion).TotalMilliseconds > SNAPSHOT_COOLDOWN_MS;
                    lastMotion = now;
                    if (cooled) SaveSnapshot();

                    // Audio cooldown: only record if enough time has passed
                    if ((now - lastAudioRecord).TotalMilliseconds > 10000 && !audioRecording)
                    {
                        lastAudioRecord = now;
                        StartAudioRecording(6);
                    }
                }
            }
        }

        private static void OnSkeletonFrameReady(object sender, SkeletonFrameReadyEventArgs e)
        {
            using (var f = e.OpenSkeletonFrame())
            {
                if (f == null) return;

                if (skelBuffer == null || skelBuffer.Length != f.SkeletonArrayLength)
                    skelBuffer = new Skeleton[f.SkeletonArrayLength];

                f.CopySkeletonDataTo(skelBuffer);

                lock (skelLock) { lastSkels = skelBuffer.ToArray(); }
            }
        }

        private static void DrawSkeletons(Graphics g, ColorImageFormat fmt, int w, int h)
        {
            Skeleton[] skels = null;
            lock (skelLock) { if (lastSkels != null) skels = lastSkels.ToArray(); }
            if (skels == null) return;

            using (var pTracked = new Pen(Color.Lime, 3f))
            using (var pInf = new Pen(Color.FromArgb(140, Color.Lime), 1.5f))
            using (var jointBrush = new SolidBrush(Color.DeepSkyBlue))
            {
                foreach (var s in skels)
                {
                    if (s == null || s.TrackingState == SkeletonTrackingState.NotTracked) continue;

                    DrawBone(g, fmt, s.Joints, JointType.Head, JointType.ShoulderCenter, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.ShoulderCenter, JointType.ShoulderLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.ShoulderCenter, JointType.ShoulderRight, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.ShoulderLeft, JointType.ElbowLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.ElbowLeft, JointType.WristLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.WristLeft, JointType.HandLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.ShoulderRight, JointType.ElbowRight, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.ElbowRight, JointType.WristRight, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.WristRight, JointType.HandRight, pTracked, pInf);

                    DrawBone(g, fmt, s.Joints, JointType.ShoulderCenter, JointType.Spine, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.Spine, JointType.HipCenter, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.HipCenter, JointType.HipLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.HipLeft, JointType.KneeLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.KneeLeft, JointType.AnkleLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.AnkleLeft, JointType.FootLeft, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.HipCenter, JointType.HipRight, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.HipRight, JointType.KneeRight, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.KneeRight, JointType.AnkleRight, pTracked, pInf);
                    DrawBone(g, fmt, s.Joints, JointType.AnkleRight, JointType.FootRight, pTracked, pInf);

                    foreach (JointType jt in Enum.GetValues(typeof(JointType)))
                    {
                        var j = s.Joints[jt];
                        if (j.TrackingState == JointTrackingState.NotTracked) continue;
                        var pt = MapToColor(j.Position, fmt);
                        g.FillEllipse(jointBrush, pt.X - 3, pt.Y - 3, 6, 6);
                    }
                }
            }
        }

        private static void DrawBone(Graphics g, ColorImageFormat fmt, JointCollection joints,
                                     JointType a, JointType b, Pen pTracked, Pen pInf)
        {
            var j1 = joints[a]; var j2 = joints[b];
            if (j1.TrackingState == JointTrackingState.NotTracked ||
                j2.TrackingState == JointTrackingState.NotTracked) return;

            var p1 = MapToColor(j1.Position, fmt);
            var p2 = MapToColor(j2.Position, fmt);
            var pen = (j1.TrackingState == JointTrackingState.Tracked &&
                       j2.TrackingState == JointTrackingState.Tracked) ? pTracked : pInf;
            g.DrawLine(pen, p1, p2);
        }

        private static PointF MapToColor(SkeletonPoint sp, ColorImageFormat fmt)
        {
            var cp = sensor.CoordinateMapper.MapSkeletonPointToColorPoint(sp, fmt);
            return new PointF(cp.X, cp.Y);
        }

        // ==========================
        // HTTP STARTUP
        // ==========================
        private static void StartHttp()
        {
            string[] prefixes = {
                $"http://+:{MJPEG_PORT}/",
                $"http://localhost:{MJPEG_PORT}/"
            };

            foreach (var p in prefixes)
            {
                try
                {
                    http = new HttpListener();
                    http.Prefixes.Add(p);
                    http.Start();
                    new Thread(HttpLoop) { IsBackground = true }.Start();
                    Console.WriteLine("HTTP listening on " + p);
                    return;
                }
                catch (HttpListenerException ex)
                {
                    Console.WriteLine($"HTTP bind failed on {p}: {ex.Message}");
                    try { http.Close(); } catch (Exception closeEx) { Console.WriteLine($"HTTP close error: {closeEx}"); }
                }
            }

            Console.WriteLine(
                $"Could not start HTTP. Try running once as admin:\n" +
                $"  netsh http add urlacl url=http://+:{MJPEG_PORT}/ user=Everyone");
        }

        // ==========================
        // HTTP LOOP (ROUTER)
        // ==========================
        private static void HttpLoop()
        {
            while (running)
            {
                HttpListenerContext ctx = null;
                try { ctx = http.GetContext(); }
                catch (Exception ex)
                {
                    if (!running) break;
                    Console.WriteLine($"HttpLoop error: {ex}");
                    continue;
                }

                // 401 if not authorized (RequireAuth handles the response)
                if (RequireAuth(ctx)) continue;

                var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
                WaitCallback cb = null;

                if (path == "/" || path == "")
                {
                    cb = s => RedirectToUi((HttpListenerContext)s);
                }
                else if (path == "/ui")
                {
                    cb = s => HandleUi((HttpListenerContext)s);
                }
                else if (path == "/stream")
                {
                    cb = s => HandleMjpeg((HttpListenerContext)s);
                }
                else if (path == "/latest.jpg")
                {
                    cb = s => HandleJpeg((HttpListenerContext)s);
                }
                else if (path == "/last.wav")
                {
                    cb = s => HandleLastWav((HttpListenerContext)s);
                }
                else if (path.StartsWith("/captures/"))
                {
                    string rel = path.Substring("/captures/".Length);
                    cb = s => ServeStatic((HttpListenerContext)s, "captures", rel);
                }
                else if (path.StartsWith("/scripts/"))
                {
                    string rel = path.Substring("/scripts/".Length);
                    cb = s => ServeStatic((HttpListenerContext)s, "scripts", rel);
                }
                else if (path.StartsWith("/api/"))
                {
                    cb = s => HandleApi((HttpListenerContext)s);
                }
                else
                {
                    cb = s => NotFound((HttpListenerContext)s);
                }

                ThreadPool.UnsafeQueueUserWorkItem(cb, ctx);
            }
        }

        // ==========================
        // HELPERS: Redirect / 404 / Static files
        // ==========================
        private static void RedirectToUi(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.StatusCode = 302;                 // temporary redirect
                ctx.Response.RedirectLocation = "/ui";
                ctx.Response.AddHeader("Cache-Control", "no-store");
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private static void NotFound(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes("Not Found");
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        private static void ServeStatic(HttpListenerContext ctx, string folder, string relPath)
        {
            try
            {
                var full = Path.Combine(folder, relPath);
                if (!File.Exists(full))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                // Content-Type
                string ext = Path.GetExtension(full).ToLowerInvariant();
                ctx.Response.ContentType =
                    ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" :
                    ext == ".png" ? "image/png" :
                    ext == ".wav" ? "audio/wav" :
                    ext == ".js" ? "application/javascript" :
                    ext == ".css" ? "text/css" :
                                    "application/octet-stream";

                // No-cache for dynamic-ish assets
                ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.AddHeader("Pragma", "no-cache");
                ctx.Response.AddHeader("Expires", "0");

                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ctx.Response.ContentLength64 = fs.Length;
                    fs.CopyTo(ctx.Response.OutputStream);
                }
            }
            catch (IOException ioEx)
            {
                Console.WriteLine($"ServeStatic IO: {ioEx.Message}");
                ctx.Response.StatusCode = 503;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ServeStatic error: {ex}");
                ctx.Response.StatusCode = 500;
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        // ==========================
        // SNAPSHOT
        // ==========================
        private static void SaveSnapshot()
        {
            Bitmap snap = null;
            lock (frameLock)
            {
                if (latestColorBitmap != null)
                    snap = (Bitmap)latestColorBitmap.Clone();
            }
            if (snap == null) return;

            Directory.CreateDirectory("captures");
            var path = Path.Combine("captures", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg");

            try
            {
                using (snap)
                using (var fs = File.Create(path))
                {
                    SaveJpeg(fs, snap, JPEG_QUALITY);
                }
                lastCapturePath = path;
                Console.WriteLine("Captured: " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("SaveSnapshot error: " + ex.Message);
            }
        }

        // Audio
        private static void StartAudioRecording(int seconds = 5)
        {
            lock (audioLock)
            {
                if (audioRecording || !audioTask.IsCompleted) return;
                audioRecording = true;
                audioTask = Task.Run(() =>
                {
                    try { RecordAudioSeconds(seconds); }
                    catch (Exception ex) { Console.WriteLine("Audio record error: " + ex.Message); }
                    finally { audioRecording = false; }
                });
            }
        }

        private static void RecordAudioSeconds(int seconds)
        {
            const int sampleRate = 16000;
            const short bitsPerSample = 16;
            const short channels = 1;
            int bytesToGrab = seconds * sampleRate * (bitsPerSample / 8) * channels;

            var src = sensor.AudioSource;
            src.EchoCancellationMode = EchoCancellationMode.None;
            src.AutomaticGainControlEnabled = false;
            src.NoiseSuppression = true;
            src.BeamAngleMode = BeamAngleMode.Adaptive;

            var path = Path.Combine("captures", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".wav");
            Directory.CreateDirectory("captures");

            using (var stream = src.Start())
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                WriteWavHeader(fs, channels, sampleRate, bitsPerSample, 0);

                byte[] buf = new byte[4096];
                int total = 0;
                while (total < bytesToGrab)
                {
                    int toRead = Math.Min(buf.Length, bytesToGrab - total);
                    int read = stream.Read(buf, 0, toRead);
                    if (read <= 0) break;
                    fs.Write(buf, 0, read);
                    total += read;
                }
                fs.Flush();
                UpdateWavSizes(fs, total);
            }
            src.Stop();

            lastAudioPath = path;
            Console.WriteLine("Audio recorded: " + path);
        }

        // MJPEG streaming
        private static void HandleMjpeg(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.SendChunked = true;
                ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
                ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.AddHeader("Pragma", "no-cache");
                ctx.Response.AddHeader("Expires", "0");

                var os = ctx.Response.OutputStream;

                while (running)
                {
                    Bitmap frame = null;
                    lock (frameLock)
                    {
                        if (latestColorBitmap != null)
                            frame = (Bitmap)latestColorBitmap.Clone();
                    }
                    if (frame == null)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    byte[] jpg;
                    using (frame)
                    using (var ms = new MemoryStream())
                    {
                        SaveJpeg(ms, frame, JPEG_QUALITY);
                        jpg = ms.ToArray();
                    }

                    var header = Encoding.ASCII.GetBytes(
                        "--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpg.Length + "\r\n\r\n");
                    os.Write(header, 0, header.Length);
                    os.Write(jpg, 0, jpg.Length);
                    os.Write(new byte[] { (byte)'\r', (byte)'\n' }, 0, 2);
                    os.Flush();

                    Thread.Sleep(50);
                }
            }
            catch (HttpListenerException)
            {
                // client disconnected
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleMjpeg error: {ex}");
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); }
                catch (Exception closeEx)
                {
                    Console.WriteLine($"HandleMjpeg close error: {closeEx}");
                }
            }
        }

        // Latest JPEG
        private static void HandleJpeg(HttpListenerContext ctx)
        {
            Bitmap bmp = null;
            try
            {
                ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.AddHeader("Pragma", "no-cache");
                ctx.Response.AddHeader("Expires", "0");

                lock (frameLock)
                {
                    if (latestColorBitmap != null)
                        bmp = (Bitmap)latestColorBitmap.Clone();
                }

                if (bmp == null)
                {
                    ctx.Response.StatusCode = 503;
                    return;
                }

                using (bmp)
                using (var ms = new MemoryStream())
                {
                    SaveJpeg(ms, bmp, JPEG_QUALITY);
                    var buf = ms.ToArray();
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.ContentLength64 = buf.Length;
                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                }
            }
            catch (HttpListenerException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleJpeg error: {ex}");
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); }
                catch (Exception ex) { Console.WriteLine($"HandleJpeg close error: {ex}"); }
            }
        }

        private static void HandleJpegStream(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html";
                ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.AddHeader("Pragma", "no-cache");
                ctx.Response.AddHeader("Expires", "0");

                var html =
                    "<html><body style='margin:0;background:#000'>" +
                    "<img id='v' src='/latest.jpg' style='width:100%;height:auto;'/>" +
                    "<script>setInterval(function(){v.src='/latest.jpg?'+Date.now()},300);</script>" +
                    "</body></html>";

                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleJpegStream error: {ex}");
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); }
                catch (Exception ex) { Console.WriteLine($"HandleJpegStream close error: {ex}"); }
            }
        }

        // UI page
        private static void HandleUi(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.AddHeader("Pragma", "no-cache");
                ctx.Response.AddHeader("Expires", "0");

                string html = File.ReadAllText("web/ui.html");
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleUi error: {ex}");
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); }
                catch (Exception ex) { Console.WriteLine($"HandleUi close error: {ex}"); }
            }
        }

        // ---- API (clean) ----
        private static void HandleApi(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

            // /api/status -> JSON status
            if (path == "/api/status")
            {
                WriteJson(ctx, BuildStatusJson());
                return;
            }

            // /api/audios -> list wavs
            if (path == "/api/audios")
            {
                var files = Directory.GetFiles("captures", "*.wav")
                    .OrderByDescending(f => f)
                    .Select(f => Path.GetFileName(f))
                    .ToArray();
                WriteJson(ctx, "{\"audios\":" + ToJsonArray(files) + "}");
                return;
            }

            // /api/set -> query params to set things
            if (path == "/api/set")
            {
                var q = ctx.Request.QueryString;

                Func<string, string> get = k => q[k];
                Func<string, bool> has = k => !string.IsNullOrEmpty(q[k]);
                Func<string, bool> isToggle = k => string.Equals(q[k], "toggle", StringComparison.OrdinalIgnoreCase);
                Func<string, bool> isTrue = k => q[k] == "1" || string.Equals(q[k], "true", StringComparison.OrdinalIgnoreCase);

                int iTmp;
                float fTmp;

                // MODE
                if (has("mode"))
                {
                    var m = get("mode");
                    if (m.Equals("ir", StringComparison.OrdinalIgnoreCase)) { AUTO_NIGHT = false; SwitchToIr(); }
                    if (m.Equals("rgb", StringComparison.OrdinalIgnoreCase)) { AUTO_NIGHT = false; SwitchToRgb(); }
                }

                // TOGGLES
                if (has("auto")) AUTO_NIGHT = isToggle("auto") ? !AUTO_NIGHT : isTrue("auto");
                if (has("tint")) IR_GREEN_TINT = isToggle("tint") ? !IR_GREEN_TINT : get("tint").Equals("green", StringComparison.OrdinalIgnoreCase);
                if (has("smooth")) IR_SMOOTH = isToggle("smooth") ? !IR_SMOOTH : isTrue("smooth");

                // TUNING
                if (int.TryParse(get("jpeg"), NumberStyles.Integer, CultureInfo.InvariantCulture, out iTmp))
                    JPEG_QUALITY = Clamp(iTmp, 10, 100);
                if (float.TryParse(get("night"), NumberStyles.Float, CultureInfo.InvariantCulture, out fTmp))
                    NIGHT_LUMA_THRESHOLD = Clamp(fTmp, 0f, 100f);
                if (float.TryParse(get("day"), NumberStyles.Float, CultureInfo.InvariantCulture, out fTmp))
                    DAY_LUMA_HYSTERESIS = Math.Max(NIGHT_LUMA_THRESHOLD + 1f, Math.Min(100f, fTmp));

                // SNAP / AUDIO
                if (has("snap")) SaveSnapshot();
                if (int.TryParse(get("record"), NumberStyles.Integer, CultureInfo.InvariantCulture, out iTmp))
                    StartAudioRecording(Clamp(iTmp, 1, 30));

                // TILT
                if (has("tilt") || has("tiltAbs"))
                {
                    try
                    {
                        if (sensor == null) throw new InvalidOperationException("No Kinect");
                        if ((DateTime.Now - lastTiltSet).TotalMilliseconds < TILT_COOLDOWN_MS)
                            throw new InvalidOperationException("Tilt cooldown");

                        int min = sensor.MinElevationAngle;
                        int max = sensor.MaxElevationAngle;
                        int cur = sensor.ElevationAngle;
                        int target = cur;

                        if (has("tiltAbs"))
                        {
                            if (int.TryParse(get("tiltAbs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out iTmp))
                                target = iTmp;
                        }
                        else
                        {
                            var t = get("tilt").ToLowerInvariant();
                            int delta =
                                (t == "up") ? +TILT_STEP :
                                (t == "down") ? -TILT_STEP :
                                (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out iTmp) ? iTmp : 0);
                            target = cur + delta;
                        }

                        target = Clamp(target, min, max);
                        if (target != cur)
                        {
                            sensor.ElevationAngle = target;
                            lastTiltSet = DateTime.Now;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Tilt set error: " + ex.Message);
                    }
                }

                WriteJson(ctx, BuildStatusJson());
                return;
            }

            // Unknown API
            try { ctx.Response.StatusCode = 404; ctx.Response.Close(); } catch { }
        }

        // Root (optional, not used by router, but kept for reference)
        private static void HandleRoot(HttpListenerContext ctx)
        {
            try
            {
                var html = @"<html><body style='background:#111;color:#ddd;font-family:Segoe UI,Arial,sans-serif'>
<h2>Kinect IP Cam</h2>
<p><a href='/ui'>/ui</a> – control panel</p>
<p><a href='/stream'>/stream</a> – MJPEG live view</p>
<p><a href='/stream-jpg'>/stream-jpg</a> – alt live view (refreshing JPG)</p>
<p><a href='/latest.jpg'>/latest.jpg</a> – last frame</p>
<p>Status: " + (irMode ? "Night (IR)" : "Day (RGB)") + @"</p>
</body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex) { Console.WriteLine($"HandleRoot error: {ex}"); }
        }

        private static void HandleLastWav(HttpListenerContext ctx)
        {
            try
            {
                var path = lastAudioPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    ctx.Response.StatusCode = 404; ctx.Response.Close(); return;
                }
                var bytes = File.ReadAllBytes(path);
                ctx.Response.ContentType = "audio/wav";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                ctx.Response.AddHeader("Pragma", "no-cache");
                ctx.Response.AddHeader("Expires", "0");
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex) { Console.WriteLine($"HandleLastWav error: {ex}"); }
            finally { try { ctx.Response.OutputStream.Close(); } catch (Exception closeEx) { Console.WriteLine($"HandleLastWav output close error: {closeEx}"); } }
        }

        private static void CleanupOldFiles()
        {
            try
            {
                var audioFiles = Directory.GetFiles("captures", "*.wav")
                    .OrderByDescending(File.GetCreationTime)
                    .ToList();
                foreach (var file in audioFiles.Skip(MAX_AUDIO_FILES))
                    File.Delete(file);

                var snapshotFiles = Directory.GetFiles("captures", "*.jpg")
                    .OrderByDescending(File.GetCreationTime)
                    .ToList();
                foreach (var file in snapshotFiles.Skip(MAX_SNAPSHOT_FILES))
                    File.Delete(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cleanup error: {ex.Message}");
            }
        }

        private static void StartCleanupThread()
        {
            new Thread(() =>
            {
                while (running)
                {
                    CleanupOldFiles();
                    Thread.Sleep(TimeSpan.FromMinutes(10));
                }
            })
            { IsBackground = true }.Start();
        }

        private static void PublishPlaceholder()
        {
            using (var bmp = new Bitmap(COLOR_WIDTH, COLOR_HEIGHT))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.DrawString("Waiting for Kinect frame...", SystemFonts.DefaultFont, Brushes.White, 10, 10);
                lock (frameLock)
                {
                    latestColorBitmap?.Dispose();
                    latestColorBitmap = (Bitmap)bmp.Clone();
                }
            }
        }

        private static float AvgLumaFromBGRA(byte[] buf, int w, int h, int step = 8)
        {
            long sum = 0, count = 0;
            int stride = w * BGRA_BYTES_PER_PIXEL;
            for (int y = 0; y < h; y += step)
            {
                int row = y * stride;
                for (int x = 0; x < w; x += step)
                {
                    int i = row + x * BGRA_BYTES_PER_PIXEL;
                    byte b = buf[i + 0], g = buf[i + 1], r = buf[i + 2];
                    sum += (299 * r + 587 * g + 114 * b) / 1000;
                    count++;
                }
            }
            if (count == 0) return 0f;
            return (float)(sum / (double)count);
        }

        private static void IR16LE_To_BGRA(byte[] ir16le, byte[] outBGRA, int w, int h)
        {
            int step = 8, minV = int.MaxValue, maxV = int.MinValue;
            for (int y = 0; y < h; y += step)
            {
                int row = y * w;
                for (int x = 0; x < w; x += step)
                {
                    int i = ((row + x) << 1);
                    int s = ir16le[i] | (ir16le[i + 1] << 8);
                    if (s <= 0) continue;
                    if (s < minV) minV = s; if (s > maxV) maxV = s;
                }
            }
            if (minV == int.MaxValue || maxV <= minV) { minV = 1; maxV = 2000; }
            float scale = 255f / (maxV - minV);

            int n = w * h, src = 0;
            for (int i = 0; i < n; i++, src += 2)
            {
                int s = ir16le[src] | (ir16le[src + 1] << 8);
                if (s <= 0) s = minV;
                int v = (int)((s - minV) * scale + 0.5f);
                if (v < 0) v = 0; if (v > 255) v = 255;

                int o = i * BGRA_BYTES_PER_PIXEL;
                if (IR_GREEN_TINT) { outBGRA[o + 0] = 0; outBGRA[o + 1] = (byte)v; outBGRA[o + 2] = 0; }
                else { byte b = (byte)v; outBGRA[o + 0] = b; outBGRA[o + 1] = b; outBGRA[o + 2] = b; }
                outBGRA[o + 3] = 0;
            }

            if (IR_SMOOTH) BoxBlurBGRA(outBGRA, w, h, 1);
        }

        private static void BoxBlurBGRA(byte[] buf, int w, int h, int passes = 1)
        {
            int stride = w * BGRA_BYTES_PER_PIXEL;
            byte[] tmp = new byte[buf.Length];

            for (int p = 0; p < passes; p++)
            {
                Array.Copy(buf, tmp, buf.Length);
                for (int y = 1; y < h - 1; y++)
                {
                    int row = y * stride;
                    for (int x = 1; x < w - 1; x++)
                    {
                        int o = row + x * BGRA_BYTES_PER_PIXEL;
                        int sumB = 0, sumG = 0, sumR = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ro = (y + dy) * stride + (x - 1) * BGRA_BYTES_PER_PIXEL;
                            for (int dx = 0; dx < 3; dx++, ro += BGRA_BYTES_PER_PIXEL)
                            {
                                sumB += tmp[ro + 0];
                                sumG += tmp[ro + 1];
                                sumR += tmp[ro + 2];
                            }
                        }
                        buf[o + 0] = (byte)(sumB / 9);
                        buf[o + 1] = (byte)(sumG / 9);
                        buf[o + 2] = (byte)(sumR / 9);
                    }
                }
            }
        }

        private static void SaveJpeg(Stream s, Bitmap bmp, long quality)
        {
            if (quality < 1) quality = 1;
            if (quality > 100) quality = 100;

            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using (var ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bmp.Save(s, codec, ps);
            }
        }

        private static string ToJsonArray(string[] arr)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(arr[i].Replace("\\", "/")).Append("\"");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static void WriteJson(HttpListenerContext ctx, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json ?? "{}");
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            try { ctx.Response.OutputStream.Close(); } catch { }
        }

        private static string BuildStatusJson()
        {
            var sb = new StringBuilder(256);
            sb.Append("{");

            sb.Append("\"mode\":\"").Append(irMode ? "IR" : "RGB").Append("\"");
            sb.Append(",\"autoNight\":").Append(AUTO_NIGHT ? "true" : "false");
            sb.Append(",\"tint\":").Append(IR_GREEN_TINT ? "true" : "false");
            sb.Append(",\"smooth\":").Append(IR_SMOOTH ? "true" : "false");

            sb.Append(",\"jpeg\":").Append(JPEG_QUALITY);
            sb.Append(",\"night\":").Append((int)NIGHT_LUMA_THRESHOLD);
            sb.Append(",\"day\":").Append((int)DAY_LUMA_HYSTERESIS);

            sb.Append(",\"lastCapture\":\"").Append((lastCapturePath ?? "").Replace("\\", "/")).Append("\"");
            sb.Append(",\"lastAudio\":\"").Append((lastAudioPath ?? "").Replace("\\", "/")).Append("\"");

            int tilt = 0;
            try { if (sensor != null) tilt = sensor.ElevationAngle; } catch { }
            sb.Append(",\"tilt\":").Append(tilt);

            sb.Append("}");
            return sb.ToString();
        }

        private static void WriteWavHeader(FileStream fs, short channels, int sampleRate, short bitsPerSample, int dataSize)
        {
            var bw = new BinaryWriter(fs, Encoding.ASCII, true);
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write(channels);
            bw.Write(sampleRate);
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);
        }

        private static void UpdateWavSizes(FileStream fs, int dataSize)
        {
            long pos = fs.Position;
            using (var bw = new BinaryWriter(fs, Encoding.ASCII, true))
            {
                fs.Seek(4, SeekOrigin.Begin); bw.Write(36 + dataSize);
                fs.Seek(40, SeekOrigin.Begin); bw.Write(dataSize);
            }
            fs.Seek(pos, SeekOrigin.Begin);
        }
    }
}