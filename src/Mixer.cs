// 试听：把伴奏 + 人声预混成一个 WAV（缓存），再用系统默认播放器打开
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal static class Mixer
    {
        private const string Dll = "libvlc.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_new(int argc, IntPtr argv);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_media_new_path(IntPtr inst, [MarshalAs(UnmanagedType.LPStr)] string path);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_add_option(IntPtr media, [MarshalAs(UnmanagedType.LPStr)] string option);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr libvlc_media_player_new_from_media(IntPtr media);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_play(IntPtr mp);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_stop(IntPtr mp);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_player_release(IntPtr mp);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void libvlc_media_release(IntPtr media);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_get_state(IntPtr media);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int libvlc_media_player_get_state(IntPtr mp);

        private static IntPtr inst = IntPtr.Zero;
        public static string LastError = "";

        public static string CacheDir
        {
            get { return Path.Combine(Path.GetTempPath(), "fnf-launcher", "mix"); }
        }

        private static bool EnsureInit(string dir)
        {
            if (inst != IntPtr.Zero) return true;
            try
            {
                if (string.IsNullOrEmpty(dir)) return false;
                string dll = Path.Combine(dir, "libvlc.dll");
                if (!File.Exists(dll)) { LastError = "mod 目录里没有 libvlc.dll，无法混音"; return false; }
                SetDllDirectory(dir);
                if (LoadLibrary(dll) == IntPtr.Zero) { LastError = "加载 libvlc.dll 失败"; return false; }

                string[] keepSrc = { "--no-video", "--quiet", "--no-sub-autodetect-file" };
                IntPtr[] keep = new IntPtr[keepSrc.Length];
                try
                {
                    for (int i = 0; i < keepSrc.Length; i++) keep[i] = Marshal.StringToHGlobalAnsi(keepSrc[i]);
                    IntPtr arr = Marshal.AllocHGlobal(IntPtr.Size * keep.Length);
                    for (int i = 0; i < keep.Length; i++) Marshal.WriteIntPtr(arr, i * IntPtr.Size, keep[i]);
                    inst = libvlc_new(keep.Length, arr);
                    Marshal.FreeHGlobal(arr);
                }
                finally
                {
                    foreach (IntPtr p in keep) { try { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); } catch { } }
                }
                if (inst == IntPtr.Zero) { LastError = "libvlc 初始化失败"; return false; }
                return true;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        // 用一个临时 WAV 让 libvlc 只做解码，不做播放（一次性，无生命周期问题）
        private static string Transcode(string src, string dir, string tag)
        {
            string outWav = Path.Combine(CacheDir, "tmp_" + tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
            try
            {
                IntPtr media = libvlc_media_new_path(inst, src);
                if (media == IntPtr.Zero) return null;
                string sout = ":sout=#transcode{acodec=s16l,channels=2,samplerate=44100}:std{access=file,mux=wav,dst='" + outWav + "'}";
                libvlc_media_add_option(media, sout);
                libvlc_media_add_option(media, ":no-sout-rtp-sap");
                IntPtr mp = libvlc_media_player_new_from_media(media);
                if (mp == IntPtr.Zero) { libvlc_media_release(media); return null; }
                libvlc_media_player_play(mp);

                // 等到播放结束（最多 120 秒）
                for (int i = 0; i < 1200; i++)
                {
                    System.Threading.Thread.Sleep(100);
                    int st = libvlc_media_player_get_state(mp);
                    if (st == 5 || st == 6) break;          // Stopped / Ended
                    if (st == 7) { LastError = "转码出错"; break; }   // Error
                }
                libvlc_media_player_stop(mp);
                System.Threading.Thread.Sleep(120);
                libvlc_media_player_release(mp);
                libvlc_media_release(media);
                if (File.Exists(outWav)) return outWav;
            }
            catch (Exception ex) { LastError = ex.Message; }
            return null;
        }

        private static byte[] ReadPcm(string wav, out int channels, out int rate, out int bits)
        {
            channels = 2; rate = 44100; bits = 16;
            byte[] all = File.ReadAllBytes(wav);
            int pos = 12, dataOff = -1, dataLen = 0;
            while (pos + 8 <= all.Length)
            {
                string id = "" + (char)all[pos] + (char)all[pos + 1] + (char)all[pos + 2] + (char)all[pos + 3];
                int size = BitConverter.ToInt32(all, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(all, pos + 10);
                    rate = BitConverter.ToInt32(all, pos + 12);
                    bits = BitConverter.ToInt16(all, pos + 22);
                }
                else if (id == "data")
                {
                    dataOff = pos + 8;
                    dataLen = Math.Min(size, all.Length - dataOff);
                    break;
                }
                pos += 8 + size + (size & 1);
            }
            if (dataOff < 0 || dataLen <= 0) { channels = 2; rate = 44100; bits = 16; return new byte[0]; }
            byte[] pcm = new byte[dataLen];
            Array.Copy(all, dataOff, pcm, 0, dataLen);
            return pcm;
        }

        private static void WriteWav(string path, byte[] pcm, int channels, int rate, int bits)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                int blockAlign = channels * bits / 8;
                w.Write(new char[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + pcm.Length);
                w.Write(new char[] { 'W', 'A', 'V', 'E' });
                w.Write(new char[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * blockAlign);
                w.Write((short)blockAlign);
                w.Write((short)bits);
                w.Write(new char[] { 'd', 'a', 't', 'a' });
                w.Write(pcm.Length);
                w.Write(pcm);
            }
        }

        private static string Safe(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
            string r = sb.ToString();
            return r.Length > 40 ? r.Substring(0, 40) : (r.Length == 0 ? "song" : r);
        }

        // 混音（伴奏 + 人声），返回单文件路径
        public static string MixBoth(SongInfo song, string dir)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                long t = 0;
                try { t = new FileInfo(song.Inst).LastWriteTimeUtc.Ticks; } catch { }
                string outPath = Path.Combine(CacheDir, Safe(song.Name) + "_" + t + "_" + (song.HasVoices ? "v" : "i") + ".wav");
                if (File.Exists(outPath) && new FileInfo(outPath).Length > 1000) return outPath;

                if (!EnsureInit(dir)) return null;

                string wavA = Transcode(song.Inst, dir, "a");
                if (wavA == null) { LastError = LastError.Length > 0 ? LastError : "伴奏转码失败"; return null; }
                string wavB = null;
                if (song.HasVoices) wavB = Transcode(song.Voices, dir, "b");

                int chA, rateA, bitsA, chB = 0, rateB = 0, bitsB = 0;
                byte[] pcmA = ReadPcm(wavA, out chA, out rateA, out bitsA);
                byte[] pcmB = wavB != null ? ReadPcm(wavB, out chB, out rateB, out bitsB) : null;

                bool canMix = pcmB != null && pcmB.Length > 0 && chA == chB && rateA == rateB && bitsA == bitsB && bitsA == 16;
                int len = pcmA.Length;
                if (canMix && pcmB.Length > len) len = pcmB.Length;
                len = (len / 2) * 2;

                byte[] mix = new byte[len];
                for (int i = 0; i + 1 < pcmA.Length && i + 1 < len; i += 2)
                {
                    short s = BitConverter.ToInt16(pcmA, i);
                    mix[i] = (byte)(s & 0xFF);
                    mix[i + 1] = (byte)((s >> 8) & 0xFF);
                }
                if (canMix)
                {
                    for (int i = 0; i + 1 < pcmB.Length && i + 1 < len; i += 2)
                    {
                        int a = BitConverter.ToInt16(mix, i);
                        int b = BitConverter.ToInt16(pcmB, i);
                        int v = a + b;
                        if (v > 32767) v = 32767;
                        if (v < -32768) v = -32768;
                        short sv = (short)v;
                        mix[i] = (byte)(sv & 0xFF);
                        mix[i + 1] = (byte)((sv >> 8) & 0xFF);
                    }
                }

                WriteWav(outPath, mix, chA, rateA, bitsA);
                try { File.Delete(wavA); } catch { }
                if (wavB != null) { try { File.Delete(wavB); } catch { } }
                return outPath;
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
        }

        public static void OpenWithSystem(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { LastError = ex.Message; }
        }

        public static void ClearCache()
        {
            try
            {
                if (Directory.Exists(CacheDir))
                    foreach (string f in Directory.GetFiles(CacheDir, "*.wav")) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        public static long CacheSize()
        {
            long total = 0;
            try
            {
                if (Directory.Exists(CacheDir))
                    foreach (string f in Directory.GetFiles(CacheDir, "*.wav")) { try { total += new FileInfo(f).Length; } catch { } }
            }
            catch { }
            return total;
        }
    }
}
