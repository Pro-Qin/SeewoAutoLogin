# v4：宏伟 / 电影感（BPM 140，30 拍 + 短尾音 = 约 13.8 秒）
# 相比 v3 的改动：去掉密集电子 hi-hat，换成弦乐式 pad + 大鼓 + 上升旋律 + 长混响，
# 和弦走 D → A → Bm → G 的宏大进行，结尾全奏一击。
import math, struct, wave, json

SR = 44100
BPM = 140.0
BEAT = 60.0 / BPM
BARS = 7.5                     # 30 拍
TAIL = 1.0                     # 尾音（比原来短很多，解决「结尾拖沓」）
BODY = 30 * BEAT               # 12.857s
DUR = BODY + TAIL
N = int(SR * DUR)

def p(v): return math.sqrt((1 - v) / 2), math.sqrt((1 + v) / 2)
L = [0.0] * N; R = [0.0] * N
def put(k, vl, vr):
    if 0 <= k < N: L[k] += vl; R[k] += vr

# ---------- 音色 ----------
def strings(t0, dur, freq, amp, panv=0.0, attack=1.1, rel=1.0):
    """弦乐式 pad：多个谐波 + 慢起音，宏伟感的主要来源"""
    gl, gr = p(panv)
    s0, n = int(t0 * SR), int(dur * SR)
    harm = (1.0, 0.5, 0.33, 0.22, 0.14, 0.09)
    for i in range(n):
        t = i / SR
        if t < attack:
            e = (t / attack) ** 1.4
        else:
            e = math.exp(-(t - attack) / max(0.4, rel))
        v = 0.0
        for h, ha in enumerate(harm, start=1):
            # 轻微失谐让声音更厚
            v += ha * math.sin(2 * math.pi * freq * h * t + 0.06 * h)
        v *= e * amp / 3.0
        put(s0 + i, v * gl, v * gr)

def bigdrum(t0, amp=1.0):
    """电影大鼓：低频下坠 + 长衰减"""
    s0, n = int(t0 * SR), int(0.85 * SR)
    ph = 0.0
    for i in range(n):
        t = i / SR
        f = 40.0 + 120.0 * math.exp(-t / 0.035)
        ph += 2 * math.pi * f / SR
        e = math.exp(-t / 0.26)
        v = math.sin(ph) * e * amp
        put(s0 + i, v, v)

def cymbal(t0, dur=1.6, amp=0.16, panv=0.0):
    """镲片 / 噪声 swell：和弦切换时给出空间感"""
    gl, gr = p(panv)
    s0, n = int(t0 * SR), int(dur * SR)
    seed = 99991 + int(t0 * 733)
    prev = 0.0
    for i in range(n):
        t = i / SR; x = t / dur
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2 - 1
        hp = rnd - prev; prev = rnd
        e = math.exp(-t / (dur * 0.42)) * (1 - math.exp(-t / 0.008))
        v = hp * e * amp
        put(s0 + i, v * gl, v * gr)

def sub(t0, dur, freq, amp=0.26):
    s0, n = int(t0 * SR), int(dur * SR)
    for i in range(n):
        t = i / SR
        a = 0.02
        e = (t / a) if t < a else math.exp(-(t - a) / (dur * 0.7))
        put(s0 + i, math.sin(2 * math.pi * freq * t) * e * amp, math.sin(2 * math.pi * freq * t) * e * amp)

def lead(t0, freq, amp=0.15, dur=0.9, panv=0.0):
    """主旋律：明亮、带点金属光泽"""
    gl, gr = p(panv)
    s0, n = int(t0 * SR), int(dur * SR)
    for i in range(n):
        t = i / SR
        e = math.exp(-t / 0.28) * (1 - math.exp(-t / 0.012))
        v = (math.sin(2 * math.pi * freq * t) + 0.34 * math.sin(4 * math.pi * freq * t)
             + 0.12 * math.sin(6 * math.pi * freq * t)) * e * amp
        put(s0 + i, v * gl, v * gr)

def riser(t0, dur, amp=0.2):
    s0, n = int(t0 * SR), int(dur * SR)
    seed = 555
    prev = 0.0
    for i in range(n):
        t = i / SR; x = t / dur
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2 - 1
        hp = rnd - prev; prev = rnd
        put(s0 + i, hp * (x ** 2.2) * amp, hp * (x ** 2.2) * amp)

def impact(t0, amp=1.0, dur=2.2):
    s0, n = int(t0 * SR), int(dur * SR)
    for i in range(n):
        t = i / SR
        e = math.exp(-t / 0.7)
        v = 0.5 * math.sin(2 * math.pi * (70 * math.exp(-t / 0.07) + 37) * t) * e * amp
        put(s0 + i, v, v)

