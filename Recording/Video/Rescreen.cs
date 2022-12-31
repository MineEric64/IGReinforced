using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using OpenCvSharp;

using IGReinforced.Extensions;
using IGReinforced.Recording.Audio.Wasapi;
using IGReinforced.Recording.Highlights;
using IGReinforced.Recording.Types;
using IGReinforced.Recording.Video.WGC;

using Size = System.Drawing.Size;
using WasapiCapture = IGReinforced.Recording.Audio.Wasapi.WasapiCapture;
using NvDecoder = IGReinforced.Recording.Video.NvPipe.Decoder;
using NvPipeCodec = IGReinforced.Recording.Video.NvPipe.Codec;
using NvPipeFormat = IGReinforced.Recording.Video.NvPipe.Format;

namespace IGReinforced.Recording.Video
{
    public class Rescreen
    {
        private static FScreen _raw = new FScreen();

        public static ConcurrentQueue<Buffered> ScreenQueue { get; set; } = new ConcurrentQueue<Buffered>();
        public static ConcurrentQueue<Buffered> AudioQueue { get; set; } = new ConcurrentQueue<Buffered>();
        public static ConcurrentQueue<Buffered> MicQueue { get; set; } = new ConcurrentQueue<Buffered>();

        public static NvDecoder Decoder { get; set; } = null;
        public static Action<(IntPtr, int)> OnDecoded;

        internal static List<int> _deltaRess = new List<int>();
        internal static List<int> _delayPerFrame = new List<int>();

        private static Stopwatch _flow = new Stopwatch(); //actual recording time
        internal static Stopwatch _deltaResSw = new Stopwatch(); //Resolution Per Frame Time
        internal static Stopwatch _delayPerFrameSw = new Stopwatch(); //Delay Per Frame Time

        public static BitrateInfo Bitrate1440 { get; private set; } = new BitrateInfo(20);
        public static BitrateInfo Bitrate1080 { get; private set; } = new BitrateInfo(10);

        public static RescreenSettings Settings { get; private set; } = RescreenSettings.Default;
        public static CaptureSupports Supports { get; private set; } = CaptureSupports.Default;

        public static int DelayPerFrame => 1000 / Settings.Fps;
        public static int FpsIfUnfixed60 => Settings.Fps > 0 ? Settings.Fps : 60;
        public static Size ScreenSize => Settings.SelectedMonitor.ScreenSize;
        public static int ReplayLength { get; set; } = 30; //unit: second

        public static bool IsRecording { get; private set; } = false;
        public static bool IsSaving { get; set; } = false;
        public static TimeSpan Elapsed => _flow.Elapsed;

        public static void MakeSettings(RescreenSettings settings)
        {
            Settings = settings;
        }

