using SherpaOnnx;

namespace Vera.LocalAI.Voice;

/// <summary>
/// Потоковое распознавание на ПК (sherpa-onnx): звук подаётся кусками прямо с микрофона,
/// промежуточный текст доступен сразу, итог — через десятки миллисекунд после конца фразы.
/// Модель по содержимому папки: model.onnx — T-one (русский, 8 кГц), encoder*.int8.onnx — Zipformer (16 кГц).
/// Один распознаватель на модель, сессий (фраз) может быть сколько угодно.
/// </summary>
public sealed class StreamingSpeech : IDisposable
{
    private readonly OnlineRecognizer _rec;
    private readonly object _gate = new();

    public StreamingSpeech(string modelDir, int threads = 2)
    {
        if (!Directory.Exists(modelDir)) throw new DirectoryNotFoundException(modelDir);
        var cfg = new OnlineRecognizerConfig();
        cfg.FeatConfig.FeatureDim = 80;
        cfg.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        cfg.ModelConfig.NumThreads = Math.Clamp(threads, 1, 8);
        cfg.ModelConfig.Provider = "cpu";
        cfg.DecodingMethod = "greedy_search";
        var tone = Path.Combine(modelDir, "model.onnx");
        if (File.Exists(tone))
        {
            Kind = "t-one";
            cfg.FeatConfig.SampleRate = 8000;
            cfg.ModelConfig.ToneCtc.Model = tone;
        }
        else
        {
            Kind = "zipformer";
            cfg.FeatConfig.SampleRate = 16000;
            string Find(string part) => Directory.GetFiles(modelDir, part + "*.int8.onnx").FirstOrDefault()
                ?? Directory.GetFiles(modelDir, part + "*.onnx").FirstOrDefault()
                ?? throw new FileNotFoundException($"Нет файла {part}*.onnx в {modelDir}");
            cfg.ModelConfig.Transducer.Encoder = Find("encoder");
            cfg.ModelConfig.Transducer.Decoder = Find("decoder");
            cfg.ModelConfig.Transducer.Joiner = Find("joiner");
        }
        ModelDir = modelDir;
        _rec = new OnlineRecognizer(cfg);
    }

    /// <summary>"t-one" | "zipformer"</summary>
    public string Kind { get; }
    public string ModelDir { get; }

    public Session Start() => new(this);

    /// <summary>Распознать запись целиком (WAV из VoiceRecorder) — для активации по слову и проверок.</summary>
    public string Transcribe(float[] samples, int sampleRate)
    {
        using var s = Start();
        s.Feed(samples, sampleRate);
        return s.Finish();
    }

    public void Dispose() => _rec.Dispose();

    /// <summary>Одна фраза. Feed — из потока микрофона, Partial/Finish — откуда угодно.</summary>
    public sealed class Session : IDisposable
    {
        private readonly StreamingSpeech _owner;
        private readonly OnlineStream _stream;
        private readonly object _gate = new();
        private string _partial = "";
        private bool _finished;
        private int _sampleRate = 16000;

        internal Session(StreamingSpeech owner)
        {
            _owner = owner;
            _stream = owner._rec.CreateStream();
        }

        /// <summary>Текст на данный момент (уже нормализованный).</summary>
        public string Partial { get { lock (_gate) return _partial; } }

        /// <summary>Сработает после каждого изменения промежуточного текста (из потока Feed).</summary>
        public event Action<string>? PartialChanged;

        public void Feed(float[] samples, int sampleRate)
        {
            string? changed = null;
            lock (_gate)
            {
                if (_finished || samples.Length == 0) return;
                _sampleRate = sampleRate;
                _stream.AcceptWaveform(sampleRate, samples);
                lock (_owner._gate)
                    while (_owner._rec.IsReady(_stream)) _owner._rec.Decode(_stream);
                var p = Normalize(_owner._rec.GetResult(_stream).Text);
                if (p != _partial) changed = _partial = p;
            }
            if (changed is not null) PartialChanged?.Invoke(changed);
        }

        /// <summary>Конец фразы: добить тишиной (модели нужен правый контекст), получить итог.</summary>
        public string Finish()
        {
            lock (_gate)
            {
                if (_finished) return _partial;
                _finished = true;
                _stream.AcceptWaveform(_sampleRate, new float[_sampleRate * 3 / 10]);
                _stream.InputFinished();
                lock (_owner._gate)
                    while (_owner._rec.IsReady(_stream)) _owner._rec.Decode(_stream);
                return _partial = Normalize(_owner._rec.GetResult(_stream).Text);
            }
        }

        public void Dispose()
        {
            lock (_gate) _stream.Dispose();
        }
    }

    /// <summary>
    /// Модели отдают текст без пунктуации: T-one — строчными, Zipformer — ПРОПИСНЫМИ.
    /// Приводим к обычному виду: строчные, первая буква заглавная, английское «I» — заглавное.
    /// </summary>
    public static string Normalize(string? text)
    {
        var t = string.Join(' ', (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (t.Length == 0) return "";
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\bi\b", "I");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\bi'(m|ll|d|ve)\b", "I'$1");
        return char.ToUpperInvariant(t[0]) + t[1..];
    }
}