# ---------- 和声：D → A → Bm → G（每 8 拍一个和弦）----------
CHORDS = [
    (0,  [146.83, 220.00, 293.66, 369.99]),   # D
    (8,  [110.00, 164.81, 220.00, 277.18]),   # A
    (16, [123.47, 185.00, 246.94, 293.66]),   # Bm
    (24, [98.00,  146.83, 196.00, 246.94]),   # G
]
for start_beat, freqs in CHORDS:
    t0 = start_beat * BEAT
    dur = (8 if start_beat < 24 else 6) * BEAT + 1.0
    for i, f in enumerate(freqs):
        strings(t0, dur, f, 0.30, panv=(-0.5 + i * 0.33), attack=0.9, rel=1.4)
        strings(t0, dur, f * 2, 0.10, panv=(0.45 - i * 0.28), attack=1.3, rel=1.2)
    # 低音根音
    sub(t0, 8 * BEAT, freqs[0] / 2, 0.30)
    cymbal(t0, 1.9, 0.14, panv=0.25)

# ---------- 节奏：庄重的大鼓（每小节第 1、3 拍），不做电子密集鼓 ----------
for bar in range(8):
    t = bar * 4 * BEAT
    if t >= BODY: break
    bigdrum(t, 0.85 if bar >= 2 else 0.6)
    if bar >= 2:
        bigdrum(t + 2 * BEAT, 0.5)
    if bar >= 4:
        bigdrum(t + 3 * BEAT, 0.32)
    if bar >= 6:
        bigdrum(t + BEAT, 0.28)

# ---------- 上升旋律：D 大调音阶，后半段加密上行 ----------
DMAJ = [293.66, 329.63, 369.99, 440.00, 493.88, 554.37, 587.33, 659.25]
for i in range(16):                       # 8-16 拍：舒缓的上行
    t = (8 + i * 0.5) * BEAT
    lead(t, DMAJ[i % 5], 0.11, panv=-0.3 if i % 2 else 0.3)
for i in range(18):                       # 16-25 拍：加密、音区更高
    t = (16 + i * 0.5) * BEAT
    lead(t, DMAJ[(i * 2) % len(DMAJ)] * 2, 0.13, panv=0.35 if i % 2 else -0.35)
for i in range(8):                        # 25-29 拍：冲向结尾
    t = (25 + i * 0.5) * BEAT
    lead(t, DMAJ[i % len(DMAJ)] * 2, 0.15)

# ---------- 铺垫与收尾 ----------
riser(4 * BEAT, 4 * BEAT, 0.16)
riser(22 * BEAT, 3 * BEAT, 0.2)
impact(25 * BEAT, 0.9)
bigdrum(25 * BEAT, 1.0)
cymbal(25 * BEAT, 2.4, 0.2)
# 结尾大和弦（全奏 + 长尾）
for f in (146.83, 220.00, 293.66, 369.99, 440.00, 587.33):
    strings(25 * BEAT, 1.4, f, 0.42, attack=0.02, rel=1.3)
sub(25 * BEAT, 1.3, 73.42, 0.34)

# ---------- 混响（更大的空间）----------
def reverb(sig, delays, fb, amt):
    out = sig[:]
    for d_ms in delays:
        d = int(SR * d_ms / 1000)
        for i in range(d, len(sig)):
            out[i] += out[i - d] * fb * amt
    return out
L = reverb(L, [97, 151, 223], 0.34, 0.30)
R = reverb(R, [103, 163, 239], 0.34, 0.30)

def soft(v): return math.tanh(v * 1.1) / math.tanh(1.1)
peak = 0.0
for i in range(N):
    L[i] = soft(L[i]); R[i] = soft(R[i])
    peak = max(peak, abs(L[i]), abs(R[i]))
scale = 0.95 / (peak or 1.0)

frames = bytearray()
for i in range(N):
    frames += struct.pack('<hh',
        int(max(-1.0, min(1.0, L[i] * scale)) * 32767),
        int(max(-1.0, min(1.0, R[i] * scale)) * 32767))
with wave.open('music-v4.wav', 'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(bytes(frames))

SECTIONS = [('q1',0,2),('q2',2,4),('q3',4,6),('hit',6,8),('brand',8,12),('ui1',12,14),
            ('ui2',14,16),('ui3',16,18),('seewo',18,21),('nums',21,23),('open',23,25),('end',25,30)]
with open('beats-v4.json', 'w', encoding='utf-8') as f:
    json.dump({'bpm': BPM, 'beat': BEAT, 'body': BODY, 'duration': DUR,
               'sections': [{'name': n, 'start': round(a * BEAT, 4), 'end': round(b * BEAT, 4)} for n, a, b in SECTIONS]},
              f, ensure_ascii=False, indent=1)
print('music-v4.wav: %.2f 秒（%d 拍 + %.1fs 尾音）, D 大调宏伟版' % (DUR, 30, TAIL))