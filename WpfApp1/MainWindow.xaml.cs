using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenCvSharp;
using System.Runtime.InteropServices;
using System.Windows.Interop;


namespace WpfAsciiPlayer
{
    public partial class MainWindow : System.Windows.Window
    {

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void EnableDarkTitleBar()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useImmersiveDarkMode = 1;
            // 20 — атрибут для Windows 11 и свежих сборок Windows 10; 19 — для старых сборок Windows 10
            if (DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
            }
        }


        private const string AsciiChars = " .:-=+*#%@";

        // Максимальный размер области на экране в пикселях (чтобы 4K видео не улетало за края монитора)
        private const double MaxDisplayWidth = 1000.0;
        private const double MaxDisplayHeight = 760.0;

        private const double CellW = 8.0;
        private const double CellH = 12.0;
        private const double FontSize = 12.0;

        private const double SaturationBoost = 1.6;
        private const double BrightnessBoost = 1.25;

        // Динамические размеры сетки символов под конкретное видео
        private int _cols = 120;
        private int _rows = 60;

        private VideoCapture _capture;
        private readonly MediaPlayer _mediaPlayer = new MediaPlayer();
        private DispatcherTimer _timer;
        private double _fps = 30.0;
        private int _lastRenderedFrame = -1;

        private readonly Mat _frame = new Mat();
        private readonly Mat _smallFrame = new Mat();
        private readonly Mat _hsvFrame = new Mat();
        private readonly Mat _grayFrame = new Mat();

        private readonly DrawingGroup _drawingGroup = new DrawingGroup();
        private GlyphTypeface _glyphTypeface;
        private readonly ushort[] _glyphIndices = new ushort[AsciiChars.Length];
        private readonly Dictionary<int, SolidColorBrush> _brushCache = new Dictionary<int, SolidColorBrush>();
        private readonly float _pixelsPerDip;

        public MainWindow()
        {
            InitializeComponent();

            _pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
            AsciiImage.Source = new DrawingImage(_drawingGroup);

            InitGlyphs();

            ContentRendered += MainWindow_ContentRendered;
            Closed += MainWindow_Closed;
        }

