using System;
using System.Threading;
using System.Drawing;

using SharpTox.Av;
using SharpTox.Core;
using SharpTox.Vpx;

using NAudio.Wave;
using AForge.Video;
using AForge.Video.DirectShow;
using System.Runtime.InteropServices;

namespace Toxy.ToxHelpers
{
    class ToxCall
    {
        private ToxAv toxav;

        private VideoCaptureDevice videoSource;
        private VideoWindow videoWindow;

        private uint frame_size;
        private WaveIn waveSource;
        private WaveOut waveOut;
        private BufferedWaveProvider waveProvider;

        public int CallIndex;
        public int FriendNumber;
        public bool VideoSupport;

        public ToxCall(ToxAv toxav, int callindex, int friendnumber, bool videoSupport)
        {
            this.toxav = toxav;
            this.FriendNumber = friendnumber;
            this.VideoSupport = videoSupport;

            CallIndex = callindex;
        }

        public void Start(int input, int output)
        {
            //who doesn't love magic numbers?!
            toxav.PrepareTransmission(CallIndex, 3, 40, VideoSupport);

            WaveFormat format = new WaveFormat((int)toxav.CodecSettings.AudioSampleRate, (int)toxav.CodecSettings.AudioChannels);
            waveProvider = new BufferedWaveProvider(format);
            waveProvider.DiscardOnBufferOverflow = true;

            if (WaveIn.DeviceCount > 0)
            {
                waveSource = new WaveIn();

                if (input != -1)
                    waveSource.DeviceNumber = input - 1;

                waveSource.WaveFormat = format;
                waveSource.DataAvailable += wave_source_DataAvailable;
                waveSource.BufferMilliseconds = (int)toxav.CodecSettings.AudioFrameDuration;
                waveSource.StartRecording();
            }

            if (WaveOut.DeviceCount > 0)
            {
                waveOut = new WaveOut();

                if (output != -1)
                    waveOut.DeviceNumber = output - 1;

                waveOut.Init(waveProvider);
                waveOut.Play();
            }

            //webcam detection stuff
            if (VideoSupport)
            {
                FilterInfoCollection list = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                videoSource = new VideoCaptureDevice(list[0].MonikerString);
                videoSource.NewFrame += video_source_NewFrame;
                videoSource.Start();

                videoWindow = new VideoWindow();
                videoWindow.Closed += videoWindow_Closed;
                videoWindow.Show();
            }
        }

        private void videoWindow_Closed(object sender, EventArgs args)
        {
            Stop();
        }

        private void video_source_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            sendVideoFrame(eventArgs.Frame);
        }

        public void ProcessAudioFrame(short[] frame, int frame_size)
        {
            if (waveOut == null)
                return;

            byte[] bytes = ShortArrayToByteArray(frame);
            waveProvider.AddSamples(bytes, 0, bytes.Length);
        }

        public void ProcessVideoFrame(IntPtr frame)
        {
            VpxImage image = VpxImage.FromPointer(frame);

            if (videoWindow == null)
            {
                image.Free();
                return;
            }

            byte[] dest = VpxHelper.Yuv420ToRgb(image, image.d_w * image.d_h * 4);

            image.Free();

            GCHandle handle = GCHandle.Alloc(dest, GCHandleType.Pinned);
            Bitmap bitmap = Bitmap.FromHbitmap(GdiWrapper.CreateBitmap((int)image.d_w, (int)image.d_h, 1, 32, handle.AddrOfPinnedObject()));
            handle.Free();

            videoWindow.PushVideoFrame(bitmap);
        }

        private byte[] ShortArrayToByteArray(short[] shorts)
        {
            byte[] bytes = new byte[shorts.Length * 2];

            for (int i = 0; i < shorts.Length; ++i)
            {
                bytes[2 * i] = (byte)shorts[i];
                bytes[2 * i + 1] = (byte)(shorts[i] >> 8);
            }

            return bytes;
        }

