using System.Text;

namespace Vera.LocalAI.Llm;

/// <summary>
/// Прячет из потока текста служебные блоки локальной модели: размышления &lt;think&gt;…&lt;/think&gt; и вызовы
/// &lt;tool_call&gt;…&lt;/tool_call&gt;. Хвост, похожий на начало тега, придерживается до следующего кусочка.
/// </summary>
public sealed class LocalStreamFilter
{
    private static readonly (string Open, string Close)[] Blocks =
    {
        ("<think>", "</think>"),
        ("<tool_call>", "</tool_call>"),
    };

    private readonly StringBuilder _buf = new();
    private int _inside = -1;

    /// <summary>Был ли в потоке хотя бы один вызов инструмента.</summary>
    public bool SawToolCall { get; private set; }

    public string Push(string piece)
    {
        _buf.Append(piece);
        var outText = new StringBuilder();
        while (true)
        {
            var s = _buf.ToString();
            if (_inside >= 0)
            {
                var close = Blocks[_inside].Close;
                var i = s.IndexOf(close, StringComparison.Ordinal);
                if (i < 0)
                {
                    // держим только возможное начало закрывающего тега
                    var keep = Partial(s, close);
                    _buf.Clear().Append(s[^keep..]);
                    break;
                }
                _buf.Clear().Append(s[(i + close.Length)..]);
                _inside = -1;
                continue;
            }
            var best = -1; var at = int.MaxValue;
            for (var b = 0; b < Blocks.Length; b++)
            {
                var i = s.IndexOf(Blocks[b].Open, StringComparison.Ordinal);
                if (i >= 0 && i < at) { at = i; best = b; }
            }
            if (best >= 0)
            {
                outText.Append(s[..at]);
                _buf.Clear().Append(s[(at + Blocks[best].Open.Length)..]);
                _inside = best;
                if (Blocks[best].Open == "<tool_call>") SawToolCall = true;
                continue;
            }
            var hold = Blocks.Max(bl => Partial(s, bl.Open));
            outText.Append(s[..^hold]);
            _buf.Clear().Append(s[^hold..]);
            break;
        }
        return outText.ToString();
    }

    /// <summary>Конец потока: незакрытый блок выбрасывается, обычный хвост отдаётся.</summary>
    public string Flush()
    {
        var s = _inside >= 0 ? "" : _buf.ToString();
        _buf.Clear();
        _inside = -1;
        return s;
    }

    private static int Partial(string s, string tag)
    {
        for (var k = Math.Min(tag.Length - 1, s.Length); k > 0; k--)
            if (s.EndsWith(tag[..k], StringComparison.Ordinal)) return k;
        return 0;
    }
}
