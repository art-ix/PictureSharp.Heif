using PictureSharp;

internal static class QualityMetrics
{
    internal static double SsimDb(Image a, Image b)
    {
        var pa = new Rgba32[a.Width * a.Height];
        var pb = new Rgba32[b.Width * b.Height];
        a.CopyPixelDataTo(pa);
        b.CopyPixelDataTo(pb);
        static double Luma(Rgba32 p) => (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);
        const double c1 = 6.5025;
        const double c2 = 58.5225;
        double total = 0;
        int windows = 0;
        for (int y = 0; y + 8 <= a.Height; y += 4)
        {
            for (int x = 0; x + 8 <= a.Width; x += 4)
            {
                double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int j = 0; j < 8; j++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double va = Luma(pa[((y + j) * a.Width) + x + i]);
                        double vb = Luma(pb[((y + j) * a.Width) + x + i]);
                        sa += va;
                        sb += vb;
                        saa += va * va;
                        sbb += vb * vb;
                        sab += va * vb;
                    }
                }

                double ma = sa / 64, mb = sb / 64;
                double va2 = (saa / 64) - (ma * ma), vb2 = (sbb / 64) - (mb * mb), cov = (sab / 64) - (ma * mb);
                total += ((2 * ma * mb) + c1) * ((2 * cov) + c2) / (((ma * ma) + (mb * mb) + c1) * (va2 + vb2 + c2));
                windows++;
            }
        }

        return -10 * Math.Log10(1 - (total / windows));
    }

    internal static double Psnr(Image a, Image b, bool alphaChannel = false)
    {
        var pa = new Rgba32[a.Width * a.Height];
        var pb = new Rgba32[b.Width * b.Height];
        a.CopyPixelDataTo(pa);
        b.CopyPixelDataTo(pb);
        double error = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            if (alphaChannel)
            {
                error += Math.Pow(pa[i].A - pb[i].A, 2);
            }
            else
            {
                error += Math.Pow(pa[i].R - pb[i].R, 2) + Math.Pow(pa[i].G - pb[i].G, 2) + Math.Pow(pa[i].B - pb[i].B, 2);
            }
        }

        double mse = error / (pa.Length * (alphaChannel ? 1 : 3));
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255 * 255 / mse);
    }

}
