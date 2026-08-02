using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Ipc;
using WinTAKTracker.Services.Reporting;
using WinTAKTracker.Services.Tak;

namespace WinTAKTracker.Services.Video;

public sealed class VideoFeedRuntime
{
    public required string FeedId { get; init; }
    public required string Tag { get; init; }
    public bool IsLive { get; set; }
    /// <summary>LIVE and FFmpeg still healthy — safe to advertise CoT / FOV.</summary>
    public bool IsPlayable { get; set; }
    public string? StreamUrl { get; set; }
    public string? LastError { get; set; }
    public BitmapSource? PreviewFrame { get; set; }
}

/// <summary>Tray-owned camera preview, FFmpeg stream/record, and CoT announce push.</summary>
public sealed class VideoService : IDisposable
{
    private readonly AppHost _host;
    private readonly object _gate = new();
    private readonly Dictionary<string, FeedWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startingFeeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _pingTimer;
    private readonly DispatcherTimer _gpsSampleTimer;
    private DateTimeOffset _lastPing = DateTimeOffset.MinValue;

    public event EventHandler? StateChanged;

    public VideoService(AppHost host)
    {
        _host = host;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewTimer.Tick += (_, _) => TickPreview();
        _previewTimer.Start();
        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _pingTimer.Tick += (_, _) => TickPing();
        _pingTimer.Start();
        _gpsSampleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _gpsSampleTimer.Tick += (_, _) => TickGpsSample();
        _gpsSampleTimer.Start();
    }

    private DateTimeOffset _lastGpsSample = DateTimeOffset.MinValue;

    public bool IsConfigured => _host.Config.Video.IsConfigured;

    public int LiveCount
    {
        get { lock (_gate) return _workers.Values.Count(w => w.IsLive); }
    }

    public IReadOnlyList<VideoFeedRuntime> SnapshotRuntimes()
    {
        lock (_gate)
        {
            return _workers.Values.Select(w => new VideoFeedRuntime
            {
                FeedId = w.FeedId,
                Tag = w.Tag,
                IsLive = w.IsLive,
                IsPlayable = w.IsPlayable,
                StreamUrl = w.StreamUrl,
                LastError = w.LastError,
                PreviewFrame = w.LastPreview,
            }).ToList();
        }
    }

