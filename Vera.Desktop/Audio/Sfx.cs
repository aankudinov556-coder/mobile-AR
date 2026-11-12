using System;
using NAudio.Wave;

namespace Vera.Desktop.Audio;

/// <summary>Короткие звуки, синтезированные синусами/треугольниками (как в Electron-версии).</summary>
public static class Sfx
{
    private enum Wave { Sine, Triangle, Square }

    public static void ListenStart() => Play(new[] { (660.0, 0.09, Wave.Triangle), (990.0, 0.12, Wave.Triangle) }, 0.12);
    public static void ListenStop() => Play(new[] { (880.0, 0.08, Wave.Triangle), (587.0, 0.12, Wave.Triangle) }, 0.10);
    public static void Reminder() => Play(new[]
    {
        (784.0, 0.16, Wave.Sine), (988.0, 0.16, Wave.Sine), (1175.0, 0.28, Wave.Sine), (988.0, 0.14, Wave.Sine), (1319.0, 0.40, Wave.Sine),
    }, 0.18);
    public static void Pop() => Play(new[] { (520.0, 0.06, Wave.Square), (780.0, 0.07, Wave.Square) }, 0.05);
    public static void Error() => Play(new[] { (330.0, 0.12, Wave.Triangle), (262.0, 0.20, Wave.Triangle) }, 0.10);

    private static float[] Render((double Freq, double Dur, Wave Type)[] seq, double volume, int sr)
    {
        // ноты накладываются: следующая начинается на 80% длительности предыдущей
        var t = 0.02;
        var total = t;
        foreach (var (_, d, _) in seq)
        {
            total = Math.Max(total, t + d + 0.03);
            t += d * 0.8;
        }
        var buf = new float[(int)(total * sr) + sr / 20];
        t = 0.02;
        foreach (var (f, d, type) in seq)
        {
            var start = (int)(t * sr);
            var len = (int)((d + 0.02) * sr);
            const double v0 = 0.0001;
            for (var i = 0; i < len && start + i < buf.Length; i++)
            {
                var x = i / (double)sr;
                double g;
                if (x < 0.015) g = v0 * Math.Pow(volume / v0, x / 0.015);
                else if (x < d) g = volume * Math.Pow(v0 / volume, (x - 0.015) / Math.Max(0.001, d - 0.015));
                else g = v0;
                var ph = 2 * Math.PI * f * x;
                var s = type switch
                {
                    Wave.Sine => Math.Sin(ph),
                    Wave.Square => Math.Sign(Math.Sin(ph)),
                    _ => 2 / Math.PI * Math.Asin(Math.Sin(ph)),
                };
                buf[start + i] += (float)(s * g);
            }
            t += d * 0.8;
        }
        return buf;
    }

    private static void Play((double, double, Wave)[] seq, double volume)
    {
        try
        {
            const int sr = 44100;
            var samples = Render(seq, volume, sr);
            var provider = new ArraySampleProvider(samples, sr, 1);
            var output = new WaveOutEvent { DesiredLatency = 120 };
            output.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider16(provider));
            output.PlaybackStopped += (_, _) => output.Dispose();
            output.Play();
        }
        catch (Exception)
        {
            // нет устройства вывода — молча
        }
    }
}
