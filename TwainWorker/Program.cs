using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using NTwain;
using NTwain.Data;

namespace IrisPxS.TwainWorker
{
    internal static class Program
    {
        private static TwainSession? _session;
        private static bool _scanSuccess = false;
        private static string _outputPath = "";
        private static int _dpi = 300;
        private static int _bitDepth = 8; // 8 (24bpp) or 16 (48bpp)
        private static bool _useTpu = true;
        private static bool _showUi = false;
        private static string? _errorMessage = null;

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Contains("--list"))
            {
                return InspectSources();
            }
            if (args.Contains("--inspect"))
            {
                return InspectGtx820Capabilities();
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--output" && i + 1 < args.Length)
                {
                    _outputPath = args[++i];
                }
                else if (args[i] == "--dpi" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out _dpi);
                }
                else if (args[i] == "--bitdepth" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int bd))
                    {
                        _bitDepth = (bd >= 16) ? 16 : 8;
                    }
                }
                else if (args[i] == "--bpp" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out int bpp))
                    {
                        _bitDepth = (bpp >= 48) ? 16 : 8;
                    }
                }
                else if (args[i] == "--tpu")
                {
                    _useTpu = true;
                }
                else if (args[i] == "--reflective")
                {
                    _useTpu = false;
                }
                else if (args[i] == "--ui")
                {
                    _showUi = true;
                }
            }

            if (string.IsNullOrEmpty(_outputPath))
            {
                _outputPath = Path.Combine(Path.GetTempPath(), $"IrisPxS_Twain_{Guid.NewGuid():N}.bmp");
            }

            var form = new MainForm();
            Application.Run(form);

            if (_scanSuccess && File.Exists(_outputPath))
            {
                Console.WriteLine($"SUCCESS:{_outputPath}");
                return 0;
            }
            else
            {
                Console.Error.WriteLine($"ERROR:{_errorMessage ?? "Scan did not complete or was cancelled."}");
                return 1;
            }
        }

        private static int InspectSources()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
            var session = new TwainSession(appId);
            var rc = session.Open();
            if (rc != ReturnCode.Success)
            {
                Console.Error.WriteLine($"Failed to open DSM: {rc}");
                return 1;
            }

            Console.WriteLine("Installed TWAIN Sources:");
            foreach (var src in session)
            {
                Console.WriteLine($"Source: '{src.Name}' | Mfr: '{src.Manufacturer}' | Family: '{src.ProductFamily}' | Version: '{src.Version.Info}'");
            }
            session.Close();
            return 0;
        }

        private static int InspectGtx820Capabilities()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
            var session = new TwainSession(appId);
            var rc = session.Open();
            if (rc != ReturnCode.Success)
            {
                Console.Error.WriteLine($"Failed to open DSM: {rc}");
                return 1;
            }

            var src = session.FirstOrDefault(s => s.Name.Equals("EPSON GT-X820", StringComparison.OrdinalIgnoreCase));
            if (src == null)
            {
                Console.Error.WriteLine("EPSON GT-X820 TWAIN source not found.");
                session.Close();
                return 1;
            }

            rc = src.Open();
            if (rc != ReturnCode.Success)
            {
                Console.Error.WriteLine($"Failed to open EPSON GT-X820: {rc}");
                session.Close();
                return 1;
            }

            Console.WriteLine("=== EPSON GT-X820 TWAIN Capabilities ===");
            Console.WriteLine($"ICapLightPath: CanGet={src.Capabilities.ICapLightPath.CanGet}, CanSet={src.Capabilities.ICapLightPath.CanSet}");
            if (src.Capabilities.ICapLightPath.CanGet)
            {
                Console.WriteLine($"Current LightPath: {src.Capabilities.ICapLightPath.GetCurrent()}");
                var values = src.Capabilities.ICapLightPath.GetValues();
                Console.WriteLine($"Supported LightPath values: {string.Join(", ", values)}");
            }

            Console.WriteLine($"ICapFilmType: CanGet={src.Capabilities.ICapFilmType.CanGet}, CanSet={src.Capabilities.ICapFilmType.CanSet}");
            if (src.Capabilities.ICapFilmType.CanGet)
            {
                Console.WriteLine($"Current FilmType: {src.Capabilities.ICapFilmType.GetCurrent()}");
                var values = src.Capabilities.ICapFilmType.GetValues();
                Console.WriteLine($"Supported FilmType values: {string.Join(", ", values)}");
            }

            Console.WriteLine($"ICapLightSource: CanGet={src.Capabilities.ICapLightSource.CanGet}, CanSet={src.Capabilities.ICapLightSource.CanSet}");
            if (src.Capabilities.ICapLightSource.CanGet)
            {
                Console.WriteLine($"Current LightSource: {src.Capabilities.ICapLightSource.GetCurrent()}");
            }

            Console.WriteLine($"ICapPixelType: CanGet={src.Capabilities.ICapPixelType.CanGet}, CanSet={src.Capabilities.ICapPixelType.CanSet}");
            if (src.Capabilities.ICapPixelType.CanGet)
            {
                Console.WriteLine($"Current PixelType: {src.Capabilities.ICapPixelType.GetCurrent()}");
                var values = src.Capabilities.ICapPixelType.GetValues();
                Console.WriteLine($"Supported PixelTypes: {string.Join(", ", values)}");
            }

            Console.WriteLine($"ICapBitDepth: CanGet={src.Capabilities.ICapBitDepth.CanGet}, CanSet={src.Capabilities.ICapBitDepth.CanSet}");
            if (src.Capabilities.ICapBitDepth.CanGet)
            {
                Console.WriteLine($"Current BitDepth: {src.Capabilities.ICapBitDepth.GetCurrent()}");
                var values = src.Capabilities.ICapBitDepth.GetValues();
                Console.WriteLine($"Supported BitDepths: {string.Join(", ", values)}");
            }

            Console.WriteLine($"ICapXResolution: CanGet={src.Capabilities.ICapXResolution.CanGet}, CanSet={src.Capabilities.ICapXResolution.CanSet}");
            if (src.Capabilities.ICapXResolution.CanGet)
            {
                var curX = src.Capabilities.ICapXResolution.GetCurrent();
                Console.WriteLine($"Current XResolution: {curX.Whole + (float)curX.Fraction / 65536f}");
            }

            src.Close();
            session.Close();
            return 0;
        }

        private class MainForm : Form
        {
            public MainForm()
            {
                this.Text = "IRIS PxS TWAIN Worker";
                this.Size = new Size(300, 150);
                this.StartPosition = FormStartPosition.CenterScreen;
                if (!_showUi)
                {
                    this.WindowState = FormWindowState.Minimized;
                    this.ShowInTaskbar = false;
                    this.FormBorderStyle = FormBorderStyle.None;
                }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                ThreadPool.QueueUserWorkItem(_ => ExecuteScan(this.Handle));
            }

            private void ExecuteScan(IntPtr hWnd)
            {
                try
                {
                    var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
                    _session = new TwainSession(appId);
                    _session.SynchronizationContext = SynchronizationContext.Current;

                    _session.DataTransferred += (s, e) =>
                    {
                        try
                        {
                            Console.WriteLine("[TwainWorker] DataTransferred event received.");
                            if (e.NativeData != IntPtr.Zero)
                            {
                                SaveDibToFile(e.NativeData, _outputPath);
                                _scanSuccess = true;
                            }
                            else if (!string.IsNullOrEmpty(e.FileDataPath) && File.Exists(e.FileDataPath))
                            {
                                File.Copy(e.FileDataPath, _outputPath, true);
                                _scanSuccess = true;
                                Console.WriteLine($"[TwainWorker] Saved file image to {_outputPath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _errorMessage = $"Save error: {ex.Message}";
                            Console.WriteLine($"[TwainWorker] Save error: {ex}");
                        }
                    };

                    _session.TransferError += (s, e) =>
                    {
                        _errorMessage = $"Transfer error: {e.Exception?.Message ?? e.ReturnCode.ToString()}";
                        Console.WriteLine($"[TwainWorker] Transfer error: {_errorMessage}");
                    };

                    _session.SourceDisabled += (s, e) =>
                    {
                        Console.WriteLine("[TwainWorker] Source disabled.");
                        this.BeginInvoke(new Action(() => this.Close()));
                    };

                    var rc = _session.Open();
                    if (rc != ReturnCode.Success)
                    {
                        _errorMessage = $"Could not open TWAIN DSM: {rc}";
                        this.BeginInvoke(new Action(() => this.Close()));
                        return;
                    }

                    // EPSON GT-X820 または EPSON スキャナーを優先選択
                    var source = _session.FirstOrDefault(s => s.Name.Contains("GT-X820", StringComparison.OrdinalIgnoreCase))
                                 ?? _session.FirstOrDefault(s => s.Name.Contains("EPSON", StringComparison.OrdinalIgnoreCase))
                                 ?? _session.FirstOrDefault();

                    if (source == null)
                    {
                        _errorMessage = "No TWAIN scanner found.";
                        this.BeginInvoke(new Action(() => this.Close()));
                        return;
                    }

                    Console.WriteLine($"[TwainWorker] Opening TWAIN Source: {source.Name}...");
                    rc = source.Open();
                    if (rc != ReturnCode.Success)
                    {
                        _errorMessage = $"Failed to open source {source.Name}: {rc}";
                        this.BeginInvoke(new Action(() => this.Close()));
                        return;
                    }

                    // 設定 (透過原稿ユニット設定を最優先)
                    try
                    {
                        // 1. 透過原稿ユニット (TPU / フィルムモード: 蓋側ランプ点灯)
                        Console.WriteLine($"[TwainWorker] LightPath CanGet={source.Capabilities.ICapLightPath.CanGet}, CanSet={source.Capabilities.ICapLightPath.CanSet}");
                        if (source.Capabilities.ICapLightPath.CanGet)
                        {
                            Console.WriteLine($"[TwainWorker] Current LightPath: {source.Capabilities.ICapLightPath.GetCurrent()}");
                        }

                        if (source.Capabilities.ICapLightPath.CanSet)
                        {
                            var targetPath = _useTpu ? LightPath.Transmissive : LightPath.Reflective;
                            var r = source.Capabilities.ICapLightPath.SetValue(targetPath);
                            Console.WriteLine($"[TwainWorker] Set ICapLightPath to {targetPath}: {r}");
                        }

                        // 2. カラー (RGB)
                        if (source.Capabilities.ICapPixelType.CanSet)
                        {
                            var r = source.Capabilities.ICapPixelType.SetValue(PixelType.RGB);
                            Console.WriteLine($"[TwainWorker] Set PixelType.RGB: {r}");
                        }

                        // 3. ビット深度 (BitDepth: 8 or 16 bit/ch)
                        if (source.Capabilities.ICapBitDepth.CanSet)
                        {
                            var r = source.Capabilities.ICapBitDepth.SetValue((short)_bitDepth);
                            Console.WriteLine($"[TwainWorker] Set ICapBitDepth {_bitDepth}: {r}");
                        }

                        // 3. 解像度 (DPI)
                        if (source.Capabilities.ICapXResolution.CanSet)
                        {
                            var r = source.Capabilities.ICapXResolution.SetValue(new TWFix32 { Whole = (short)_dpi, Fraction = 0 });
                            Console.WriteLine($"[TwainWorker] Set XResolution {_dpi}: {r}");
                        }
                        if (source.Capabilities.ICapYResolution.CanSet)
                        {
                            var r = source.Capabilities.ICapYResolution.SetValue(new TWFix32 { Whole = (short)_dpi, Fraction = 0 });
                            Console.WriteLine($"[TwainWorker] Set YResolution {_dpi}: {r}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TwainWorker] Capability config warning: {ex.Message}");
                    }

                    // スキャン開始
                    SourceEnableMode mode = _showUi ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI;
                    Console.WriteLine($"[TwainWorker] Enabling Source with mode: {mode}...");
                    rc = source.Enable(mode, _showUi, hWnd);
                    if (rc != ReturnCode.Success)
                    {
                        _errorMessage = $"Failed to enable source: {rc}";
                        Console.WriteLine($"[TwainWorker] {_errorMessage}");
                        this.BeginInvoke(new Action(() => this.Close()));
                    }
                }
                catch (Exception ex)
                {
                    _errorMessage = $"Exception in ExecuteScan: {ex.Message}";
                    Console.WriteLine($"[TwainWorker] {_errorMessage}");
                    this.BeginInvoke(new Action(() => this.Close()));
                }
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                try
                {
                    if (_session != null)
                    {
                        if (_session.CurrentSource != null && _session.CurrentSource.IsOpen)
                        {
                            _session.CurrentSource.Close();
                        }
                        if (_session.State >= 3)
                        {
                            _session.Close();
                        }
                    }
                }
                catch { }
                base.OnFormClosing(e);
            }

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern IntPtr GlobalLock(IntPtr handle);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern bool GlobalUnlock(IntPtr handle);

            [DllImport("kernel32.dll", ExactSpelling = true)]
            private static extern UIntPtr GlobalSize(IntPtr handle);

            private static void SaveDibToFile(IntPtr hGlobal, string outputPath)
            {
                if (hGlobal == IntPtr.Zero)
                {
                    throw new ArgumentException("hGlobal is null");
                }

                IntPtr ptr = GlobalLock(hGlobal);
                if (ptr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to lock HGLOBAL data.");
                }

                try
                {
                    // Read BITMAPINFOHEADER (at least 40 bytes)
                    uint biSize = (uint)Marshal.ReadInt32(ptr, 0);
                    if (biSize < 40)
                    {
                        throw new InvalidOperationException($"Invalid DIB header size: {biSize}");
                    }

                    int biWidth = Marshal.ReadInt32(ptr, 4);
                    int biHeight = Marshal.ReadInt32(ptr, 8);
                    ushort biPlanes = (ushort)Marshal.ReadInt16(ptr, 12);
                    ushort biBitCount = (ushort)Marshal.ReadInt16(ptr, 14);
                    uint biCompression = (uint)Marshal.ReadInt32(ptr, 16);
                    uint biSizeImage = (uint)Marshal.ReadInt32(ptr, 20);
                    uint biClrUsed = (uint)Marshal.ReadInt32(ptr, 32);

                    int colorCount = 0;
                    if (biClrUsed > 0)
                    {
                        colorCount = (int)biClrUsed;
                    }
                    else if (biBitCount <= 8)
                    {
                        colorCount = 1 << biBitCount;
                    }
                    int colorTableSize = colorCount * 4;

                    // If BI_BITFIELDS (compression == 3) and biSize == 40, there are 3 DWORD masks (12 bytes)
                    if (biCompression == 3 && biSize == 40)
                    {
                        colorTableSize = 12;
                    }

                    int stride = ((biWidth * biBitCount + 31) / 32) * 4;
                    if (biSizeImage == 0)
                    {
                        biSizeImage = (uint)(stride * Math.Abs(biHeight));
                    }

                    ulong dibTotalBytes = GlobalSize(hGlobal).ToUInt64();
                    ulong calculatedBytes = biSize + (ulong)colorTableSize + biSizeImage;
                    if (dibTotalBytes == 0 || dibTotalBytes < calculatedBytes)
                    {
                        dibTotalBytes = calculatedBytes;
                    }

                    uint bfOffBits = 14 + biSize + (uint)colorTableSize;
                    uint bfSize = (uint)(14 + dibTotalBytes);

                    using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    using (var bw = new BinaryWriter(fs))
                    {
                        // 14-byte BITMAPFILEHEADER
                        bw.Write((ushort)0x4D42);  // 'BM'
                        bw.Write(bfSize);          // bfSize
                        bw.Write((ushort)0);       // bfReserved1
                        bw.Write((ushort)0);       // bfReserved2
                        bw.Write(bfOffBits);       // bfOffBits

                        // Stream the DIB data in 64KB chunks directly from unmanaged memory
                        byte[] chunk = new byte[65536];
                        long remaining = (long)dibTotalBytes;
                        long offset = 0;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(chunk.Length, remaining);
                            Marshal.Copy(new IntPtr(ptr.ToInt64() + offset), chunk, 0, toRead);
                            fs.Write(chunk, 0, toRead);
                            offset += toRead;
                            remaining -= toRead;
                        }
                    }

                    Console.WriteLine($"[TwainWorker] Successfully streamed DIB ({biWidth}x{biHeight}, {biBitCount}bpp, {dibTotalBytes} bytes) to {outputPath}");
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }
            }
        }
    }
}
