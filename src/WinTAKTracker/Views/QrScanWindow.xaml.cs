using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using ZXing;
using ZXing.Common;

namespace WinTAKTracker.Views;

public partial class QrScanWindow : System.Windows.Window
{
    private readonly DispatcherTimer _timer;
    private VideoCapture? _capture;
    private readonly BarcodeReaderGeneric _reader;
    private bool _closing;

    public string? ScannedText { get; private set; }

    public QrScanWindow()
    {
        InitializeComponent();
        _reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.QR_CODE],
                TryHarder = true,
            },
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += Timer_OnTick;
        Loaded += (_, _) => PopulateCameras();
        Closed += (_, _) => Cleanup();
    }

    private void PopulateCameras()
    {
        CameraCombo.Items.Clear();
        for (var i = 0; i < 5; i++)
        {
            using var test = new VideoCapture(i);
            if (!test.IsOpened()) continue;
            CameraCombo.Items.Add($"Camera {i}");
        }

        if (CameraCombo.Items.Count == 0)
        {
            StatusText.Text = "No camera found. Use paste instead.";
            return;
        }

        CameraCombo.SelectedIndex = 0;
    }

    private void CameraCombo_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CameraCombo.SelectedIndex < 0) return;
        StartCamera(CameraCombo.SelectedIndex);
    }

    private void StartCamera(int index)
    {
        CleanupCapture();
        try
        {
            _capture = new VideoCapture(index);
            if (!_capture.IsOpened())
            {
                StatusText.Text = "Could not open camera.";
                return;
            }

            StatusText.Text = "Scanning…";
            _timer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Camera error: {ex.GetType().Name}";
        }
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        if (_capture is null || !_capture.IsOpened() || _closing) return;
        using var frame = new Mat();
        if (!_capture.Read(frame) || frame.Empty()) return;

        PreviewImage.Source = frame.ToBitmapSource();

        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        var bytes = new byte[gray.Rows * gray.Cols];
        System.Runtime.InteropServices.Marshal.Copy(gray.Data, bytes, 0, bytes.Length);
        var luminance = new RGBLuminanceSource(bytes, gray.Cols, gray.Rows, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = _reader.Decode(luminance);
        if (result?.Text is { Length: > 0 } text)
        {
            ScannedText = text;
            StatusText.Text = "QR detected.";
            DialogResult = true;
            Close();
        }
    }

    private void Paste_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                ScannedText = Clipboard.GetText();
                DialogResult = true;
                Close();
                return;
            }
        }
        catch { /* ignore */ }

        StatusText.Text = "Clipboard has no text. Copy an enroll URL first.";
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CleanupCapture()
    {
        _timer.Stop();
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
    }

    private void Cleanup()
    {
        _closing = true;
        CleanupCapture();
    }
}
