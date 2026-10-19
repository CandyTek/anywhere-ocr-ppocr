using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AnywhereOcr;

/// <summary>
/// 8-bit interleaved BGR image (same memory layout as an OpenCV CV_8UC3 Mat),
/// so that every preprocessing step can mirror PaddleOCR's numpy/cv2 code.
/// </summary>
public sealed class BgrImage
{
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Data;

    public BgrImage(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new byte[checked(width * height * 3)];
    }

    public static BgrImage Load(string path)
    {
        using var bmp = new Bitmap(path);
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var img = new BgrImage(bmp.Width, bmp.Height);
            int rowBytes = bmp.Width * 3;
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(bd.Scan0 + y * bd.Stride, img.Data, y * rowBytes, rowBytes);
            return img;
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    /// <summary>Zero-pads the image to at least the given size (DetResizeForTest.image_padding).</summary>
    public BgrImage PadTo(int minWidth, int minHeight)
    {
        int w = Math.Max(Width, minWidth), h = Math.Max(Height, minHeight);
        if (w == Width && h == Height) return this;
        var dst = new BgrImage(w, h);
        for (int y = 0; y < Height; y++)
            Buffer.BlockCopy(Data, y * Width * 3, dst.Data, y * w * 3, Width * 3);
        return dst;
    }

    /// <summary>cv2.resize(..., interpolation=INTER_LINEAR) equivalent.</summary>
    public BgrImage ResizeLinear(int dstWidth, int dstHeight)
    {
        if (dstWidth == Width && dstHeight == Height) return this;
        var dst = new BgrImage(dstWidth, dstHeight);
        double sx = (double)Width / dstWidth, sy = (double)Height / dstHeight;

        var x0 = new int[dstWidth]; var x1 = new int[dstWidth]; var wx = new float[dstWidth];
        for (int x = 0; x < dstWidth; x++)
        {
            double fx = (x + 0.5) * sx - 0.5;
            if (fx < 0) fx = 0;
            int ix = (int)Math.Floor(fx);
            double d = fx - ix;
            if (ix >= Width - 1) { ix = Width - 1; d = 0; }
            x0[x] = ix; x1[x] = Math.Min(ix + 1, Width - 1); wx[x] = (float)d;
        }
        var y0 = new int[dstHeight]; var y1 = new int[dstHeight]; var wy = new float[dstHeight];
        for (int y = 0; y < dstHeight; y++)
        {
            double fy = (y + 0.5) * sy - 0.5;
            if (fy < 0) fy = 0;
            int iy = (int)Math.Floor(fy);
            double d = fy - iy;
            if (iy >= Height - 1) { iy = Height - 1; d = 0; }
            y0[y] = iy; y1[y] = Math.Min(iy + 1, Height - 1); wy[y] = (float)d;
        }

        byte[] src = Data, outp = dst.Data;
        int srcStride = Width * 3, dstStride = dstWidth * 3;
        for (int y = 0; y < dstHeight; y++)
        {
            int r0 = y0[y] * srcStride, r1 = y1[y] * srcStride;
            float dy = wy[y], dy1 = 1f - dy;
            int o = y * dstStride;
            for (int x = 0; x < dstWidth; x++)
            {
                int c0 = x0[x] * 3, c1 = x1[x] * 3;
                float dx = wx[x], dx1 = 1f - dx;
                for (int c = 0; c < 3; c++)
                {
                    float top = src[r0 + c0 + c] * dx1 + src[r0 + c1 + c] * dx;
                    float bot = src[r1 + c0 + c] * dx1 + src[r1 + c1 + c] * dx;
                    float v = top * dy1 + bot * dy;
                    outp[o + x * 3 + c] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>np.rot90 equivalent: rotate 90 degrees counter-clockwise.</summary>
    public BgrImage Rot90()
    {
        var dst = new BgrImage(Height, Width);
        int w = Width, h = Height;
        for (int i = 0; i < w; i++)          // destination row
        {
            int srcCol = w - 1 - i;
            for (int j = 0; j < h; j++)      // destination column
            {
                int s = (j * w + srcCol) * 3, d = (i * h + j) * 3;
                dst.Data[d] = Data[s]; dst.Data[d + 1] = Data[s + 1]; dst.Data[d + 2] = Data[s + 2];
            }
        }
        return dst;
    }

    /// <summary>
    /// cv2.warpPerspective(img, M, (w, h), borderMode=BORDER_REPLICATE, flags=INTER_CUBIC)
    /// where <paramref name="inverse"/> is M^-1 (destination to source mapping, row-major 3x3).
    /// </summary>
    public BgrImage WarpPerspectiveCubic(double[] inverse, int dstWidth, int dstHeight)
    {
        var dst = new BgrImage(dstWidth, dstHeight);
        int w = Width, h = Height;
        byte[] src = Data;
        Span<float> cx = stackalloc float[4];
        Span<float> cy = stackalloc float[4];
        for (int y = 0; y < dstHeight; y++)
        {
            for (int x = 0; x < dstWidth; x++)
            {
                double X = inverse[0] * x + inverse[1] * y + inverse[2];
                double Y = inverse[3] * x + inverse[4] * y + inverse[5];
                double W = inverse[6] * x + inverse[7] * y + inverse[8];
                W = W != 0 ? 1.0 / W : 0;
                double sx = X * W, sy = Y * W;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy);
                CubicWeights((float)(sx - ix), cx);
                CubicWeights((float)(sy - iy), cy);
                int o = (y * dstWidth + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    float sum = 0;
                    for (int m = 0; m < 4; m++)
                    {
                        int yy = Math.Clamp(iy - 1 + m, 0, h - 1);
                        int row = yy * w;
                        float rs = 0;
                        for (int n = 0; n < 4; n++)
                        {
                            int xx = Math.Clamp(ix - 1 + n, 0, w - 1);
                            rs += cx[n] * src[(row + xx) * 3 + c];
                        }
                        sum += cy[m] * rs;
                    }
                    dst.Data[o + c] = (byte)Math.Clamp((int)(sum + 0.5f), 0, 255);
                }
            }
        }
        return dst;
    }

    // OpenCV's bicubic kernel (A = -0.75).
    private static void CubicWeights(float t, Span<float> w)
    {
        const float A = -0.75f;
        w[0] = ((A * (t + 1) - 5 * A) * (t + 1) + 8 * A) * (t + 1) - 4 * A;
        w[1] = ((A + 2) * t - (A + 3)) * t * t + 1;
        w[2] = ((A + 2) * (1 - t) - (A + 3)) * (1 - t) * (1 - t) + 1;
        w[3] = 1f - w[0] - w[1] - w[2];
    }
}
