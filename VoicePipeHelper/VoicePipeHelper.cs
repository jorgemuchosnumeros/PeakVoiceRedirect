using System;
using System.IO;
using System.IO.Pipes;
using NAudio.Wave;

namespace VoicePipeHelper;

class VoicePipeHelper
{
    static void Main(string[] args)
    {
        string pipe = GetArg(args, "--pipe", "peak_voicetap_unknown");
        int rate = int.Parse(GetArg(args, "--rate", "48000"));
        int ch = int.Parse(GetArg(args, "--ch", "1"));
        
        int ppid = int.Parse(GetArg(args, key: "--ppid", def: "0"));
        if (ppid > 0) StartParentWatchdog(ppid);

        using (var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte))
        {
            server.WaitForConnection();
            var br = new BinaryReader(server);
            
            int sr = br.ReadInt32();
            int cc = br.ReadInt32();
            if (sr > 0) rate = sr;
            if (cc > 0) ch = cc;

            var format = new WaveFormat(rate, 16, ch);
            var buffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };

            using (var output = new WaveOutEvent())
            {
                output.Init(buffer);
                output.Play();

                while (server.IsConnected)
                {
                    int byteCount;
                    try { byteCount = br.ReadInt32(); }
                    catch { break; }

                    var data = br.ReadBytes(byteCount);
                    if (data.Length == 0) break;

                    buffer.AddSamples(data, 0, data.Length);
                }
            }
        }
    }

    static string GetArg(string[] args, string key, string def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return def;
    }
    
    static void StartParentWatchdog(int ppid)
    {
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById(ppid);
                    if (p.HasExited) Environment.Exit(0);
                }
                catch
                {
                    Environment.Exit(0);
                }
                System.Threading.Thread.Sleep(500);
            }
        });
        t.IsBackground = true;
        t.Start();
    }
}