        private void InitGlyphs()
        {
            var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out _glyphTypeface))
            {
                for (int i = 0; i < AsciiChars.Length; i++)
                {
                    _glyphIndices[i] = _glyphTypeface.CharacterToGlyphMap[AsciiChars[i]];
                }
            }
        }

        private void MainWindow_ContentRendered(object sender, EventArgs e)
        {
            EnableDarkTitleBar();

            var ofd = new OpenFileDialog
            {
                Title = "Выберите видеофайл",
                Filter = "Видеофайлы|*.mp4;*.avi;*.mov;*.mkv;*.webm|Все файлы|*.*"
            };

            if (ofd.ShowDialog() != true)
            {
                Close();
                return;
            }

            _capture = new VideoCapture(ofd.FileName);
            if (!_capture.IsOpened())
            {
                MessageBox.Show("Не удалось открыть видеофайл.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            // Рассчитываем пропорции окна и сетки символов под реальный размер видео
            AdjustSizeToVideo(_capture.FrameWidth, _capture.FrameHeight);

            _fps = _capture.Fps > 0 ? _capture.Fps : 60.0;

            _mediaPlayer.Open(new Uri(ofd.FileName));
            _mediaPlayer.Volume = 1.0;

            _mediaPlayer.MediaEnded += (s, args) =>
            {
                _mediaPlayer.Position = TimeSpan.Zero;
                _capture.Set(VideoCaptureProperties.PosFrames, 0);
                _lastRenderedFrame = -1;
                _mediaPlayer.Play();
            };

            _mediaPlayer.Play();

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void AdjustSizeToVideo(int videoWidth, int videoHeight)
        {
            if (videoWidth <= 0 || videoHeight <= 0) return;

            // Берём 85% от реальной рабочей области вашего монитора
            double maxW = SystemParameters.WorkArea.Width * 0.85;
            double maxH = SystemParameters.WorkArea.Height * 0.85;

            // Вписываем пропорции видео в доступную площадь экрана
            double scale = Math.Min(maxW / videoWidth, maxH / videoHeight);
            double windowWidth = videoWidth * scale;
            double windowHeight = videoHeight * scale;

            // Задаём размер самого окна (с запасом под верхнюю рамку заголовка)
            Width = windowWidth + 16;
            Height = windowHeight + 39;

            // Считаем плотность сетки символов (ограничиваем сверху 180x100, чтобы сохранить плавные 60 FPS)
            _cols = Math.Max(40, Math.Min(180, (int)Math.Round(windowWidth / CellW)));
            _rows = Math.Max(20, Math.Min(100, (int)Math.Round(windowHeight / CellH)));

            // Центрируем окно на экране
            Left = (SystemParameters.WorkArea.Width - Width) / 2 + SystemParameters.WorkArea.Left;
            Top = (SystemParameters.WorkArea.Height - Height) / 2 + SystemParameters.WorkArea.Top;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_capture == null || !_capture.IsOpened()) return;

            int targetFrame = (int)(_mediaPlayer.Position.TotalSeconds * _fps);
            if (targetFrame <= _lastRenderedFrame) return;

            if (targetFrame - _lastRenderedFrame > 1)
            {
                _capture.Set(VideoCaptureProperties.PosFrames, targetFrame);
            }

            if (!_capture.Read(_frame) || _frame.Empty()) return;

            _lastRenderedFrame = targetFrame;

            Cv2.Resize(_frame, _smallFrame, new OpenCvSharp.Size(_cols, _rows), 0, 0, InterpolationFlags.Area);
            Cv2.CvtColor(_smallFrame, _grayFrame, ColorConversionCodes.BGR2GRAY);

            Cv2.CvtColor(_smallFrame, _hsvFrame, ColorConversionCodes.BGR2HSV);
            for (int r = 0; r < _rows; r++)
            {
                for (int c = 0; c < _cols; c++)
                {
                    Vec3b hsv = _hsvFrame.At<Vec3b>(r, c);
                    int s = Math.Min(255, (int)(hsv.Item1 * SaturationBoost));
                    int v = Math.Min(255, (int)(hsv.Item2 * BrightnessBoost));
                    _hsvFrame.Set(r, c, new Vec3b(hsv.Item0, (byte)s, (byte)v));
                }
            }
            Cv2.CvtColor(_hsvFrame, _smallFrame, ColorConversionCodes.HSV2BGR);

            RenderAsciiFrame();
        }

        private SolidColorBrush GetCachedBrush(byte r, byte g, byte b)
        {
            r = (byte)(r & 0xF8);
            g = (byte)(g & 0xF8);
            b = (byte)(b & 0xF8);

            int key = (r << 16) | (g << 8) | b;
            if (!_brushCache.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                _brushCache[key] = brush;
            }
            return brush;
        }

        private void RenderAsciiFrame()
        {
            if (_glyphTypeface == null) return;

            using (DrawingContext dc = _drawingGroup.Open())
            {
                dc.DrawRectangle(Brushes.Black, null, new System.Windows.Rect(0, 0, _cols * CellW, _rows * CellH));

                var advanceWidths = new double[] { CellW };

                for (int r = 0; r < _rows; r++)
                {
                    double y = (r + 0.85) * CellH;

                    for (int c = 0; c < _cols; c++)
                    {
                        Vec3b bgr = _smallFrame.At<Vec3b>(r, c);
                        if (bgr.Item0 < 20 && bgr.Item1 < 20 && bgr.Item2 < 20)
                            continue;

                        byte gray = _grayFrame.At<byte>(r, c);
                        int charIndex = (gray * (AsciiChars.Length - 1)) / 255;
                        if (charIndex == 0) continue;

                        var brush = GetCachedBrush(bgr.Item2, bgr.Item1, bgr.Item0);

                        var glyphRun = new GlyphRun(
                            _glyphTypeface,
                            0,
                            false,
                            FontSize,
                            _pixelsPerDip,
                            new ushort[] { _glyphIndices[charIndex] },
                            new System.Windows.Point(c * CellW, y),
                            advanceWidths,
                            null, null, null, null, null, null);

                        dc.DrawGlyphRun(brush, glyphRun);
                    }
                }
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _timer?.Stop();
            _mediaPlayer.Stop();
            _mediaPlayer.Close();
            _capture?.Release();
            _frame.Dispose();
            _smallFrame.Dispose();
            _hsvFrame.Dispose();
            _grayFrame.Dispose();
        }
    }
}
