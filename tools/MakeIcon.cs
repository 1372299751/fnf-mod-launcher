// 从源 ico 里取出最好的一帧，重新生成多尺寸 launcher.ico
// 用法：MakeIcon.exe <源ico> <输出ico> [帧尺寸(默认自动挑最大的有效帧)]

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

internal static class MakeIcon
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: MakeIcon <src.ico> <out.ico> [frameSize]");
            return 2;
        }
        string src = args[0], dst = args[1];
        int want = args.Length > 2 ? int.Parse(args[2]) : 0;

        byte[] data = File.ReadAllBytes(src);
        int count = BitConverter.ToUInt16(data, 4);

        Bitmap picked = null;
        int pickedSize = 0;
        for (int i = 0; i < count; i++)
        {
            int e = 6 + i * 16;
            int w = data[e]; if (w == 0) w = 256;
            int h = data[e + 1]; if (h == 0) h = 256;
            if (want > 0 && w != want) continue;
            if (want == 0 && w <= pickedSize) continue;
            try
            {
                Bitmap b = Decode(data, e, w, h);
                if (b == null) continue;
                if (picked != null) picked.Dispose();
                picked = b;
                pickedSize = w;
            }
            catch { }
        }
        if (picked == null) { Console.WriteLine("no usable frame"); return 1; }
        Console.WriteLine("source frame: " + pickedSize + "x" + pickedSize);

        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> images = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap b = Resize(picked, s)) images.Add(DibBytes(b));
        }

        using (FileStream fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s == 256 ? 0 : s));
                w.Write((byte)(s == 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] img in images) w.Write(img);
        }
        Console.WriteLine("ok: " + Path.GetFullPath(dst));
        return 0;
    }

    private static Bitmap Decode(byte[] d, int entry, int w, int h)
    {
        int len = BitConverter.ToInt32(d, entry + 8);
        int off = BitConverter.ToInt32(d, entry + 12);
        if (len <= 0 || off <= 0 || off + len > d.Length) return null;

        bool png = len > 8 && d[off] == 0x89 && d[off + 1] == 0x50;
        if (png)
        {
            using (MemoryStream ms = new MemoryStream(d, off, len))
                return new Bitmap(ms);
        }

        int hdr = BitConverter.ToInt32(d, off);
        int bw = BitConverter.ToInt32(d, off + 4);
        int bh = BitConverter.ToInt32(d, off + 8);
        int bpp = BitConverter.ToUInt16(d, off + 14);
        if (bpp != 32 || bw <= 0 || bh <= 0) return null;

        int rowBytes = bw * 4;
        int xorOff = off + hdr;
        if (xorOff + rowBytes * h > d.Length) return null;

        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                int srcRow = xorOff + (h - 1 - y) * rowBytes;
                Array.Copy(d, srcRow, row, 0, rowBytes);
                Marshal.Copy(row, 0, (IntPtr)((long)bd.Scan0 + (long)y * bd.Stride), rowBytes);
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    private static Bitmap Resize(Image src, int size)
    {
        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = src.Width < size ? InterpolationMode.HighQualityBicubic
                                                   : InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, size, size));
        }
        return b;
    }

    private static byte[] DibBytes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        byte[] xor = new byte[w * h * 4];

        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                IntPtr row = (IntPtr)((long)bd.Scan0 + (long)y * bd.Stride);
                Marshal.Copy(row, xor, (h - 1 - y) * w * 4, w * 4);
            }
        }
        finally { bmp.UnlockBits(bd); }

        int maskStride = ((w + 31) / 32) * 4;
        byte[] and = new byte[maskStride * h];

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(0);
            bw.Write(xor.Length + and.Length);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            bw.Write(xor);
            bw.Write(and);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