    public async Task StartFeedAsync(string feedId)
    {
        var cfg = _host.Config.Video;
        var feed = cfg.Feeds.FirstOrDefault(f => f.Id == feedId)
                   ?? throw new InvalidOperationException("Feed not found.");
        if (!feed.Enabled || string.IsNullOrWhiteSpace(feed.CameraName))
            throw new InvalidOperationException("Feed is not configured.");

        var ffmpeg = FfmpegLocator.Resolve(cfg.FfmpegPath)
                     ?? throw new InvalidOperationException("FFmpeg not found. Set path under Settings → Video.");

        FeedWorker worker;
        lock (_gate)
        {
            if (!_workers.TryGetValue(feedId, out worker!))
            {
                worker = new FeedWorker(feedId, feed.Tag);
                _workers[feedId] = worker;
            }

            if (worker.IsLive) return;
            _startingFeeds.Add(feedId);
        }

        try
        {
        var (advertiseUrl, ffmpegUrl) = BuildStreamUrls(cfg, feed);
        var camIndex = CameraEnumerator.ResolveIndex(feed.CameraName, cfg.FfmpegPath);
        var dshowName = CameraEnumerator.ResolveDshowVideoName(feed.CameraName, cfg.FfmpegPath);
        var dshowAlt = CameraEnumerator.ResolveDshowVideoAlternativeName(feed.CameraName, cfg.FfmpegPath);
        try
        {
            VideoRecordingHelper.EnforceFolderLimit(
                ResolveRecordingFolder(cfg), cfg.RecordingMaxFolderMb, cfg.RecordingOverLimitPolicy);
        }
        catch (InvalidOperationException ex)
        {
            worker.LastError = ex.Message;
            StateChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }

        // DirectShow cameras are exclusive — release every OpenCv preview before FFmpeg opens.
        CloseAllPreviews();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(900).ConfigureAwait(false);

        var attempts = new List<(string Label, string Device, bool StreamAudio)>
        {
            ("friendly", dshowName, cfg.StreamAudio),
        };
        if (!string.IsNullOrWhiteSpace(dshowAlt) &&
            !string.Equals(dshowAlt, dshowName, StringComparison.OrdinalIgnoreCase))
            attempts.Add(("alternative", dshowAlt!, cfg.StreamAudio));
        if (cfg.StreamAudio)
        {
            attempts.Add(("friendly-video-only", dshowName, false));
            if (!string.IsNullOrWhiteSpace(dshowAlt))
                attempts.Add(("alternative-video-only", dshowAlt!, false));
        }

        var started = false;
        string? lastTried = dshowName;
        foreach (var (label, device, streamAudio) in attempts)
        {
            lastTried = device;
            _host.Log.Info("Video",
                $"Starting feed '{feed.Tag}' dshow[{label}]=\"{device}\" advertise={advertiseUrl}");
            var args = BuildFfmpegArgs(cfg, feed, device, ffmpegUrl, worker, streamAudio);
            started = await worker.StartProcessAsync(ffmpeg, args, advertiseUrl).ConfigureAwait(false);
            if (started) break;
            _host.Log.Warn("Video", $"FFmpeg open failed ({label}): {worker.LastError}");
            await Task.Delay(400).ConfigureAwait(false);
        }

        if (!started)
        {
            worker.CancelPreparedRecording();
            worker.OpenPreview(camIndex);
            StateChanged?.Invoke(this, EventArgs.Empty);
            throw new InvalidOperationException(
                worker.LastError ??
                $"FFmpeg failed to open camera \"{lastTried}\". Pick the device again under Settings → Video.");
        }

        if (cfg.RecordingEnabled)
            worker.BeginPreparedRecording(TimeSpan.FromMinutes(Math.Max(1, cfg.RecordingSegmentMinutes)));

        VideoAudioCues.PlayStart(cfg.AudioCuesEnabled);
        await PushAnnounceAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            lock (_gate) _startingFeeds.Remove(feedId);
        }
    }

    private void CloseAllPreviews()
    {
        lock (_gate)
        {
            foreach (var w in _workers.Values)
                w.ClosePreview();
        }
    }

