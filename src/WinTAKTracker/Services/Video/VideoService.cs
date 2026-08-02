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
        }

        var url = BuildStreamUrl(cfg, feed);
        var camIndex = CameraEnumerator.ResolveIndex(feed.CameraName, cfg.FfmpegPath);
        var dshowName = CameraEnumerator.ResolveDshowVideoName(feed.CameraName, cfg.FfmpegPath);
        var args = BuildFfmpegArgs(cfg, feed, dshowName, url, worker);
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

        // DirectShow cameras are usually exclusive — release OpenCv preview before FFmpeg grabs the device.
        worker.ClosePreview();
        var started = await worker.StartProcessAsync(ffmpeg, args, url).ConfigureAwait(false);
        if (!started)
        {
            worker.OpenPreview(camIndex);
            StateChanged?.Invoke(this, EventArgs.Empty);
            throw new InvalidOperationException(worker.LastError ?? "FFmpeg failed to open the camera.");
        }

        VideoAudioCues.PlayStart(cfg.AudioCuesEnabled);
        await PushAnnounceAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
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
            feeds = _workers.Values.Where(w => w.IsLive && !string.IsNullOrWhiteSpace(w.StreamUrl))
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
            SendFovSensorMarker = cfg.Video.SendFovSensorMarker,
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

                // Idle preview only — never hold the camera while FFmpeg is LIVE.
                if (!worker.IsLive)
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
        lock (_gate)
        {
            foreach (var w in _workers.Values.Where(x => !x.IsLive))
                w.GrabPreviewFrame();
        }

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

    private string BuildStreamUrl(VideoSettings cfg, VideoFeedSettings feed)
    {
        var tag = CotVideoBuilder.Sanitize(feed.Tag);
        var path = $"/live-{tag}";
        return cfg.Transport switch
        {
            "Push" when !string.IsNullOrWhiteSpace(cfg.PushUrl) =>
                InjectPushCredentials(AppendPath(cfg.PushUrl!, tag), cfg),
            // Multicast CoT URL is the media group, not the PC's unicast IP.
            "UdpMulticast" => $"udp://{cfg.MulticastAddress}:{cfg.MulticastPort}",
            // On-device RTSP: advertise this PC's LAN IP so ATAK can pull the stream.
            _ => $"rtsp://{GetLanIpv4(cfg.NetworkInterface)}:{cfg.RtspListenPort}{path}",
        };
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
        FeedWorker worker)
    {
        var gop = Math.Max(1, cfg.Fps * Math.Max(1, cfg.KeyframeSeconds));
        var videoEsc = EscapeDshow(dshowVideoName);
        string input;
        string enc;
        // Do not force -video_size on dshow (many webcams return I/O error); scale after capture.
        var scale = $"-vf scale={cfg.Width}:{cfg.Height}";
        if (cfg.StreamAudio)
        {
            var audio = CameraEnumerator.ResolveDshowAudioName(cfg.FfmpegPath);
            input = audio is null
                ? $"-f dshow -rtbufsize 100M -framerate {cfg.Fps} -i video=\"{videoEsc}\""
                : $"-f dshow -rtbufsize 100M -framerate {cfg.Fps} -i video=\"{videoEsc}\":audio=\"{EscapeDshow(audio)}\"";
            enc = audio is null
                ? $"{scale} -c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p -an"
                : $"{scale} -c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p " +
                  "-c:a aac -b:a 128k -ac 1";
        }
        else
        {
            input = $"-f dshow -rtbufsize 100M -framerate {cfg.Fps} -i video=\"{videoEsc}\"";
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
        var baseName = VideoRecordingHelper.BuildSegmentBaseName(
            DateTimeOffset.UtcNow, DateTimeOffset.Now,
            Environment.MachineName, identity.Callsign,
            Environment.UserName, feed.Tag);
        var pattern = Path.Combine(folder, baseName + "_%03d.mp4").Replace('\\', '/');
        var segSec = Math.Max(1, cfg.RecordingSegmentMinutes) * 60;
        worker.BeginRecording(folder, baseName, TimeSpan.FromMinutes(Math.Max(1, cfg.RecordingSegmentMinutes)));

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
        public string? StreamUrl { get; private set; }
        public string? LastError { get; set; }
        public BitmapSource? LastPreview { get; private set; }
        public bool Recording => _recording;
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
            var errTask = Task.Run(() =>
            {
                try
                {
                    while (_process is { HasExited: false } || errBuf.Length == 0)
                    {
                        var line = _process?.StandardError.ReadLine();
                        if (line is null) break;
                        errBuf.AppendLine(line);
                        if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                            LastError = line.Trim();
                    }
                }
                catch { /* ignore */ }
            });

            // Give FFmpeg a moment to open dshow / bind RTSP before declaring LIVE.
            await Task.Delay(1200).ConfigureAwait(false);
            if (_process.HasExited)
            {
                try { await errTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
                LastError ??= "FFmpeg exited immediately. Check camera name / FFmpeg path.";
                if (errBuf.Length > 0 && LastError.Contains("exited", StringComparison.OrdinalIgnoreCase))
                {
                    var line = errBuf.ToString().Split('\n')
                        .LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim();
                    if (!string.IsNullOrWhiteSpace(line)) LastError = line;
                }

                IsLive = false;
                StreamUrl = null;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(LastError) &&
                LastError.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                StopProcess();
                IsLive = false;
                StreamUrl = null;
                return false;
            }

            IsLive = true;
            return true;
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

        public void BeginRecording(string folder, string baseName, TimeSpan segment)
        {
            _recording = true;
            _recordingFolder = folder;
            _recordingBase = baseName;
            _samples.Clear();
            _closedSegments.Clear();
            _ = Task.Run(async () =>
            {
                // Poll for completed segment files and hash/kml them.
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
