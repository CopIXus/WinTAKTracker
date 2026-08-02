using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Ipc;
using WinTAKTracker.Services.Reporting;

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

        worker.StartProcess(ffmpeg, args, url);
        worker.OpenPreview(camIndex);
        VideoAudioCues.PlayStart(cfg.AudioCuesEnabled);
        await PushAnnounceAsync().ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopFeedAsync(string feedId)
    {
        FeedWorker? worker;
        lock (_gate) _workers.TryGetValue(feedId, out worker);
        if (worker is null) return;
        await worker.StopAsync().ConfigureAwait(false);
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
                if (!_workers.ContainsKey(feed.Id))
                    _workers[feed.Id] = new FeedWorker(feed.Id, feed.Tag);
                else
                    _workers[feed.Id].Tag = feed.Tag;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TickPreview()
    {
        lock (_gate)
        {
            foreach (var w in _workers.Values)
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
            "UdpMulticast" => $"udp://{cfg.MulticastAddress}:{cfg.MulticastPort}",
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
        if (cfg.StreamAudio)
        {
            var audio = CameraEnumerator.ResolveDshowAudioName(cfg.FfmpegPath);
            input = audio is null
                ? $"-f dshow -rtbufsize 100M -framerate {cfg.Fps} -video_size {cfg.Width}x{cfg.Height} " +
                  $"-i video=\"{videoEsc}\""
                : $"-f dshow -rtbufsize 100M -framerate {cfg.Fps} -video_size {cfg.Width}x{cfg.Height} " +
                  $"-i video=\"{videoEsc}\":audio=\"{EscapeDshow(audio)}\"";
            enc = audio is null
                ? $"-c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p -an"
                : $"-c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p " +
                  "-c:a aac -b:a 128k -ac 1";
        }
        else
        {
            input =
                $"-f dshow -rtbufsize 100M -framerate {cfg.Fps} -video_size {cfg.Width}x{cfg.Height} " +
                $"-i video=\"{videoEsc}\"";
            enc =
                $"-c:v libx264 -preset veryfast -tune zerolatency -b:v {cfg.BitrateKbps}k -g {gop} -pix_fmt yuv420p -an";
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

    private static string GetLanIpv4(string? preferredNic)
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .ToList();

            if (!string.IsNullOrWhiteSpace(preferredNic) &&
                !preferredNic.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                var match = nics.FirstOrDefault(n =>
                    string.Equals(n.Name, preferredNic, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n.Description, preferredNic, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    var ip = PrimaryIpv4(match);
                    if (ip is not null) return ip;
                }
            }

            foreach (var ni in nics)
            {
                var ip = PrimaryIpv4(ni);
                if (ip is not null) return ip;
            }
        }
        catch { /* ignore */ }

        return "127.0.0.1";
    }

    private static string? PrimaryIpv4(NetworkInterface ni)
    {
        foreach (var addr in ni.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return addr.Address.ToString();
        }

        return null;
    }

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

        public void StartProcess(string ffmpeg, string args, string url)
        {
            StopProcess();
            StreamUrl = url;
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
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var err = _process.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(err) && err.Contains("error", StringComparison.OrdinalIgnoreCase))
                        LastError = err.Split('\n').LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))?.Trim();
                }
                catch { /* ignore */ }
            });

            IsLive = true;
            LastError = null;
        }

        public void OpenPreview(int index)
        {
            try
            {
                _preview?.Release();
                _preview?.Dispose();
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
