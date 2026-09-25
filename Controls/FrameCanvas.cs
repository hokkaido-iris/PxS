using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IrisPxS.Models;

namespace IrisPxS.Controls
{
    public class FrameCanvas : Canvas
    {
        public static readonly DependencyProperty ImageSourceProperty =
            DependencyProperty.Register(nameof(ImageSource), typeof(BitmapSource), typeof(FrameCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnImageSourceChanged));

        public BitmapSource? ImageSource
        {
            get => (BitmapSource?)GetValue(ImageSourceProperty);
            set => SetValue(ImageSourceProperty, value);
        }

        public static readonly DependencyProperty LogicalImageWidthProperty =
            DependencyProperty.Register(nameof(LogicalImageWidth), typeof(int), typeof(FrameCanvas),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => (d as FrameCanvas)?.ResetView()));

        public int LogicalImageWidth
        {
            get => (int)GetValue(LogicalImageWidthProperty);
            set => SetValue(LogicalImageWidthProperty, value);
        }

        public static readonly DependencyProperty LogicalImageHeightProperty =
            DependencyProperty.Register(nameof(LogicalImageHeight), typeof(int), typeof(FrameCanvas),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => (d as FrameCanvas)?.ResetView()));

        public int LogicalImageHeight
        {
            get => (int)GetValue(LogicalImageHeightProperty);
            set => SetValue(LogicalImageHeightProperty, value);
        }

        public static readonly DependencyProperty FramesProperty =
            DependencyProperty.Register(nameof(Frames), typeof(ObservableCollection<FilmFrame>), typeof(FrameCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnFramesChanged));

        private static void OnFramesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameCanvas canvas)
            {
                if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged oldColl)
                {
                    oldColl.CollectionChanged -= canvas.Frames_CollectionChanged;
                }
                if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged newColl)
                {
                    newColl.CollectionChanged += canvas.Frames_CollectionChanged;
                }
                canvas.InvalidateVisual();
            }
        }

        private void Frames_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            InvalidateVisual();
        }

        public ObservableCollection<FilmFrame>? Frames
        {
            get => (ObservableCollection<FilmFrame>?)GetValue(FramesProperty);
            set => SetValue(FramesProperty, value);
        }

        public static readonly DependencyProperty SelectedFrameProperty =
            DependencyProperty.Register(nameof(SelectedFrame), typeof(FilmFrame), typeof(FrameCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public FilmFrame? SelectedFrame
        {
            get => (FilmFrame?)GetValue(SelectedFrameProperty);
            set => SetValue(SelectedFrameProperty, value);
        }

        public static readonly DependencyProperty IsDragEnabledProperty =
            DependencyProperty.Register(nameof(IsDragEnabled), typeof(bool), typeof(FrameCanvas),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsDragEnabled
        {
            get => (bool)GetValue(IsDragEnabledProperty);
            set => SetValue(IsDragEnabledProperty, value);
        }

        public bool IsEyedropperMode { get; set; } = false;

        public event EventHandler<(byte R, byte G, byte B)>? ColorPicked;
        public event EventHandler<FilmFrame>? FrameSelected;
        public event EventHandler? FrameModified;
        public event EventHandler<double>? ZoomChanged;

        public double ZoomScale => _scale;

        private double _scale = 1.0;
        private Point _panOffset = new Point(0, 0);
        private Point _lastMousePos;
        private bool _isPanning = false;

        // ドラッグ移動・リサイズ管理
        private FilmFrame? _draggedFrame = null;
        private int _resizeHandle = -1; // -1: なし, 0: 移動, 1~8: 各ハンドル (TL, T, TR, R, BR, B, BL, L)
        private Point _dragStartPos;
        private OpenCvSharp.Rect _initialRect;

        public FrameCanvas()
        {
            ClipToBounds = true;
            Focusable = true;
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 24));
        }

        private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameCanvas canvas)
            {
                canvas.ResetView();
            }
        }

        public void ResetView()
        {
            if (ImageSource == null || ActualWidth <= 0 || ActualHeight <= 0) return;

            int logicalW = LogicalImageWidth > 0 ? LogicalImageWidth : ImageSource.PixelWidth;
            int logicalH = LogicalImageHeight > 0 ? LogicalImageHeight : ImageSource.PixelHeight;
            if (logicalW <= 0 || logicalH <= 0) return;

            double scaleX = ActualWidth / logicalW;
            double scaleY = ActualHeight / logicalH;
            _scale = Math.Min(scaleX, scaleY) * 0.95;

            double renderedW = logicalW * _scale;
            double renderedH = logicalH * _scale;
            _panOffset = new Point((ActualWidth - renderedW) / 2, (ActualHeight - renderedH) / 2);

            InvalidateVisual();
            ZoomChanged?.Invoke(this, _scale);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_scale == 1.0) ResetView();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            // 背景描画
            dc.DrawRectangle(Background, null, new System.Windows.Rect(0, 0, ActualWidth, ActualHeight));

            if (ImageSource == null)
            {
                var text = new FormattedText(
                    "スキャン画像またはプレビューがありません\n[PreScan] または [画像を開く] をクリックしてください",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    14,
                    new SolidColorBrush(Color.FromRgb(140, 140, 150)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
                return;
            }

            // 画像の描画 (論理サイズに合わせた拡大縮小・パン適用)
            int logicalW = LogicalImageWidth > 0 ? LogicalImageWidth : ImageSource.PixelWidth;
            int logicalH = LogicalImageHeight > 0 ? LogicalImageHeight : ImageSource.PixelHeight;
            var imgRect = new System.Windows.Rect(_panOffset.X, _panOffset.Y, logicalW * _scale, logicalH * _scale);
            dc.DrawImage(ImageSource, imgRect);

            // コマ枠オーバーレイの描画
            if (Frames != null)
            {
                int idx = 1;
                foreach (var frame in Frames)
                {
                    bool isSelected = (frame == SelectedFrame);
                    DrawFrameOverlay(dc, frame, isSelected);
                    idx++;
                }
            }
        }

        private void DrawFrameOverlay(DrawingContext dc, FilmFrame frame, bool isSelected)
        {
            var r = frame.CropRect;
            double screenX = _panOffset.X + r.X * _scale;
            double screenY = _panOffset.Y + r.Y * _scale;
            double screenW = r.Width * _scale;
            double screenH = r.Height * _scale;

            var screenRect = new System.Windows.Rect(screenX, screenY, screenW, screenH);

            var strokeColor = isSelected ? Color.FromRgb(0, 200, 255) : Color.FromRgb(255, 180, 0);
            var fillBrush = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)40 : (byte)15, strokeColor.R, strokeColor.G, strokeColor.B));
            var pen = new Pen(new SolidColorBrush(strokeColor), isSelected ? 2.5 : 1.5);

            dc.DrawRectangle(fillBrush, pen, screenRect);

            // 内側トリム（余白カット実領域）の破線ガイド描画
            if (frame.CropInsetPercent > 0.05)
            {
                var inR = frame.GetInsetCropRect();
                double inX = _panOffset.X + inR.X * _scale;
                double inY = _panOffset.Y + inR.Y * _scale;
                double inW = inR.Width * _scale;
                double inH = inR.Height * _scale;

                var insetPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1.0)
                {
                    DashStyle = DashStyles.Dash
                };
                dc.DrawRectangle(null, insetPen, new System.Windows.Rect(inX, inY, inW, inH));
            }

            // 番号バッジ描画
            var badgeBg = new SolidColorBrush(strokeColor);
            var badgeRect = new System.Windows.Rect(screenX + 4, screenY + 4, 32, 22);
            dc.DrawRoundedRectangle(badgeBg, null, badgeRect, 4, 4);

            var text = new FormattedText(
                $"#{frame.FrameNumber}",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                11,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(text, new Point(badgeRect.X + (badgeRect.Width - text.Width) / 2, badgeRect.Y + (badgeRect.Height - text.Height) / 2));

            // 選択時の8ハンドル描画 (マウスドラッグ有効時のみ表示)
            if (isSelected && IsDragEnabled)
            {
                DrawHandles(dc, screenRect);
            }
        }

        private void DrawHandles(DrawingContext dc, System.Windows.Rect rect)
        {
            double hSize = 8;
            var handleBrush = Brushes.White;
            var handlePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 160, 240)), 1.5);

            Point[] points =
            {
                new Point(rect.Left, rect.Top),
                new Point(rect.Left + rect.Width / 2, rect.Top),
                new Point(rect.Right, rect.Top),
                new Point(rect.Right, rect.Top + rect.Height / 2),
                new Point(rect.Right, rect.Bottom),
                new Point(rect.Left + rect.Width / 2, rect.Bottom),
                new Point(rect.Left, rect.Bottom),
                new Point(rect.Left, rect.Top + rect.Height / 2)
            };

            foreach (var pt in points)
            {
                dc.DrawRectangle(handleBrush, handlePen, new System.Windows.Rect(pt.X - hSize / 2, pt.Y - hSize / 2, hSize, hSize));
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            CaptureMouse();
            var pos = e.GetPosition(this);

            if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Right && Keyboard.IsKeyDown(Key.Space)))
            {
                _isPanning = true;
                _lastMousePos = pos;
                Cursor = Cursors.SizeAll;
                return;
            }

            // スポイトモード時の処理
            if (IsEyedropperMode && ImageSource != null && e.ChangedButton == MouseButton.Left)
            {
                PickColorAtScreenPoint(pos);
                return;
            }

            if (e.ChangedButton == MouseButton.Left && Frames != null)
            {
                // まず選択枠のハンドル上かチェック (マウスドラッグ有効時のみ)
                if (IsDragEnabled && SelectedFrame != null)
                {
                    int handle = HitTestHandles(pos, SelectedFrame);
                    if (handle >= 0)
                    {
                        _resizeHandle = handle;
                        _draggedFrame = SelectedFrame;
                        _dragStartPos = pos;
                        _initialRect = SelectedFrame.CropRect;
                        return;
                    }
                }

                // コマ枠クリック判定 (最前面またはクリック位置のコマ)
                FilmFrame? hitFrame = null;
                for (int i = Frames.Count - 1; i >= 0; i--)
                {
                    if (IsPointInsideFrame(pos, Frames[i]))
                    {
                        hitFrame = Frames[i];
                        break;
                    }
                }

                if (hitFrame != null)
                {
                    SelectedFrame = hitFrame;
                    FrameSelected?.Invoke(this, hitFrame);
                    if (IsDragEnabled)
                    {
                        _resizeHandle = 0; // 移動モード
                        _draggedFrame = hitFrame;
                        _dragStartPos = pos;
                        _initialRect = hitFrame.CropRect;
                    }
                    else
                    {
                        _resizeHandle = -1;
                        _draggedFrame = null;
                    }
                    InvalidateVisual();
                    return;
                }
                else
                {
                    // 空白地クリック: パン開始
                    _isPanning = true;
                    _lastMousePos = pos;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pos = e.GetPosition(this);

            if (_isPanning)
            {
                _panOffset.X += (pos.X - _lastMousePos.X);
                _panOffset.Y += (pos.Y - _lastMousePos.Y);
                _lastMousePos = pos;
                InvalidateVisual();
                return;
            }

            if (IsDragEnabled && _draggedFrame != null && e.LeftButton == MouseButtonState.Pressed)
            {
                double dx = (pos.X - _dragStartPos.X) / _scale;
                double dy = (pos.Y - _dragStartPos.Y) / _scale;

                if (_resizeHandle == 0)
                {
                    // 移動
                    int newX = Math.Max(0, (int)(_initialRect.X + dx));
                    int newY = Math.Max(0, (int)(_initialRect.Y + dy));
                    _draggedFrame.CropRect = new OpenCvSharp.Rect(newX, newY, _initialRect.Width, _initialRect.Height);
                }
                else
                {
                    // リサイズ
                    ApplyResize(_draggedFrame, _initialRect, _resizeHandle, dx, dy);
                }

                InvalidateVisual();
                FrameModified?.Invoke(this, EventArgs.Empty);
                return;
            }

            // カーソルの形状更新
            if (IsEyedropperMode)
            {
                Cursor = Cursors.Cross;
            }
            else if (IsDragEnabled && SelectedFrame != null)
            {
                int h = HitTestHandles(pos, SelectedFrame);
                Cursor = GetCursorForHandle(h);
            }
            else if (Frames != null && Frames.Any(f => IsPointInsideFrame(pos, f)))
            {
                Cursor = Cursors.Hand;
            }
            else
            {
                Cursor = Cursors.Arrow;
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            ReleaseMouseCapture();
            _isPanning = false;
            _draggedFrame = null;
            _resizeHandle = -1;
            Cursor = Cursors.Arrow;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            var pos = e.GetPosition(this);

            double zoomFactor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            double newScale = Math.Clamp(_scale * zoomFactor, 0.05, 10.0);

            // マウスカーソル位置を中心にズーム
            _panOffset.X = pos.X - (pos.X - _panOffset.X) * (newScale / _scale);
            _panOffset.Y = pos.Y - (pos.Y - _panOffset.Y) * (newScale / _scale);
            _scale = newScale;

            InvalidateVisual();
            ZoomChanged?.Invoke(this, _scale);
        }

        private bool IsPointInsideFrame(Point screenPos, FilmFrame frame)
        {
            var r = frame.CropRect;
            double sx = _panOffset.X + r.X * _scale;
            double sy = _panOffset.Y + r.Y * _scale;
            double sw = r.Width * _scale;
            double sh = r.Height * _scale;

            return screenPos.X >= sx && screenPos.X <= sx + sw &&
                   screenPos.Y >= sy && screenPos.Y <= sy + sh;
        }

        private int HitTestHandles(Point screenPos, FilmFrame frame)
        {
            var r = frame.CropRect;
            double sx = _panOffset.X + r.X * _scale;
            double sy = _panOffset.Y + r.Y * _scale;
            double sw = r.Width * _scale;
            double sh = r.Height * _scale;

            double tol = 7.0;
            Point[] pts =
            {
                new Point(sx, sy),
                new Point(sx + sw / 2, sy),
                new Point(sx + sw, sy),
                new Point(sx + sw, sy + sh / 2),
                new Point(sx + sw, sy + sh),
                new Point(sx + sw / 2, sy + sh),
                new Point(sx, sy + sh),
                new Point(sx, sy + sh / 2)
            };

            for (int i = 0; i < pts.Length; i++)
            {
                if (Math.Abs(screenPos.X - pts[i].X) <= tol && Math.Abs(screenPos.Y - pts[i].Y) <= tol)
                    return i + 1;
            }

            return -1;
        }

        private Cursor GetCursorForHandle(int handle)
        {
            return handle switch
            {
                1 or 5 => Cursors.SizeNWSE,
                2 or 6 => Cursors.SizeNS,
                3 or 7 => Cursors.SizeNESW,
                4 or 8 => Cursors.SizeWE,
                _ => Cursors.Arrow
            };
        }

        private void ApplyResize(FilmFrame frame, OpenCvSharp.Rect orig, int handle, double dx, double dy)
        {
            int x = orig.X, y = orig.Y, w = orig.Width, h = orig.Height;

            switch (handle)
            {
                case 1: // Top-Left
                    x += (int)dx; y += (int)dy; w -= (int)dx; h -= (int)dy;
                    break;
                case 2: // Top
                    y += (int)dy; h -= (int)dy;
                    break;
                case 3: // Top-Right
                    y += (int)dy; w += (int)dx; h -= (int)dy;
                    break;
                case 4: // Right
                    w += (int)dx;
                    break;
                case 5: // Bottom-Right
                    w += (int)dx; h += (int)dy;
                    break;
                case 6: // Bottom
                    h += (int)dy;
                    break;
                case 7: // Bottom-Left
                    x += (int)dx; w -= (int)dx; h += (int)dy;
                    break;
                case 8: // Left
                    x += (int)dx; w -= (int)dx;
                    break;
            }

            if (w > 30 && h > 30)
            {
                frame.CropRect = new OpenCvSharp.Rect(Math.Max(0, x), Math.Max(0, y), w, h);
            }
        }

        private void PickColorAtScreenPoint(Point screenPos)
        {
            if (ImageSource == null) return;

            int logicalW = LogicalImageWidth > 0 ? LogicalImageWidth : ImageSource.PixelWidth;
            int logicalH = LogicalImageHeight > 0 ? LogicalImageHeight : ImageSource.PixelHeight;

            int logicalX = (int)((screenPos.X - _panOffset.X) / _scale);
            int logicalY = (int)((screenPos.Y - _panOffset.Y) / _scale);

            int imgX = (int)(logicalX * ((double)ImageSource.PixelWidth / logicalW));
            int imgY = (int)(logicalY * ((double)ImageSource.PixelHeight / logicalH));

            if (imgX >= 0 && imgX < ImageSource.PixelWidth && imgY >= 0 && imgY < ImageSource.PixelHeight)
            {
                var cropped = new CroppedBitmap(ImageSource, new Int32Rect(imgX, imgY, 1, 1));
                byte[] pixels = new byte[4];
                cropped.CopyPixels(pixels, 4, 0);

                // BGRA -> R, G, B
                byte b = pixels[0];
                byte g = pixels[1];
                byte r = pixels[2];

                ColorPicked?.Invoke(this, (r, g, b));
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            int step = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? 10 : 2;

            int dx = 0;
            int dy = 0;

            switch (e.Key)
            {
                case Key.Up:
                    dy = -step;
                    break;
                case Key.Down:
                    dy = step;
                    break;
                case Key.Left:
                    dx = -step;
                    break;
                case Key.Right:
                    dx = step;
                    break;
                default:
                    return;
            }

            e.Handled = true;

            // ユーザー要望: コマ位置微調整は適用されている全コマを同時に移動
            NudgeAllFrames(dx, dy);
        }

        /// <summary>
        /// 選択中のコマ枠を平行移動
        /// </summary>
        public void NudgeSelectedFrame(int dx, int dy)
        {
            if (SelectedFrame == null)
            {
                // 選択コマがなければ先頭コマを対象にするか全コマ移動
                if (Frames != null && Frames.Count > 0)
                {
                    NudgeAllFrames(dx, dy);
                }
                return;
            }

            var r = SelectedFrame.CropRect;
            int newX = Math.Max(0, r.X + dx);
            int newY = Math.Max(0, r.Y + dy);

            if (ImageSource != null)
            {
                newX = Math.Min(newX, ImageSource.PixelWidth - r.Width);
                newY = Math.Min(newY, ImageSource.PixelHeight - r.Height);
            }

            SelectedFrame.CropRect = new OpenCvSharp.Rect(newX, newY, r.Width, r.Height);
            InvalidateVisual();
            FrameModified?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 全コマ枠を一括で平行移動
        /// </summary>
        public void NudgeAllFrames(int dx, int dy)
        {
            if (Frames == null || Frames.Count == 0) return;

            foreach (var frame in Frames)
            {
                var r = frame.CropRect;
                int newX = Math.Max(0, r.X + dx);
                int newY = Math.Max(0, r.Y + dy);

                if (ImageSource != null)
                {
                    newX = Math.Min(newX, ImageSource.PixelWidth - r.Width);
                    newY = Math.Min(newY, ImageSource.PixelHeight - r.Height);
                }

                frame.CropRect = new OpenCvSharp.Rect(newX, newY, r.Width, r.Height);
            }

            InvalidateVisual();
            FrameModified?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 選択中のコマ枠のサイズを変更 (dw: 幅増減, dh: 高さ増減)
        /// </summary>
        public void ResizeSelectedFrame(int dw, int dh)
        {
            if (SelectedFrame == null) return;
            var r = SelectedFrame.CropRect;
            int newW = Math.Max(30, r.Width + dw);
            int newH = Math.Max(30, r.Height + dh);

            if (ImageSource != null)
            {
                newW = Math.Min(newW, ImageSource.PixelWidth - r.X);
                newH = Math.Min(newH, ImageSource.PixelHeight - r.Y);
            }

            SelectedFrame.CropRect = new OpenCvSharp.Rect(r.X, r.Y, newW, newH);
            InvalidateVisual();
            FrameModified?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 選択中のコマ枠の座標・寸法を直接指定
        /// </summary>
        public void SetSelectedFrameRect(int x, int y, int w, int h)
        {
            if (SelectedFrame == null) return;
            w = Math.Max(20, w);
            h = Math.Max(20, h);
            x = Math.Max(0, x);
            y = Math.Max(0, y);

            if (ImageSource != null)
            {
                if (x + w > ImageSource.PixelWidth) w = Math.Max(20, ImageSource.PixelWidth - x);
                if (y + h > ImageSource.PixelHeight) h = Math.Max(20, ImageSource.PixelHeight - y);
            }

            SelectedFrame.CropRect = new OpenCvSharp.Rect(x, y, w, h);
            InvalidateVisual();
            FrameModified?.Invoke(this, EventArgs.Empty);
        }

        public void ZoomIn()
        {
            ApplyZoom(1.25, new Point(ActualWidth / 2, ActualHeight / 2));
        }

        public void ZoomOut()
        {
            ApplyZoom(1.0 / 1.25, new Point(ActualWidth / 2, ActualHeight / 2));
        }

        public void ZoomActualSize()
        {
            if (ImageSource == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            _scale = 1.0;
            _panOffset = new Point((ActualWidth - ImageSource.PixelWidth) / 2, (ActualHeight - ImageSource.PixelHeight) / 2);
            InvalidateVisual();
            ZoomChanged?.Invoke(this, _scale);
        }

        private void ApplyZoom(double factor, Point center)
        {
            if (ImageSource == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double newScale = Math.Clamp(_scale * factor, 0.05, 10.0);
            _panOffset.X = center.X - (center.X - _panOffset.X) * (newScale / _scale);
            _panOffset.Y = center.Y - (center.Y - _panOffset.Y) * (newScale / _scale);
            _scale = newScale;
            InvalidateVisual();
            ZoomChanged?.Invoke(this, _scale);
        }

        /// <summary>
        /// 選択中のコマ枠をフィルム幅・画像中央へセンタリング
        /// </summary>
        public void CenterSelectedFrame()
        {
            if (SelectedFrame == null || ImageSource == null) return;
            var r = SelectedFrame.CropRect;
            int newX = Math.Max(0, (ImageSource.PixelWidth - r.Width) / 2);
            SelectedFrame.CropRect = new OpenCvSharp.Rect(newX, r.Y, r.Width, r.Height);
            InvalidateVisual();
            FrameModified?.Invoke(this, EventArgs.Empty);
        }
    }
}
