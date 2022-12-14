using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using Windows.Foundation.Metadata;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Effects;

using log4net;

using Composition.WindowsRuntimeHelpers_NETStd;

using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

using IGReinforced.Recording.Types;
using IGReinforced.Recording.Video.NvEncoder;

using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Encoder = IGReinforced.Recording.Video.NvEncoder.Encoder;

namespace IGReinforced.Recording.Video.WGC
{
    public class WGCHelper
    {
        private static GraphicsCaptureItem _item = null;
        private static IDirect3DDevice _device = Direct3D11Helper.CreateDevice();
        private static Device _sharpDevice = Direct3D11Helper.CreateSharpDXDevice(_device);
        private static Direct3D11CaptureFramePool _framePool = null;
        private static GraphicsCaptureSession _session = null;

        private static int _width;
        private static int _height;
        private static Texture2D _frameTexture;
        private static Encoder _encoder = null;
        private static int _frameCount = 0;

        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static bool IsInitialized { get; private set; } = false;
        public static bool IsBorderRequired { get; set; } = false;

        public static event EventHandler<byte[]> ScreenRefreshed;

        public static bool Initialize()
        {
            if (GraphicsCaptureSession.IsSupported())
            {
                IsInitialized = true;
            }
            return IsInitialized;
        }

        public static void StartHwndCapture(IntPtr hwnd)
        {
            GraphicsCaptureItem item = CaptureHelper.CreateItemForWindow(hwnd);
            if (item != null) StartCaptureInternal(item);
        }

        public static void StartHmonCapture(IntPtr hmon)
        {
            GraphicsCaptureItem item = CaptureHelper.CreateItemForMonitor(hmon);
            if (item != null) StartCaptureInternal(item);
        }

        public static void StartPrimaryMonitorCapture()
        {
            MonitorInfo monitor = (from m in MonitorEnumerationHelper.GetMonitors()
                                   where m.IsPrimary
                                   select m).First();
            StartHmonCapture(monitor.Hmon);
        }

        public static void StartSelectedMonitorCapture()
        {
            StartHmonCapture(Rescreen.Settings.SelectedMonitor.Hmon);
        }

        public static void StopCapture()
        {
            _session?.Dispose();
            _framePool?.Dispose();
            _item = null;
            _session = null;
            _framePool = null;

            _frameTexture?.Dispose();
        }

        private static void StartCaptureInternal(GraphicsCaptureItem item)
        {
            _item = item;
            
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size
                );
            DateTime start = DateTime.MinValue;
            int needElapsed = 0;

            _framePool.FrameArrived += (s, a) =>
            {
                int deltaRes = (int)Rescreen._deltaResSw.ElapsedMilliseconds;
                TimeSpan delta = DateTime.Now - start;

                if (start == DateTime.MinValue)
                {
                    start = DateTime.Now;
                }
                else if (Rescreen.DelayPerFrame > 0 && needElapsed - deltaRes > (int)delta.TotalMilliseconds)
                {
                    Thread.Sleep(needElapsed - deltaRes - (int)delta.TotalMilliseconds);
                }

                Rescreen._deltaResSw.Restart();

                needElapsed += Rescreen.DelayPerFrame;

                if (_framePool == null)
                {
                    log.Error("FramePool is null. Can't process the frame, returned");
                    MessageBox.Show("Can't process the frame with Windows.Graphics.Capture. Try again later.", "IGReinforced", MessageBoxButton.OK, MessageBoxImage.Error);

                    return;
                }

                using (var frame = _framePool.TryGetNextFrame())
                {
                    ProcessFrame(frame);
                }

                Rescreen._delayPerFrameSw.Stop();
                Rescreen._delayPerFrame.Add((int)Rescreen._delayPerFrameSw.ElapsedMilliseconds);

                Rescreen._delayPerFrameSw.Restart();
            };

            _item.Closed += (s, a) =>
            {
                StopCapture();
            };

            _width = item.Size.Width;
            _height = item.Size.Height;

            // Create Staging texture CPU-accessible
            var textureDesc = new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                Width = _width,
                Height = _height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Staging
            };
            _frameTexture = new Texture2D(_sharpDevice, textureDesc);

            if (Rescreen.Settings.Encoding == EncodingType.Nvenc)
            {
                _encoder = new Encoder();

                EncoderDesc setting = new EncoderDesc()
                {
                    width = _width,
                    height = _height,
                    frameRate = Rescreen.FpsIfUnfixed60,
                    format = NvEncoder.Format.B8G8R8A8_UNORM,
                    bitRate = Rescreen.Settings.Bitrate
                };
                setting.maxFrameSize = setting.bitRate / setting.frameRate;

                _encoder.Create(setting, _sharpDevice);
                _encoder.onEncoded += (s, e) =>
                {
                    byte[] buffer = new byte[e.Item2];
                    Marshal.Copy(e.Item1, buffer, 0, e.Item2);

                    ScreenRefreshed?.Invoke(null, buffer);
                };

                if (!_encoder.isValid)
                {
                    Rescreen.Supports.Nvenc = false;
                    log.Warn("Nvenc Encoding Not Supported.");
                }
            }

            _frameCount = 0;

            _session = _framePool.CreateCaptureSession(_item);
            _session.StartCapture();

            Rescreen._delayPerFrameSw.Start();
        }

        private static void ProcessFrame(Direct3D11CaptureFrame frame)
        {
            using (Texture2D surfaceTexture = Direct3D11Helper.CreateSharpDXTexture2D(frame.Surface))
            {
                _sharpDevice.ImmediateContext.CopyResource(surfaceTexture, _frameTexture);
            }

            if (Rescreen.Settings.Encoding == EncodingType.Nvenc)
            {
                bool idr = Rescreen.Settings.Fps > 0 ? _frameCount++ % Rescreen.Settings.Fps == 0 : false;

                if (_encoder.Encode(_frameTexture, false))
                {
                    _encoder.Update();
                }
            }
            else
            {
                DataBox mappedMemory =
                   _sharpDevice.ImmediateContext.MapSubresource(_frameTexture, 0, MapMode.Read, MapFlags.None);

                IntPtr sourcePointer = mappedMemory.DataPointer;
                int sourceStride = mappedMemory.RowPitch;
                int destinationStride = _width * 4;

                byte[] frameBytes = new byte[_width * _height * 4]; // 4 bytes / pixel (High Mem. Allocation)

                unsafe
                {
                    fixed (byte* frameBytesPointer = frameBytes)
                    {
                        IntPtr destinationPointer = (IntPtr)frameBytesPointer;
                        FScreen.CopyMemory(
                            false, // Should run in parallel or not.
                            0,
                            _height,
                            sourcePointer,
                            destinationPointer,
                            sourceStride,
                            destinationStride
                            );
                    }
                }

                _sharpDevice.ImmediateContext.UnmapSubresource(_frameTexture, 0);
                ScreenRefreshed?.Invoke(null, frameBytes);
            }

            Rescreen._deltaResSw.Stop();
            Rescreen._deltaRess.Add((int)Rescreen._deltaResSw.ElapsedMilliseconds);
        }
    }
}
