using System.Reflection;
using System.Runtime.InteropServices;

namespace Yib;

internal static class AudioPlayer
{
    internal const int DefaultDevice = -1;

    private const int SampleRate = 44100;
    private const ushort Channels = 1;
    private const ushort BitsPerSample = 16;
    private const int ClickWavHeaderSize = 44;

    // WAVEHDR.dwFlags bit the driver sets once it is finished with the buffer.
    private const uint WHDR_DONE = 0x00000001;

    // waveOutUnprepareHeader returns this while the buffer is still in use.
    private const int WAVERR_STILLPLAYING = 33;

    private static readonly byte[] ClickPcm = LoadClickPcm();

    public static IReadOnlyList<(int Index, string Name)> GetOutputDevices()
    {
        var devices = new List<(int Index, string Name)>();
        uint count = NativeMethods.waveOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            var caps = new NativeMethods.WAVEOUTCAPS();
            if (NativeMethods.waveOutGetDevCaps((IntPtr)i, ref caps, (uint)Marshal.SizeOf<NativeMethods.WAVEOUTCAPS>()) == 0)
            {
                devices.Add(((int)i, caps.szPname));
            }
        }

        return devices;
    }

    public static void PlayClick(int deviceIndex, int volumePercent)
    {
        if (ClickPcm.Length == 0)
        {
            return;
        }

        // waveOut* playback blocks the calling thread for the clip's duration, so it must run
        // off the UI thread - the caller (closing the dial, posting the paste) can't wait on it.
        ThreadPool.QueueUserWorkItem(_ => PlayInternal(deviceIndex, volumePercent));
    }

    private static void PlayInternal(int deviceIndex, int volumePercent)
    {
        var format = new NativeMethods.WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (ushort)(Channels * BitsPerSample / 8),
            nAvgBytesPerSec = SampleRate * Channels * BitsPerSample / 8,
        };

        IntPtr device = (IntPtr)(deviceIndex < 0 ? DefaultDevice : deviceIndex);
        if (NativeMethods.waveOutOpen(out IntPtr handle, device, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
        {
            return;
        }

        GCHandle pinned = default;
        IntPtr headerPtr = IntPtr.Zero;
        int headerSize = Marshal.SizeOf<NativeMethods.WAVEHDR>();
        bool prepared = false;
        try
        {
            ushort channelVolume = (ushort)(0xFFFF * Math.Clamp(volumePercent, 0, 100) / 100);
            uint volume = channelVolume | ((uint)channelVolume << 16);
            NativeMethods.waveOutSetVolume(handle, volume);

            pinned = GCHandle.Alloc(ClickPcm, GCHandleType.Pinned);
            headerPtr = Marshal.AllocHGlobal(headerSize);
            var header = new NativeMethods.WAVEHDR
            {
                lpData = pinned.AddrOfPinnedObject(),
                dwBufferLength = (uint)ClickPcm.Length,
            };
            Marshal.StructureToPtr(header, headerPtr, false);

            if (NativeMethods.waveOutPrepareHeader(handle, headerPtr, (uint)headerSize) == 0)
            {
                prepared = true;
                NativeMethods.waveOutWrite(handle, headerPtr, (uint)headerSize);

                // Wait for the driver to actually finish with the buffer, signalled by WHDR_DONE -
                // NOT a fixed Thread.Sleep guess. Freeing the pinned PCM/header while playback is
                // still reading from it is a use-after-free that corrupts the process heap (an
                // 0xC0000005 crash in ucrtbase that no managed handler can catch).
                int durationMs = (int)(ClickPcm.Length / (double)format.nAvgBytesPerSec * 1000);
                long deadline = Environment.TickCount64 + durationMs + 500;
                while (Environment.TickCount64 < deadline)
                {
                    var current = Marshal.PtrToStructure<NativeMethods.WAVEHDR>(headerPtr);
                    if ((current.dwFlags & WHDR_DONE) != 0)
                    {
                        break;
                    }
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            // Stop any still-in-flight playback so the driver releases the buffer before we
            // unprepare and free it - belt-and-suspenders if WHDR_DONE never arrived in time.
            NativeMethods.waveOutReset(handle);

            if (prepared)
            {
                for (int i = 0; i < 50 &&
                     NativeMethods.waveOutUnprepareHeader(handle, headerPtr, (uint)headerSize) == WAVERR_STILLPLAYING; i++)
                {
                    Thread.Sleep(5);
                }
            }

            if (headerPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(headerPtr);
            }
            if (pinned.IsAllocated)
            {
                pinned.Free();
            }
            NativeMethods.waveOutClose(handle);
        }
    }

    private static byte[] LoadClickPcm()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Yib.Resources.click.wav");
        if (stream is null)
        {
            return Array.Empty<byte>();
        }

        using var reader = new BinaryReader(stream);
        byte[] wav = reader.ReadBytes((int)stream.Length);

        // The embedded clip is a fixed-format 44-byte-header PCM WAV (see _wavgen) - skip
        // straight past RIFF/WAVE/fmt /data to the raw samples.
        return wav.Length > ClickWavHeaderSize ? wav[ClickWavHeaderSize..] : Array.Empty<byte>();
    }
}
