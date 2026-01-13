using UnityEngine;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace VoiceRedirect;

public class VoiceTap : MonoBehaviour
{
    private float[] _ring;
    private int _writePos;
    private int _readPos;

    private volatile bool _stop;
    private Thread _senderThread;

    private string _playerName = "unknown";
    private bool _shouldRedirect;

    private int _channels;

    private string _pipeName;

    private const int SampleRate = 48000;
    private const int RingSamples = SampleRate * 2 * 2; // 2 seconds
    private const int ChunkSamples = 2048;

    private readonly short[] _pcm16 = new short[ChunkSamples];
    private readonly byte[] _pcmBytes = new byte[ChunkSamples * 2];

    private void Awake()
    {
        _ring = new float[RingSamples];

        var character = GetComponentInParent<Character>();
        
        _playerName = character.characterName;
        _shouldRedirect = Plugin.RedirectSet.Contains(_playerName);
        _pipeName = "peak_voicetap_" + Sanitize(_playerName);

        Plugin.Log.LogInfo($"VoiceTap: {_playerName}, redirect={_shouldRedirect}, pipe={_pipeName}");

        if (_shouldRedirect)
        {
            Plugin.EnsureHelperRunning(_pipeName, SampleRate, 1);
        }

        _stop = false;
        _senderThread = new Thread(SenderLoop) { IsBackground = true, Name = "VoiceTapSender_" + _playerName };
        _senderThread.Start();
    }

    private void OnDestroy()
    {
        _stop = true;
        try { _senderThread?.Join(200); }
        catch { }
    }

    // AUDIO thread
    private void OnAudioFilterRead(float[] data, int ch)
    {
        if (_channels == 0)
        {
            // publish channels once
            Interlocked.CompareExchange(ref _channels, ch, 0);
        }

        int n = data.Length;
        int cap = _ring.Length;

        int wp = _writePos;
        for (int i = 0; i < n; i++)
        {
            _ring[wp] = data[i];
            wp++;
            if (wp >= cap) wp = 0;
        }
        Thread.VolatileWrite(ref _writePos, wp);

        // mute in game output
        if (_shouldRedirect)
        {
            for (int i = 0; i < n; i++) data[i] = 0f;
        }
    }

    private void SenderLoop()
    {
        // Wait for audio
        while (!_stop && Thread.VolatileRead(ref _channels) == 0) Thread.Sleep(10);

        int ch = Thread.VolatileRead(ref _channels);
        if (ch <= 0) ch = 1;

        while (!_stop)
        {
            NamedPipeClientStream pipe = null;
            BinaryWriter bw = null;

            try
            {
                if (!_shouldRedirect) { Thread.Sleep(250); continue; }

                pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                pipe.Connect(1000);
                bw = new BinaryWriter(pipe);

                // simple header
                bw.Write(0x56544150); // 'VTAP'
                bw.Write(SampleRate);
                bw.Write(ch);
                bw.Flush();

                while (!_stop && pipe.IsConnected && _shouldRedirect)
                {
                    int avail = AvailableSamples();
                    if (avail <= 0) { Thread.Sleep(1); continue; }

                    int toRead = Math.Min(avail, ChunkSamples);
                    SendChunk(bw, toRead);
                }
            }
            catch
            {
                Thread.Sleep(250);
            }
            finally
            {
                try { bw?.Close(); } catch { }
                try { pipe?.Close(); } catch { }
            }
        }
    }

    private int AvailableSamples()
    {
        int cap = _ring.Length;
        int wp = Thread.VolatileRead(ref _writePos);
        int rp = Thread.VolatileRead(ref _readPos);

        if (wp >= rp) return wp - rp;
        return cap - rp + wp;
    }

    private void SendChunk(BinaryWriter bw, int sampleCount)
    {
        int cap = _ring.Length;
        int rp = Thread.VolatileRead(ref _readPos);

        for (int i = 0; i < sampleCount; i++)
        {
            float f = _ring[rp];
            rp++;
            if (rp >= cap) rp = 0;

            if (f > 1f) f = 1f;
            else if (f < -1f) f = -1f;

            _pcm16[i] = (short)Mathf.RoundToInt(f * 32767f);
        }
        Thread.VolatileWrite(ref _readPos, rp);

        int bytes = sampleCount * 2;
        Buffer.BlockCopy(_pcm16, 0, _pcmBytes, 0, bytes);

        bw.Write(bytes);
        bw.Write(_pcmBytes, 0, bytes);
        bw.Flush();
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