        private void wave_source_DataAvailable(object sender, WaveInEventArgs e)
        {
            ushort[] ushorts = new ushort[e.Buffer.Length / 2];
            Buffer.BlockCopy(e.Buffer, 0, ushorts, 0, e.Buffer.Length);

            byte[] dest = new byte[65535];
            int size = toxav.PrepareAudioFrame(CallIndex, dest, 65535, ushorts);

            ToxAvError error = toxav.SendAudio(CallIndex, dest, size);
            if (error != ToxAvError.None)
                Console.WriteLine("Could not send audio: {0}", error);
        }

        public void Stop()
        {
            //TODO: we might want to block here until RecordingStopped and PlaybackStopped are fired

            if (waveSource != null)
            {
                waveSource.StopRecording();
                waveSource.Dispose();
            }

            if (waveOut != null)
            {
                waveOut.Stop();
                waveOut.Dispose();
            }

            if (videoSource != null)
            {
                videoSource.SignalToStop();
                videoSource.NewFrame -= video_source_NewFrame;
                videoSource = null;
            }

            if (videoWindow != null)
                videoWindow.Close();

            toxav.KillTransmission(CallIndex);
            toxav.Hangup(CallIndex);
        }

        public void SwitchInputDevice(int index)
        {
            waveSource.StopRecording();
            waveSource.DeviceNumber = index;
            waveSource.StartRecording();
        }

        public void SwitchOutputDevice(int index)
        {
            waveOut.Stop();
            waveOut.Dispose();

            waveOut = new WaveOut();
            waveOut.DeviceNumber = index;
            waveOut.Init(waveProvider);
            waveOut.Play();
        }

        public void Answer(ToxAvCodecSettings settings)
        {
            ToxAvError error = toxav.Answer(CallIndex, settings);
            if (error != ToxAvError.None)
                throw new Exception("Could not answer call " + error.ToString());
        }

        public void Call(int current_number, ToxAvCodecSettings settings, int ringing_seconds)
        {
            toxav.Call(current_number, settings, ringing_seconds, out CallIndex);
        }

        private void sendVideoFrame(Bitmap frame)
        {
            GdiWrapper.BITMAPINFO info = new GdiWrapper.BITMAPINFO()
            {
                bmiHeader =
                {
                    biWidth = frame.Width,
                    biHeight = -frame.Height,
                    biPlanes = 1,
                    biBitCount = 24,
                    biCompression = GdiWrapper.BitmapCompressionMode.BI_RGB
                }
            };

            info.bmiHeader.Init();

            byte[] bytes = new byte[frame.Width * frame.Height * 3];
            IntPtr context = GdiWrapper.CreateCompatibleDC(IntPtr.Zero);
            IntPtr hbitmap = frame.GetHbitmap();

            GdiWrapper.GetDIBits(context, hbitmap, 0, (uint)frame.Height, bytes, ref info, GdiWrapper.DIB_Color_Mode.DIB_RGB_COLORS);
            GdiWrapper.DeleteObject(hbitmap);
            GdiWrapper.DeleteDC(context);

            byte[] dest = new byte[frame.Width * frame.Height * 4];

            try
            {
                VpxImage img = VpxImage.Create(VpxImageFormat.VPX_IMG_FMT_I420, (ushort)frame.Width, (ushort)frame.Height, 1);

                //fixed (byte* b = bytes)
                VpxHelper.RgbToYuv420(img, bytes, (ushort)frame.Width, (ushort)frame.Height);

                int length = ToxAvFunctions.PrepareVideoFrame(toxav.GetHandle(), CallIndex, dest, dest.Length, (IntPtr)img.Pointer);
                img.Free();

                if (length > 0)
                {
                    byte[] bytesToSend = new byte[length];
                    Array.Copy(dest, bytesToSend, length);

                    ToxAvError error = ToxAvFunctions.SendVideo(toxav.GetHandle(), CallIndex, bytesToSend, (uint)bytesToSend.Length);
                    if (error != ToxAvError.None)
                        Console.WriteLine("Could not send video frame: {0}, {1}", error, length);
                }
                else
                {
                    Console.WriteLine("Could not prepare frame: {0}", (ToxAvError)length);
                }
            }
            catch
            {
                Console.WriteLine("Could not convert frame");
            }

            frame.Dispose();
        }
    }
}
