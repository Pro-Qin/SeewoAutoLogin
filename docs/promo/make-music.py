# 生成 15 秒原创配乐（Apple 发布会风格：空灵 pad + 低频脉冲 + 清脆琶音 + 收尾一击）
# 纯标准库合成，无版权素材，可自由分发。
import math, struct, wave

SR = 44100
DUR = 15.2
N = int(SR * DUR)
L = [0.0] * N
R = [0.0] * N

def env(i, n, attack, decay, sustain=0.0, release=0.0):
    t = i / SR
    a = attack
    d = decay
    if t < a:
        return (t / a) ** 1.6
    if t < a + d:
        x = (t - a) / d
        return (1.0 - x) ** 2.0
    return 0.0

def tone(start, dur, freq, amp, attack=0.01, decay=None, pan=0.0, detune=0.0, harm=(1.0,)):
    if decay is None:
        decay = dur
    s0 = int(start * SR)
    n = int(dur * SR)
    gl = math.sqrt((1.0 - pan) / 2.0)
    gr = math.sqrt((1.0 + pan) / 2.0)
    for i in range(n):
        k = s0 + i
        if k >= N:
            break
        e = env(i, n, attack, decay)
        if e <= 0.0:
            continue
        t = i / SR
        v = 0.0
        for h, ha in enumerate(harm, start=1):
            f = freq * h + detune * h
            v += ha * math.sin(2 * math.pi * f * t)
        v *= e * amp
        L[k] += v * gl
        R[k] += v * gr

def noise_hit(start, dur, amp, pan=0.0, decay=0.18):
    s0 = int(start * SR)
    n = int(dur * SR)
    seed = 12345
    gl = math.sqrt((1.0 - pan) / 2.0)
    gr = math.sqrt((1.0 + pan) / 2.0)
    for i in range(n):
        k = s0 + i
        if k >= N:
            break
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2.0 - 1.0
        e = math.exp(-(i / SR) / decay)
        v = rnd * e * amp
        L[k] += v * gl
        R[k] += v * gr

# ---- 和声骨架：A 大三和弦（A2 / E3 / A3 / C#4）----
PAD = [110.0, 164.81, 220.0, 277.18]
for i, f in enumerate(PAD):
    tone(0.15 + i * 0.12, 13.4, f, 0.085, attack=1.6, decay=11.5,
         pan=(-0.45 + i * 0.3), detune=0.35, harm=(1.0, 0.35, 0.12))

# ---- 低频脉冲：每 2 拍一次，后面加密 ----
pulses = [2.6, 4.1, 5.6, 6.6, 7.4, 8.2, 8.9, 9.5, 10.05, 10.55, 11.0, 11.4, 11.75, 12.05, 12.35]
for p in pulses:
    tone(p, 0.32, 55.0, 0.30, attack=0.004, decay=0.30, harm=(1.0, 0.5, 0.2))
    tone(p, 0.16, 110.0, 0.10, attack=0.003, decay=0.15)

# ---- 清脆琶音：A 五声音阶，后半段变密 ----
ARP = [880.0, 1108.7, 1318.5, 1760.0, 1318.5, 1108.7]
seq = []
t = 5.0
step = 0.42
for i in range(34):
    seq.append((t, ARP[i % len(ARP)]))
    step = max(0.15, step - 0.008)
    t += step
for i, (ts, f) in enumerate(seq):
    tone(ts, 0.55, f, 0.075 + min(i, 18) * 0.0035, attack=0.004, decay=0.5,
         pan=(0.34 if i % 2 == 0 else -0.34), harm=(1.0, 0.22, 0.08))

# ---- 过渡噪声点（每次画面切换给一点空气感）----
for ts in [2.55, 4.95, 7.95, 10.95, 13.35]:
    noise_hit(ts, 0.5, 0.05, pan=0.2, decay=0.14)

# ---- 收尾一击：大和弦 + 长尾 ----
for f, a in [(220.0, 0.16), (277.18, 0.13), (329.63, 0.11), (440.0, 0.09), (554.37, 0.07)]:
    tone(13.45, 1.75, f, a, attack=0.006, decay=1.7, harm=(1.0, 0.4, 0.15))
tone(13.45, 1.2, 110.0, 0.28, attack=0.005, decay=1.15, harm=(1.0, 0.5))

# ---- 简易混响（梳状延迟 + 反馈）----
def reverb(sig, delays_ms, feedback, mix):
    out = sig[:]
    for d_ms in delays_ms:
        d = int(SR * d_ms / 1000.0)
        for i in range(d, len(sig)):
            out[i] += out[i - d] * feedback * mix
    return out

L = reverb(L, [83, 127, 191], 0.34, 0.30)
R = reverb(R, [89, 131, 197], 0.34, 0.30)

# ---- 淡出 + 归一化 ----
fade = int(SR * 0.5)
for i in range(fade):
    g = i / fade
    L[N - fade + i] *= g
    R[N - fade + i] *= g
peak = max(max(abs(v) for v in L), max(abs(v) for v in R)) or 1.0
scale = 0.89 / peak

frames = bytearray()
for i in range(N):
    l = int(max(-1.0, min(1.0, L[i] * scale)) * 32767)
    r = int(max(-1.0, min(1.0, R[i] * scale)) * 32767)
    frames += struct.pack('<hh', l, r)

with wave.open('music.wav', 'wb') as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(bytes(frames))

print('music.wav 生成完成：%.1f 秒, 峰值归一化 %.2f' % (N / SR, scale))