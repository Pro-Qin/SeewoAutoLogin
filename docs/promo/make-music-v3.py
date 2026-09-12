# 快节奏版配乐 v3：Corporate Upbeat / 科技感（BPM 140）
# 关键点：所有音符严格落在拍点上，并导出 beats.json 供画面卡点使用；
# 加入 sidechain（每次 kick 压下其它声部）——这是这类音乐听起来「有推进力」的核心。
import math, struct, wave, json

SR = 44100
BPM = 140.0
BEAT = 60.0 / BPM            # 0.42857s
BARS = 8                     # 8 小节 = 32 拍
TAIL = 1.6                   # 尾音
BODY = BARS * 4 * BEAT       # 13.714s
DUR = BODY + TAIL
N = int(SR * DUR)

# 分段（拍为单位）—— 与画面镜头一致
SECTIONS = [
    ('q1',    0,  2), ('q2',  2,  4), ('q3',  4,  6), ('hit', 6,  8),
    ('brand', 8, 12), ('ui1', 12, 14), ('ui2', 14, 16), ('ui3', 16, 18),
    ('seewo', 18, 21), ('nums', 21, 23), ('open', 23, 25), ('end', 25, 32),
]

L = [0.0] * N
R = [0.0] * N
def pan(p):
    return math.sqrt((1 - p) / 2), math.sqrt((1 + p) / 2)
def add(k, vl, vr):
    if 0 <= k < N:
        L[k] += vl; R[k] += vr

# --- 声部：先渲染到独立轨，便于做 sidechain ---
def newtrack(): return [0.0] * N
def put(track, k, v):
    if 0 <= k < N: track[k] += v

T = {name: newtrack() for name in ('kick','clap','hat','shaker','bass','arp','pad','fx','sub')}

def t2k(t): return int(t * SR)

def kick(t0, amp=1.0, track='kick'):
    s0, n = t2k(t0), int(0.34 * SR)
    gl, gr = pan(0.0)
    ph = 0.0
    for i in range(n):
        t = i / SR
        f = 52.0 + 150.0 * math.exp(-t / 0.021)
        ph += 2 * math.pi * f / SR
        e = math.exp(-t / 0.105)
        v = math.sin(ph) * e * amp
        put(T[track], s0 + i, v)
        # 低频 sub 层，让鼓更「实」
        put(T['sub'], s0 + i, math.sin(2 * math.pi * 42 * t) * math.exp(-t / 0.14) * amp * 0.5)

def clap(t0, amp=0.5):
    s0, n = t2k(t0), int(0.2 * SR)
    gl, gr = pan(-0.08)
    seed = 20250101 + int(t0 * 997)
    for i in range(n):
        t = i / SR
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2 - 1
        e = math.exp(-t / 0.028) + 0.45 * math.exp(-max(0, t - 0.011) / 0.045)
        v = rnd * e * amp
        put(T['clap'], s0 + i, v * gl); put(T['clap'], s0 + i, v * gr)

def perc(t0, amp, dur, decay, panv, track, seed0=7):
    s0, n = t2k(t0), int(dur * SR)
    gl, gr = pan(panv)
    seed = seed0 + int(t0 * 1237)
    prev = 0.0
    for i in range(n):
        t = i / SR
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2 - 1
        hp = rnd - prev; prev = rnd
        put(T[track], s0 + i, hp * math.exp(-t / decay) * amp * gl)
        put(T[track], s0 + i, hp * math.exp(-t / decay) * amp * gr)

def bass(t0, dur, freq, amp=0.32):
    s0, n = t2k(t0), int(dur * SR)
    harm = (1.0, 0.42, 0.2, 0.09)
    for i in range(n):
        t = i / SR
        a = 0.006
        e = (t / a) if t < a else math.exp(-(t - a) / (dur * 0.6))
        v = sum(ha * math.sin(2 * math.pi * freq * h * t) for h, ha in enumerate(harm, 1))
        put(T['bass'], s0 + i, v * e * amp * 0.45)

