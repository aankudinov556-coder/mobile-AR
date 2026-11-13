#!/usr/bin/env python3
# Использование: pip install gguf numpy; python tools/make_tiny_gguf.py ggml-vocab-qwen2.gguf tiny.gguf
# Словарь: https://raw.githubusercontent.com/ggml-org/llama.cpp/master/models/ggml-vocab-qwen2.gguf
# Затем: VERA_TINY_GGUF=tiny.gguf dotnet test Vera.LocalAI.Tests
# Крошечная qwen2-модель со случайными весами поверх настоящего словаря — только для проверки движка.
import sys, numpy as np, gguf
src, dst = sys.argv[1], sys.argv[2]
r = gguf.GGUFReader(src)
w = gguf.GGUFWriter(dst, "qwen2")
n_vocab = None
for k, f in r.fields.items():
    if k.startswith("GGUF.") or k == "general.architecture": continue
    if not (k.startswith("tokenizer.") or k.startswith("general.")): continue
    t = f.types[0]
    if t == gguf.GGUFValueType.ARRAY:
        sub = f.types[1]
        if sub == gguf.GGUFValueType.STRING:
            vals = [bytes(f.parts[i]).decode("utf-8", "replace") for i in f.data]
        else:
            vals = [f.parts[i].tolist()[0] for i in f.data]
        if k == "tokenizer.ggml.tokens": n_vocab = len(vals)
        w.add_key_value(k, vals, t, sub_type=sub)
    elif t == gguf.GGUFValueType.STRING:
        w.add_key_value(k, bytes(f.parts[f.data[0]]).decode("utf-8", "replace"), t)
    else:
        w.add_key_value(k, f.parts[f.data[0]].tolist()[0], t)
E, L, H, KV, FF, CTX = 64, 2, 4, 2, 128, 4096
w.add_context_length(CTX); w.add_embedding_length(E); w.add_block_count(L)
w.add_feed_forward_length(FF); w.add_head_count(H); w.add_head_count_kv(KV)
w.add_layer_norm_rms_eps(1e-6); w.add_rope_freq_base(10000.0)
if "tokenizer.chat_template" not in r.fields:
    w.add_chat_template("{% for message in messages %}{{'<|im_start|>' + message['role'] + '\n' + message['content'] + '<|im_end|>' + '\n'}}{% endfor %}{% if add_generation_prompt %}{{ '<|im_start|>assistant\n' }}{% endif %}")
rng = np.random.default_rng(0)
def T(name, *shape): w.add_tensor(name, (rng.standard_normal(shape) * 0.02).astype(np.float32))
hd = E // H
T("token_embd.weight", n_vocab, E)
for i in range(L):
    w.add_tensor(f"blk.{i}.attn_norm.weight", np.ones(E, np.float32))
    T(f"blk.{i}.attn_q.weight", E, E); T(f"blk.{i}.attn_q.bias", E)
    T(f"blk.{i}.attn_k.weight", KV*hd, E); T(f"blk.{i}.attn_k.bias", KV*hd)
    T(f"blk.{i}.attn_v.weight", KV*hd, E); T(f"blk.{i}.attn_v.bias", KV*hd)
    T(f"blk.{i}.attn_output.weight", E, E)
    w.add_tensor(f"blk.{i}.ffn_norm.weight", np.ones(E, np.float32))
    T(f"blk.{i}.ffn_gate.weight", FF, E); T(f"blk.{i}.ffn_up.weight", FF, E); T(f"blk.{i}.ffn_down.weight", E, FF)
w.add_tensor("output_norm.weight", np.ones(E, np.float32))
T("output.weight", n_vocab, E)
w.write_header_to_file(); w.write_kv_data_to_file(); w.write_tensors_to_file(); w.close()
print("vocab", n_vocab, "template" , "tokenizer.chat_template" in r.fields)