    public async Task StopFeedAsync(string feedId)
    {
        FeedWorker? worker;
        lock (_gate) _workers.TryGetValue(feedId, out worker);
        if (worker is null) return;
        var camIndex = CameraEnumerator.ResolveIndex(
            _host.Config.Video.Feeds.FirstOrDefault(f => f.Id == feedId)?.CameraName,
            _host.Config.Video.FfmpegPath);
        await worker.StopAsync().ConfigureAwait(false);
        worker.OpenPreview(camIndex);
        VideoAudioCues.PlayStop(_host.Config.Video.AudioCuesEnabled);
        await PushAnnounceAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAllAsync()
    {
        foreach (var feed in _host.Config.Video.Feeds.Where(f => f.Enabled))
        {
            try { await StartFeedAsync(feed.Id).ConfigureAwait(false); }
            catch { /* per-feed error surfaced on worker */ }
        }
    }

    public async Task StopAllAsync()
    {
        List<string> ids;
        lock (_gate) ids = _workers.Keys.ToList();
        foreach (var id in ids)
            await StopFeedAsync(id).ConfigureAwait(false);
    }

    public async Task PushAnnounceAsync()
    {
        var cfg = _host.Config;
        var identity = _host.Core.GetActiveIdentity();
        var fix = _host.AttachedToService
            ? null
            : _host.Gps.CurrentFix;
        var course = fix?.CourseDegrees
                     ?? _host.LastServiceStatus?.CourseDegrees;

        List<VideoFeedAnnounce> feeds;
        lock (_gate)
        {
            // CoT / FOV only for playable LIVE feeds (FFmpeg still running, URL set, no hard error).
            feeds = _workers.Values.Where(w => w.IsPlayable)
                .Select(w =>
                {
                    var feedCfg = cfg.Video.Feeds.FirstOrDefault(f => f.Id == w.FeedId)
                                  ?? new VideoFeedSettings { Id = w.FeedId, Tag = w.Tag };
                    return new VideoFeedAnnounce
                    {
                        FeedId = w.FeedId,
                        Tag = w.Tag,
                        StreamUrl = w.StreamUrl!,
                        Alias = CotVideoBuilder.MakeAlias(identity.Callsign, w.Tag),
                        VideoUid = CotVideoBuilder.MakeVideoUid(cfg.DeviceUid, w.Tag),
                        HfovDegrees = feedCfg.HfovDegrees,
                        VfovDegrees = feedCfg.VfovDegrees,
                        RangeMeters = feedCfg.RangeMeters,
                        AzimuthDegrees = CotVideoBuilder.ResolveAzimuth(cfg, feedCfg, course),
                        ElevationDegrees = feedCfg.ElevationDegrees,
                    };
                }).ToList();
        }

        var state = new VideoAnnounceState
        {
            Active = feeds.Count > 0,
            SendFovSensorMarker = cfg.Video.SendFovSensorMarker && feeds.Count > 0,
            Feeds = feeds,
        };

        _host.Core.SetVideoAnnounce(state);
        if (_host.ServiceClient is not null)
        {
            try
            {
                await _host.ServiceClient.SetVideoAnnounceAsync(VideoAnnounceDto.From(state))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _host.Log.Warn("Video", $"SetVideoAnnounce IPC failed: {ex.Message}");
            }
        }
    }

    public void EnsureWorkersForConfig()
    {
        lock (_gate)
        {
            foreach (var feed in _host.Config.Video.Feeds.Where(f => f.Enabled))
            {
                if (!_workers.TryGetValue(feed.Id, out var worker))
                {
                    worker = new FeedWorker(feed.Id, feed.Tag);
                    _workers[feed.Id] = worker;
                }
                else
                    worker.Tag = feed.Tag;

                // Idle preview only — never hold the camera while FFmpeg is LIVE or starting.
                if (!worker.IsLive && !_startingFeeds.Contains(feed.Id))
                {
                    var idx = CameraEnumerator.ResolveIndex(feed.CameraName, _host.Config.Video.FfmpegPath);
                    worker.OpenPreview(idx);
                }
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TickPreview()
    {
        var lostLive = false;
        lock (_gate)
        {
            foreach (var w in _workers.Values)
            {
                if (w.IsLive && w.ProcessExited)
                {
                    w.MarkDead("Stream process exited.");
                    lostLive = true;
                    continue;
                }

                if (!w.IsLive)
                    w.GrabPreviewFrame();
            }
        }

        if (lostLive)
            _ = PushAnnounceAsync();

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TickPing()
    {
        if (!_host.Config.Video.AudioPingWhileLive) return;
        if (LiveCount == 0) return;
        if (DateTimeOffset.UtcNow - _lastPing < TimeSpan.FromMinutes(2)) return;
        _lastPing = DateTimeOffset.UtcNow;
        VideoAudioCues.PlayPing(_host.Config.Video.AudioCuesEnabled);
    }

    private void TickGpsSample()
    {
        var interval = Math.Clamp(_host.Config.Video.RecordingGpsSampleSeconds, 1, 60);
        if (DateTimeOffset.UtcNow - _lastGpsSample < TimeSpan.FromSeconds(interval)) return;
        _lastGpsSample = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            foreach (var w in _workers.Values.Where(x => x.IsLive && x.Recording))
            {
                var fix = _host.Gps.CurrentFix;
                if (fix is null && _host.LastServiceStatus is { Latitude: not null, Longitude: not null } st)
                {
                    w.AddGpsSample(new GpsSample(
                        DateTimeOffset.UtcNow, st.Latitude!.Value, st.Longitude!.Value, st.AltitudeMeters));
                }
                else if (fix is not null)
                {
                    w.AddGpsSample(new GpsSample(
                        DateTimeOffset.UtcNow, fix.Latitude, fix.Longitude, fix.AltitudeMeters));
                }
            }
        }
    }

    /// <summary>Returns (advertiseUrl for CoT, ffmpegUrl for process args).</summary>
    private (string Advertise, string Ffmpeg) BuildStreamUrls(VideoSettings cfg, VideoFeedSettings feed)
    {
        var tag = CotVideoBuilder.Sanitize(feed.Tag);
        var path = $"/live-{tag}";
        if (cfg.Transport.Equals("Push", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cfg.PushUrl))
        {
            var u = InjectPushCredentials(AppendPath(cfg.PushUrl!, tag), cfg);
            return (u, u);
        }

        if (cfg.Transport.Equals("UdpMulticast", StringComparison.OrdinalIgnoreCase))
        {
            var u = $"udp://{cfg.MulticastAddress}:{cfg.MulticastPort}";
            return (u, u);
        }

        // Listen on all interfaces; advertise the preferred LAN IP in CoT.
        var lan = GetLanIpv4(cfg.NetworkInterface);
        var advertise = $"rtsp://{lan}:{cfg.RtspListenPort}{path}";
        var listen = $"rtsp://0.0.0.0:{cfg.RtspListenPort}{path}";
        return (advertise, listen);
    }

    private string InjectPushCredentials(string url, VideoSettings cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.PushUsername)) return url;
        try
        {
            var uri = new Uri(url);
            if (!string.IsNullOrEmpty(uri.UserInfo)) return url;
            var pwd = "";
            if (!string.IsNullOrWhiteSpace(cfg.PushPasswordBlobName))
                pwd = _host.ConfigStore.ReadSecret(cfg.PushPasswordBlobName!) ?? "";
            var userInfo = Uri.EscapeDataString(cfg.PushUsername!) + ":" + Uri.EscapeDataString(pwd);
            return $"{uri.Scheme}://{userInfo}@{uri.Host}" +
                   (uri.IsDefaultPort ? "" : $":{uri.Port}") +
                   uri.PathAndQuery;
        }
        catch
        {
            return url;
        }
    }

    private static string AppendPath(string baseUrl, string tag)
    {
        if (baseUrl.Contains(tag, StringComparison.OrdinalIgnoreCase)) return baseUrl;
        return baseUrl.TrimEnd('/') + "-" + tag;
    }

    private string BuildFfmpegArgs(
        VideoSettings cfg,
        VideoFeedSettings feed,
        string dshowVideoName,
        string streamUrl,
        FeedWorker worker,
        bool streamAudio)
    {
        var gop = Math.Max(1, cfg.Fps * Math.Max(1, cfg.KeyframeSeconds));
        var videoEsc = EscapeDshow(dshowVideoName);
        string input;
        string enc;
        // Avoid -video_size / -framerate on dshow input (common I/O error sources); scale + -r on encode.
        var scale = $"-vf scale={cfg.Width}:{cfg.Height} -r {cfg.Fps}";
        if (streamAudio)
        {
            var audio = CameraEnumerator.ResolveDshowAudioName(cfg.FfmpegPath);
            input = audio is null
                ? $"-f dshow -rtbufsize 100M -i video=\"{videoEsc}\""
                : $"-f dshow -rtbufsize 100M -i video=\"{videoEsc}\":audio=\"{EscapeDshow(audio)}\"";
            enc = audio is null
                ? $"{scale} -c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p -an"
                : $"{scale} -c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p " +
                  "-c:a aac -b:a 128k -ac 1";
        }
        else
        {
            input = $"-f dshow -rtbufsize 100M -i video=\"{videoEsc}\"";
            enc =
                $"{scale} -c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p -an";
        }

        string netOut;
        if (cfg.Transport.Equals("UdpMulticast", StringComparison.OrdinalIgnoreCase))
            netOut = $"-f mpegts \"{streamUrl}\"";
        else if (cfg.Transport.Equals("Push", StringComparison.OrdinalIgnoreCase) &&
                 streamUrl.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase))
            netOut = $"-f flv \"{streamUrl}\"";
        else if (cfg.Transport.Equals("OnDeviceRtsp", StringComparison.OrdinalIgnoreCase))
            netOut = $"-f rtsp -rtsp_flags listen \"{streamUrl}\"";
        else
            netOut = $"-f rtsp -rtsp_transport tcp \"{streamUrl}\"";

        if (!cfg.RecordingEnabled)
            return $"{input} {enc} {netOut}";

        var folder = ResolveRecordingFolder(cfg);
        Directory.CreateDirectory(folder);
        var identity = _host.Core.GetActiveIdentity();
        // Stable base for this start attempt; BeginRecording runs only after FFmpeg is healthy.
        var baseName = worker.RecordingBaseName ?? VideoRecordingHelper.BuildSegmentBaseName(
            DateTimeOffset.UtcNow, DateTimeOffset.Now,
            Environment.MachineName, identity.Callsign,
            Environment.UserName, feed.Tag);
        worker.PrepareRecording(folder, baseName);
        var pattern = Path.Combine(folder, baseName + "_%03d.mp4").Replace('\\', '/');
        var segSec = Math.Max(1, cfg.RecordingSegmentMinutes) * 60;

        // Tee: network + segmented MP4 from one encode.
        var tee =
            $"[f=mpegts]{streamUrl}|[f=segment:segment_time={segSec}:reset_timestamps=1]{pattern}";
        if (!cfg.Transport.Equals("UdpMulticast", StringComparison.OrdinalIgnoreCase))
        {
            // Dual encode: network mux + segmented MP4 (tee is awkward for RTSP listen).
            return $"{input} {enc} {netOut} " +
                   $"-c:v libx264 -preset veryfast -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p -an " +
                   $"-f segment -segment_time {segSec} -reset_timestamps 1 \"{pattern}\"";
        }

        return $"{input} {enc} -f tee \"{tee}\"";
    }

    private static string EscapeDshow(string s) => s.Replace("\"", "");

    private static string ResolveRecordingFolder(VideoSettings cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.RecordingFolder))
            return cfg.RecordingFolder!;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "WinTAKTracker");
    }

    private static string GetLanIpv4(string? preferredNic) =>
        MeshSaBroadcaster.TryResolveAdvertiseIpv4(preferredNic) ?? "127.0.0.1";

    public void Dispose()
    {
        _previewTimer.Stop();
        _pingTimer.Stop();
        _gpsSampleTimer.Stop();
        StopAllAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            foreach (var w in _workers.Values)
                w.Dispose();
            _workers.Clear();
        }
    }

    private sealed class FeedWorker : IDisposable
    {
        public string FeedId { get; }
        public string Tag { get; set; }
        public bool IsLive { get; private set; }
        public bool IsPlayable =>
            IsLive &&
            _process is { HasExited: false } &&
            !string.IsNullOrWhiteSpace(StreamUrl) &&
            !IsHardFfmpegError(LastError);
        public bool ProcessExited => IsLive && (_process is null || _process.HasExited);
        public string? StreamUrl { get; private set; }
        public string? LastError { get; set; }
        public BitmapSource? LastPreview { get; private set; }
        public bool Recording => _recording;
        public string? RecordingBaseName => _recordingBase;
        private Process? _process;
        private VideoCapture? _preview;
        private bool _recording;
        private string? _recordingFolder;
        private string? _recordingBase;
        private readonly List<GpsSample> _samples = [];
        private readonly List<string> _closedSegments = [];

        public FeedWorker(string feedId, string tag)
        {
            FeedId = feedId;
            Tag = tag;
        }

        public async Task<bool> StartProcessAsync(string ffmpeg, string args, string url)
        {
            StopProcess();
            StreamUrl = url;
            LastError = null;
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            _process = Process.Start(psi);
            if (_process is null)
            {
                LastError = "Failed to start FFmpeg.";
                IsLive = false;
                return false;
            }

            var errBuf = new System.Text.StringBuilder();
            _ = Task.Run(() =>
            {
                try
                {
                    while (_process is { HasExited: false })
                    {
                        var line = _process.StandardError.ReadLine();
                        if (line is null) break;
                        errBuf.AppendLine(line);
                        if (IsHardFfmpegError(line))
                            LastError = line.Trim();
                    }
                }
                catch { /* ignore */ }
            });

            // Wait for dshow open / RTSP listen; hard errors usually appear quickly.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(150).ConfigureAwait(false);
                if (_process.HasExited) break;
                if (IsHardFfmpegError(LastError)) break;
            }

            if (_process.HasExited || IsHardFfmpegError(LastError))
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(LastError))
                {
                    var line = errBuf.ToString().Split('\n')
                        .LastOrDefault(l => IsHardFfmpegError(l))?.Trim();
                    LastError = line ?? "FFmpeg exited. Check camera name under Settings → Video (use FFmpeg device list).";
                }

                StopProcess();
                IsLive = false;
                StreamUrl = null;
                return false;
            }

            IsLive = true;
            return true;
        }

        private static bool IsHardFfmpegError(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            return line.Contains("Error opening input", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Could not find video device", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Could not open video device", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("No such device", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Immediate exit requested", StringComparison.OrdinalIgnoreCase);
        }

        public void ClosePreview()
        {
            try
            {
                _preview?.Release();
                _preview?.Dispose();
            }
            catch { /* ignore */ }
            finally
            {
                _preview = null;
            }
        }

        public void OpenPreview(int index)
        {
            if (IsLive) return; // camera owned by FFmpeg
            try
            {
                ClosePreview();
                _preview = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
            }
            catch (Exception ex)
            {
                LastError = "Preview: " + ex.Message;
            }
        }

        public void GrabPreviewFrame()
        {
            if (_preview is null || !_preview.IsOpened()) return;
            try
            {
                using var frame = new Mat();
                if (!_preview.Read(frame) || frame.Empty()) return;
                LastPreview = frame.ToBitmapSource();
                LastPreview?.Freeze();
            }
            catch { /* ignore */ }
        }

        public void PrepareRecording(string folder, string baseName)
        {
            _recordingFolder = folder;
            _recordingBase = baseName;
        }

        public void CancelPreparedRecording()
        {
            if (_recording) return;
            _recordingFolder = null;
            _recordingBase = null;
        }

        public void BeginPreparedRecording(TimeSpan segment)
        {
            if (string.IsNullOrWhiteSpace(_recordingFolder) || string.IsNullOrWhiteSpace(_recordingBase))
                return;
            _recording = true;
            _samples.Clear();
            _closedSegments.Clear();
            _ = Task.Run(async () =>
            {
                while (_recording)
                {
                    await Task.Delay(segment + TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    FinalizeNewSegments();
                }
            });
        }

        public void AddGpsSample(GpsSample sample)
        {
            if (!_recording) return;
            _samples.Add(sample);
        }

        private void FinalizeNewSegments()
        {
            if (_recordingFolder is null || _recordingBase is null) return;
            foreach (var file in Directory.GetFiles(_recordingFolder, _recordingBase + "_*.mp4"))
            {
                if (_closedSegments.Contains(file)) continue;
                try
                {
                    using var fs = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < 1024) continue;
                }
                catch { continue; }

                // Prefer closed files: if still growing skip once.
                var len1 = new FileInfo(file).Length;
                Thread.Sleep(400);
                var len2 = new FileInfo(file).Length;
                if (len2 != len1) continue;

                try
                {
                    VideoRecordingHelper.WriteSha256(file);
                    var kml = Path.ChangeExtension(file, ".kml");
                    VideoRecordingHelper.WriteKmlTrack(kml, Path.GetFileNameWithoutExtension(file), _samples.ToList());
                    _closedSegments.Add(file);
                    _samples.Clear();
                }
                catch { /* ignore */ }
            }
        }

        public async Task StopAsync()
        {
            _recording = false;
            FinalizeNewSegments();
            StopProcess();
            IsLive = false;
            StreamUrl = null;
            await Task.CompletedTask;
        }

        public void MarkDead(string reason)
        {
            _recording = false;
            FinalizeNewSegments();
            StopProcess();
            IsLive = false;
            StreamUrl = null;
            LastError = reason;
        }

        private void StopProcess()
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch { /* ignore */ }
            finally
            {
                _process?.Dispose();
                _process = null;
            }
        }

        public void Dispose()
        {
            _recording = false;
            StopProcess();
            _preview?.Release();
            _preview?.Dispose();
        }
    }
}