def arp(t0, freq, amp=0.17, dur=0.3, panv=0.0):
    s0, n = t2k(t0), int(dur * SR)
    gl, gr = pan(panv)
    for i in range(n):
        t = i / SR
        e = math.exp(-t / 0.075)
        v = (math.sin(2 * math.pi * freq * t) + 0.3 * math.sin(4 * math.pi * freq * t)
             + 0.1 * math.sin(6 * math.pi * freq * t)) * e * amp
        put(T['arp'], s0 + i, v * gl); put(T['arp'], s0 + i, v * gr)

def pad(t0, dur, freq, amp=0.05):
    s0, n = t2k(t0), int(dur * SR)
    for i in range(n):
        t = i / SR
        a = 0.5
        e = (t / a) if t < a else math.exp(-(t - a) / (dur * 0.8))
        v = (math.sin(2 * math.pi * freq * t) + 0.28 * math.sin(4 * math.pi * freq * t)) * e * amp
        put(T['pad'], s0 + i, v)

def riser(t0, dur, amp=0.26, panv=0.0):
    s0, n = t2k(t0), int(dur * SR)
    gl, gr = pan(panv)
    seed = 31337
    prev = 0.0
    for i in range(n):
        t = i / SR; x = t / dur
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2 - 1
        hp = rnd - prev; prev = rnd
        put(T['fx'], s0 + i, hp * (x ** 2.0) * amp * gl)
        put(T['fx'], s0 + i, hp * (x ** 2.0) * amp * gr)

def impact(t0, amp=1.0):
    s0, n = t2k(t0), int(1.7 * SR)
    for i in range(n):
        t = i / SR
        e = math.exp(-t / 0.5)
        v = 0.5 * math.sin(2 * math.pi * (62 * math.exp(-t / 0.08) + 34) * t) * e * amp
        put(T['fx'], s0 + i, v)
    for f in (220.0, 277.18, 329.63, 440.0, 554.37):
        for i in range(int(1.5 * SR)):
            t = i / SR
            put(T['fx'], s0 + i, math.sin(2 * math.pi * f * t) * math.exp(-t / 0.7) * 0.055 * amp)

# ================= 编曲 =================
SCALE = [440.00, 493.88, 523.25, 587.33, 659.25, 783.99, 880.00, 987.77]  # A 小调自然音阶
ROOTS = [110.00, 110.00, 130.81, 146.83]                                   # A A C D

# --- 0-8 拍：悬念铺垫（密集 shaker + 每 2 拍重音）---
for i in range(16):                       # 16 个八分音符 = 8 拍
    t = i * (BEAT / 2)
    perc(t, 0.085 if i % 2 == 0 else 0.055, 0.05, 0.012, 0.25, 'shaker', 11)
for i in range(4):                        # 疑问句重音落在 0/2/4/6 拍
    t = i * 2 * BEAT
    kick(t, 0.85)
    bass(t, BEAT * 0.9, 55.0, 0.3)
riser(0.0, 8 * BEAT, 0.22)

# --- 8-12 拍：build（kick 四拍 + clap 反拍）---
for b in range(4):
    t = (8 + b) * BEAT
    kick(t, 0.95)
    clap(t + BEAT * 0.5, 0.42)
    perc(t + BEAT * 0.25, 0.09, 0.05, 0.012, 0.25, 'shaker', 21)
    perc(t + BEAT * 0.75, 0.09, 0.05, 0.012, -0.25, 'shaker', 31)
riser(6 * BEAT, 6 * BEAT, 0.3)

