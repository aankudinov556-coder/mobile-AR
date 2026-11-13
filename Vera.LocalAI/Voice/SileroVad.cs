using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vera.Core.Speech;

namespace Vera.LocalAI.Voice;

/// <summary>
/// Нейросетевой детектор речи Silero VAD (ONNX, 2 МБ, MIT). Отличает голос от шума вентилятора, музыки и клавиатуры
/// лучше порога громкости. Подключается к <see cref="VoiceRecorder.SpeechProbability"/>.
/// Поддерживает модели v5 (input/state/sr) и v4 (input/h/c/sr).
/// </summary>
public sealed class SileroVad : IDisposable
{
    private const int Rate = 16000;
    private const int Window = 512;   // 32 мс
    private const int Context = 64;   // v5: к окну приклеиваются последние 64 отсчёта прошлого окна

    private readonly InferenceSession _session;
    private readonly bool _v5;
    private readonly object _gate = new();
    private float[] _state;
    private float[] _h, _c;
    private readonly float[] _context = new float[Context];
    private readonly List<float> _pending = new();
    private float _last;

    public SileroVad(string modelPath)
    {
        var o = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        o.AddSessionConfigEntry("session.intra_op.allow_spinning", "0"); // не жечь процессор в ожидании
        _session = new InferenceSession(modelPath, o);
        _v5 = _session.InputMetadata.ContainsKey("state");
        _state = new float[2 * 1 * 128];
        _h = new float[2 * 1 * 64];
        _c = new float[2 * 1 * 64];
    }

    /// <summary>Модель v5 (новая) или v4.</summary>
    public bool IsV5 => _v5;

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_state);
            Array.Clear(_h);
            Array.Clear(_c);
            Array.Clear(_context);
            _pending.Clear();
            _last = 0;
        }
    }

    /// <summary>
    /// Вероятность речи (0..1) для кадра микрофона любой частоты. Кадр копится до окна 32 мс;
    /// если окна ещё нет — возвращается прошлое значение. Подходит для VoiceRecorder.SpeechProbability.
    /// </summary>
    public float Probability(float[] samples, int sampleRate)
    {
        var s16 = sampleRate == Rate ? samples : WavEncoder.Downsample(samples, sampleRate, Rate);
        lock (_gate)
        {
            _pending.AddRange(s16);
            var max = -1f;
            while (_pending.Count >= Window)
            {
                var win = _pending.GetRange(0, Window).ToArray();
                _pending.RemoveRange(0, Window);
                var p = Run(win);
                if (p > max) max = p;
            }
            if (max >= 0) _last = max;
            return _last;
        }
    }

    /// <summary>Одно окно 512 отсчётов 16 кГц → вероятность речи.</summary>
    internal float Run(float[] window)
    {
        var sr = new DenseTensor<long>(new[] { (long)Rate }, Array.Empty<int>());
        if (_v5)
        {
            var x = new float[Context + Window];
            Array.Copy(_context, x, Context);
            Array.Copy(window, 0, x, Context, Window);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(x, new[] { 1, Context + Window })),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
                NamedOnnxValue.CreateFromTensor("sr", sr),
            };
            using var r = _session.Run(inputs);
            var prob = r.First(v => v.Name == "output").AsEnumerable<float>().First();
            _state = r.First(v => v.Name == "stateN").AsEnumerable<float>().ToArray();
            Array.Copy(x, x.Length - Context, _context, 0, Context);
            return prob;
        }
        else
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(window, new[] { 1, Window })),
                NamedOnnxValue.CreateFromTensor("sr", sr),
                NamedOnnxValue.CreateFromTensor("h", new DenseTensor<float>(_h, new[] { 2, 1, 64 })),
                NamedOnnxValue.CreateFromTensor("c", new DenseTensor<float>(_c, new[] { 2, 1, 64 })),
            };
            using var r = _session.Run(inputs);
            var prob = r.First(v => v.Name == "output").AsEnumerable<float>().First();
            _h = r.First(v => v.Name == "hn").AsEnumerable<float>().ToArray();
            _c = r.First(v => v.Name == "cn").AsEnumerable<float>().ToArray();
            return prob;
        }
    }

    public void Dispose() => _session.Dispose();
}