        public static void Start()
        {
            _flow.Reset();
            HighlightManager.Flow.Reset();

            Settings.Bitrate = GetBitrateInfo().Bps;

            void InitializeVideo() //300ms elapsed
            {
                Decoder = new NvDecoder(ScreenSize.Width, ScreenSize.Height, NvPipeCodec.H264, NvPipeFormat.RGBA32);
                Decoder.onDecoded += (s, e) =>
                {
                    if (IsSaving)
                    {
                        OnDecoded?.Invoke(e);
                    }
                };

                switch (Settings.VideoType)
                {
                    case CaptureVideoType.DD:
                        CaptureSupports.SupportsDesktopDuplication();

                        if (!Supports.DesktopDuplication)
                        {
                            MessageBox.Show("The screen can't be captured when using DXGI Desktop Duplication.\nPlease use another capture method.",
                            "IGReinforced : Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        _raw.ScreenRefreshed += ScreenRefreshed;
                        _raw.Start();

                        break;

                    case CaptureVideoType.WGC:
                        CaptureSupports.SupportsWGC();

                        if (!Supports.WGC)
                        {
                            MessageBox.Show("The screen can't be captured when using Windows.Graphics.Capture.\nPlease use another capture method.",
                            "IGReinforced : Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                        WGCHelper.ScreenRefreshed += ScreenRefreshed;
                        WGCHelper.StartSelectedMonitorCapture();

                        break;
                }
            }
            void InitializeAudio() //200ms elapsed
            {
                if (!WasapiCapture.IsInitialized)
                {
                    WasapiCapture.InitializeIn();
                    WasapiCapture.InitializeOut();
                }

                WasapiCapture.AudioDataAvailable += AudioRefreshed;
                WasapiCapture.MicDataAvailable += MicRefreshed;
                WasapiCapture.Record();
            }

            MainWindow.MyDispatcher.BeginInvoke(new Action(() => InitializeVideo()));
            MainWindow.MyDispatcher.BeginInvoke(new Action(() => InitializeAudio()));

            _flow.Start();
            HighlightManager.Flow.Start();

            IsRecording = true;
        }

        public static void Stop()
        {
            if (!IsRecording) return;

            switch (Settings.VideoType)
            {
                case CaptureVideoType.DD:
                    _raw.Stop();
                    _raw.ScreenRefreshed -= ScreenRefreshed;

                    break;

                case CaptureVideoType.WGC:
                    WGCHelper.StopCapture();
                    WGCHelper.ScreenRefreshed -= ScreenRefreshed;

                    break;
            }

            Decoder?.Close();
            Decoder = null;

            WasapiCapture.Stop();
            WasapiCapture.AudioDataAvailable -= AudioRefreshed;
            WasapiCapture.MicDataAvailable -= MicRefreshed;

            _flow.Stop();
            HighlightManager.Flow.Stop();

            _deltaResSw.Reset();
            _delayPerFrameSw.Reset();

            ClearAllBuffer();
            IsRecording = false;
        }

        private static void ScreenRefreshed(object sender, byte[] buffer)
        {
            byte[] compressed = buffer.Compress(); //byte[] -> compressed byte[]

            if (Elapsed.TotalSeconds > ReplayLength)
            {
                ScreenQueue.TryDequeue(out var old);

                if (!IsSaving)
                {
                    byte[] decompressed = old.Buffer.Decompress();
                    GCHandle handle = GCHandle.Alloc(decompressed, GCHandleType.Pinned);
                    IntPtr oldPtr = handle.AddrOfPinnedObject();

                    Decoder.Decode(oldPtr, decompressed.Length);
                    handle.Free();
                }
            }

            ScreenQueue.Enqueue(new Buffered(compressed));
        }

        private static void AudioRefreshed(object sender, byte[] buffer)
        {
            const bool DISCARD_IF_EMPTY = false;

            if (!DISCARD_IF_EMPTY || buffer.Any(x => x != 0))
            {
                byte[] compressed = buffer.Compress(); //byte[] -> compressed byte[]

                if (Elapsed.TotalSeconds > ReplayLength)
                    AudioQueue.TryDequeue(out _);
                AudioQueue.Enqueue(new Buffered(compressed));
            }
        }

        private static void MicRefreshed(object sender, byte[] buffer)
        {
            const bool DISCARD_IF_EMPTY = false;

            if (!DISCARD_IF_EMPTY || buffer.Any(x => x != 0))
            {
                byte[] compressed = buffer.Compress(); //byte[] -> compressed byte[]

                if (Elapsed.TotalSeconds > ReplayLength)
                    MicQueue.TryDequeue(out _);
                MicQueue.Enqueue(new Buffered(compressed));
            }
        }

        public static BitrateInfo GetBitrateInfo()
        {
            BitrateInfo info = null;
            int height = ScreenSize.Height;

            if (height > 1440) info = Bitrate1440;
            else if (height > 1080 && height < 1440) info = Bitrate1080;
            else info = Bitrate1080;

            return info;
        }

        public static void ClearAllBuffer()
        {
            int screenLength = ScreenQueue.Count;
            int audioLength = AudioQueue.Count;
            int micLength = MicQueue.Count;

            for (int i = 0; i < screenLength; i++) ScreenQueue.TryDequeue(out _);
            for (int i = 0; i < audioLength; i++) AudioQueue.TryDequeue(out _);
            for (int i = 0; i < micLength; i++) MicQueue.TryDequeue(out _);

            _flow.Reset();
            _flow.Start();
        }

        public static bool SupportsNvenc()
        {
            if (Settings.SelectedMonitor.GPU != GPUSelect.Nvidia) return false;
            if (!CaptureSupports.SupportsNvenc()) return false;
            return true;
        }

        //For Debugging Methods for Rescreen!!!
        //For Debugging Methods for Rescreen!!!
        //For Debugging Methods for Rescreen!!!

        public static double GetFps(int frameCount, double elapsedSeconds)
        {
            return frameCount / elapsedSeconds;
        }

        public static double GetAverage(List<int> list)
        {
            var cloned = list.ToList();
            return cloned.Count > 0 ? cloned.Average() : 0;
        }

        public static double GetAverageMbps(double fps)
        {
            var lengthList = new List<int>();
            var screenList = ScreenQueue.ToList();
            int length = 0;
            int position = 0;

            foreach (var screen in screenList)
            {
                if (position++ > fps)
                {
                    position = 0;
                    lengthList.Add(length);
                    length = 0;
                }
                length += screen.Buffer.Length;
            }

            if (lengthList.Count == 0) return 0.0;

            double GetAverageMbps2(double lengthPerSecond)
            {
                const double MB_PER_BYTE = 9.537 * 0.0000001;
                double averageMb = lengthPerSecond * MB_PER_BYTE;

                return averageMb * 8;
            }

            return GetAverageMbps2(lengthList.Average());
        }

        public static string GetRecordedInfo()
        {
            double fps = GetFps(ScreenQueue.Count, Elapsed.TotalSeconds);
            double rpf = GetAverage(_deltaRess);
            double dpf = GetAverage(_delayPerFrame);
            string info =
                "Resolution Per Frame : " + rpf.ToString("0.##") + "ms\n" +
                "Delay Per Frame : " + dpf.ToString("0.##") + "ms\n" +
                "Fps : " + fps.ToString("0.##") + "\n" +
                "Fps (DPF) : " + (1000 / dpf).ToString("0.##") + "\n" +
                "Mbps : " + GetAverageMbps(fps).ToString("0.##");

            return info;
        }
    }
}