# --- 12-32 拍：drop（全鼓组 + 贝斯律动 + 琶音旋律）---
for b in range(12, 32):
    t = b * BEAT
    kick(t, 1.0)
    clap(t + BEAT * 0.5, 0.5)
    perc(t + BEAT * 0.25, 0.10, 0.05, 0.011, 0.26, 'shaker', 41)
    perc(t + BEAT * 0.75, 0.10, 0.05, 0.011, -0.26, 'shaker', 51)
    root = ROOTS[(b // 4) % len(ROOTS)]
    bass(t, BEAT * 0.42, root, 0.34)
    bass(t + BEAT * 0.5, BEAT * 0.36, root, 0.24)
    # 八分音符琶音：每拍两个音，上行跑动
    arp(t + BEAT * 0.25, SCALE[(b * 3) % len(SCALE)], 0.15, panv=0.28)
    arp(t + BEAT * 0.75, SCALE[(b * 5 + 2) % len(SCALE)] * 1.5, 0.10, panv=-0.28)
    if b >= 20:   # 后半段加密到十六分，制造「冲」的感觉
        arp(t + BEAT * 0.125, SCALE[(b * 7) % len(SCALE)] * 2, 0.06, panv=-0.4)
        arp(t + BEAT * 0.625, SCALE[(b * 11 + 1) % len(SCALE)] * 2, 0.06, panv=0.4)

for f in (110.0, 164.81, 220.0, 277.18):
    pad(10 * BEAT, 22 * BEAT, f, 0.045)

# --- 收尾 ---
impact(31 * BEAT, 0.95)
kick(31 * BEAT, 1.0)
clap(31 * BEAT, 0.55)
riser(29 * BEAT, 2 * BEAT, 0.3)
for f in (220.0, 277.18, 329.63, 440.0, 554.37):
    pad(31 * BEAT, TAIL + 0.6, f, 0.075)

# ================= sidechain：kick 触发，把其它声部压低 =================
def sidechain(src, duck=0.62, rel=0.30):
    out = src[:]
    env = 1.0
    step = 1.0 - math.exp(-1.0 / (SR * 0.006))
    dec = math.exp(-1.0 / (SR * rel))
    for i in range(N):
        target = duck if T['kick'][i] > 0.35 else 1.0
        env += (target - env) * (step if target < env else (1 - dec))
        out[i] = src[i] * env
    return out

for name in ('bass', 'arp', 'pad', 'fx'):
    T[name] = sidechain(T[name])

# ================= 合并 + 混响 + 限幅 =================
GAIN = {'kick': 0.92, 'sub': 0.28, 'clap': 0.40, 'shaker': 0.5, 'bass': 0.85,
        'arp': 1.0, 'pad': 1.0, 'fx': 0.9}
for name, g in GAIN.items():
    tr = T[name]
    for i in range(N):
        v = tr[i] * g
        L[i] += v; R[i] += v

# 轻微立体声加宽（对 arp 与 fx）
for name in ('arp', 'fx'):
    tr = T[name]
    for i in range(0, N, 1):
        d = 12
        if i + d < N:
            L[i] += tr[i + d] * 0.12
            R[i] += tr[max(0, i - d)] * 0.12

def reverb(sig, delays, fb, amt):
    out = sig[:]
    for d_ms in delays:
        d = int(SR * d_ms / 1000)
        for i in range(d, len(sig)):
            out[i] += out[i - d] * fb * amt
    return out
L = reverb(L, [67, 109, 167], 0.3, 0.22)
R = reverb(R, [73, 121, 179], 0.3, 0.22)

def soft(v): return math.tanh(v * 1.12) / math.tanh(1.12)
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

with wave.open('music-v3.wav', 'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(bytes(frames))

# 导出拍点表：画面用同一张表，天然卡点
beats = {
    'bpm': BPM, 'beat': BEAT, 'bars': BARS, 'body': BODY, 'duration': DUR,
    'sections': [{'name': n, 'startBeat': a, 'endBeat': b,
                  'start': round(a * BEAT, 4), 'end': round(b * BEAT, 4)} for n, a, b in SECTIONS],
    'beats': [round(i * BEAT, 4) for i in range(BARS * 4 + 4)]
}
with open('beats.json', 'w', encoding='utf-8') as f:
    json.dump(beats, f, ensure_ascii=False, indent=1)

print('music-v3.wav: %.2f 秒 (BPM %d)，拍点表 beats.json 已导出' % (DUR, int(BPM)))