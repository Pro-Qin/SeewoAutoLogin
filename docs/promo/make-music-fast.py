# 16 秒「激烈快速」版配乐：紧张铺垫 + 密集鼓点 + riser + drop + 结尾强击
# 纯标准库合成（无版权素材，可自由使用）。BPM 130。
import math, struct, wave

SR = 44100
BPM = 130.0
BEAT = 60.0 / BPM           # 0.4615s
BAR = BEAT * 4
DUR = 16.4
N = int(SR * DUR)
L = [0.0] * N
R = [0.0] * N

def mix(k, vl, vr):
    if 0 <= k < N:
        L[k] += vl
        R[k] += vr

def panned(pan):
    return math.sqrt((1.0 - pan) / 2.0), math.sqrt((1.0 + pan) / 2.0)

def kick(t0, amp=0.95, dur=0.42, pan=0.0):
    gl, gr = panned(pan)
    s0 = int(t0 * SR)
    n = int(dur * SR)
    ph = 0.0
    for i in range(n):
        t = i / SR
        f = 46.0 + 130.0 * math.exp(-t / 0.028)      # 音高快速下坠，才有冲击力
        ph += 2 * math.pi * f / SR
        e = math.exp(-t / 0.13)
        v = math.sin(ph) * e * amp
        mix(s0 + i, v * gl, v * gr)

def clap(t0, amp=0.42, pan=0.0):
    gl, gr = panned(pan)
    s0 = int(t0 * SR)
    n = int(0.22 * SR)
    seed = 987654321
    for i in range(n):
        t = i / SR
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2.0 - 1.0
        e = math.exp(-t / 0.035) + 0.5 * math.exp(-max(0.0, t - 0.012) / 0.05)
        v = rnd * e * amp
        mix(s0 + i, v * gl, v * gr)

def hat(t0, amp=0.16, dur=0.045, pan=0.22):
    gl, gr = panned(pan)
    s0 = int(t0 * SR)
    n = int(dur * SR)
    seed = 424242 + int(t0 * 1000)
    prev = 0.0
    for i in range(n):
        t = i / SR
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2.0 - 1.0
        hp = rnd - prev          # 一阶高通，得到金属质感的镲
        prev = rnd
        e = math.exp(-t / 0.014)
        v = hp * e * amp
        mix(s0 + i, v * gl, v * gr)

def bass(t0, dur, freq, amp=0.30, pan=0.0):
    gl, gr = panned(pan)
    s0 = int(t0 * SR)
    n = int(dur * SR)
    harm = (1.0, 0.46, 0.24, 0.12, 0.07)
    for i in range(n):
        t = i / SR
        a = 0.008
        e = (t / a) if t < a else math.exp(-(t - a) / (dur * 0.55))
        v = 0.0
        for h, ha in enumerate(harm, start=1):
            v += ha * math.sin(2 * math.pi * freq * h * t)
        v *= e * amp * 0.5
        mix(s0 + i, v * gl, v * gr)

def pluck(t0, freq, amp=0.16, dur=0.34, pan=0.0):
    gl, gr = panned(pan)
    s0 = int(t0 * SR)
    n = int(dur * SR)
    for i in range(n):
        t = i / SR
        e = math.exp(-t / 0.085)
        v = (math.sin(2 * math.pi * freq * t) + 0.34 * math.sin(4 * math.pi * freq * t)
             + 0.12 * math.sin(6 * math.pi * freq * t)) * e * amp
        mix(s0 + i, v * gl, v * gr)

def riser(t0, dur, amp=0.22, pan=0.0):
    gl, gr = panned(pan)
    s0 = int(t0 * SR)
    n = int(dur * SR)
    seed = 777
    prev = 0.0
    for i in range(n):
        t = i / SR
        x = t / dur
        seed = (1103515245 * seed + 12345) % (2 ** 31)
        rnd = (seed / (2 ** 31)) * 2.0 - 1.0
        # 高通截止随进度上升：用差分次数近似
        hp = rnd - prev
        prev = rnd
        if x > 0.55:
            hp = hp - 0.6 * (hp - rnd)
        e = x ** 2.2
        v = hp * e * amp
        mix(s0 + i, v * gl, v * gr)

def impact(t0, amp=0.9):
    # 低频冲击 + 噪声 + 明亮和声，用于 4.05s 的「停下。」与结尾重击
    s0 = int(t0 * SR)
    n = int(1.9 * SR)
    for i in range(n):
        t = i / SR
        e = math.exp(-t / 0.55)
        v = 0.55 * math.sin(2 * math.pi * (58.0 * math.exp(-t / 0.09) + 30.0) * t) * e * amp
        mix(s0 + i, v, v)
    for f in (220.0, 277.18, 329.63, 440.0):
        s1 = int(t0 * SR)
        m = int(1.8 * SR)
        for i in range(m):
            t = i / SR
            e = math.exp(-t / 0.85)
            v = math.sin(2 * math.pi * f * t) * e * 0.075 * amp
            mix(s1 + i, v, v)

# ===== 编曲 ===== 0-3.7s 紧张铺垫（疑问句节拍点）
for b in range(8):
    t = b * BEAT
    hat(t, 0.13 if b % 2 == 0 else 0.09)
    hat(t + BEAT * 0.5, 0.10)
    if b % 2 == 1:
        hat(t + BEAT * 0.25, 0.07)
        hat(t + BEAT * 0.75, 0.07)
riser(0.0, 3.9, 0.26)
# 三句疑问句各配一记重音
for ts in (0.15, 1.50, 2.85):
    kick(ts, 0.72)
    bass(ts, 0.42, 55.0, 0.30)

# ===== 3.7-5.5s 预 drop：加密 + 上升 =====
for b in range(4):
    t = 3.7 + b * (BEAT / 2)
    hat(t, 0.12)
    if b % 2 == 0:
        clap(t, 0.24)
kick(4.05, 0.85)
riser(3.7, 1.85, 0.30)

# ===== 5.5-13.4s 主段：四拍 kick + 八分 bass + 明亮旋律 =====
MAIN_START, MAIN_END = 5.5, 13.4
nb = int((MAIN_END - MAIN_START) / BEAT)
SCALE = [440.0, 523.25, 587.33, 659.25, 783.99, 880.0]
for b in range(nb):
    t = MAIN_START + b * BEAT
    kick(t, 0.92)
    hat(t + BEAT * 0.5, 0.14)
    hat(t + BEAT * 0.25, 0.08)
    hat(t + BEAT * 0.75, 0.08)
    if b % 4 == 2:
        clap(t, 0.40)
    # 八分音符贝斯线（A 小调骨架）
    ROOT = [110.0, 110.0, 146.83, 130.81]
    root = ROOT[(b // 4) % len(ROOT)]
    bass(t, BEAT * 0.46, root, 0.34)
    bass(t + BEAT * 0.5, BEAT * 0.40, root, 0.22)
    # 旋律：后段逐渐加密、上行
    if b >= 4:
        step = SCALE[(b * 3) % len(SCALE)]
        pluck(t + BEAT * 0.25, step, 0.15 if b < 10 else 0.19, pan=0.3 if b % 2 else -0.3)
    if b >= 10:
        pluck(t + BEAT * 0.75, SCALE[(b * 5) % len(SCALE)] * 2, 0.10, pan=-0.25)

# 和声垫在后半程进来，把能量顶上去
for f in (110.0, 164.81, 220.0, 277.18):
    s0 = int(8.6 * SR)
    n = int(5.4 * SR)
    for i in range(n):
        t = i / SR
        a = 0.6
        e = (t / a) if t < a else math.exp(-(t - a) / 3.4)
        v = (math.sin(2 * math.pi * f * t) + 0.3 * math.sin(4 * math.pi * f * t)) * e * 0.055
        mix(s0 + i, v, v)

# ===== 收尾 ===== 13.4s 强击 + 长尾 =====
impact(13.4, 1.0)
kick(13.4, 1.0)
clap(13.4, 0.5)
riser(12.4, 1.0, 0.24)

# ===== 混响 =====
def reverb(sig, delays_ms, fb, mixamt):
    out = sig[:]
    for d_ms in delays_ms:
        d = int(SR * d_ms / 1000.0)
        for i in range(d, len(sig)):
            out[i] += out[i - d] * fb * mixamt
    return out

L = reverb(L, [71, 113, 173], 0.33, 0.26)
R = reverb(R, [79, 127, 181], 0.33, 0.26)

# ===== 软限幅（激烈编曲容易削顶）=====
import math as _m
def soft(v):
    return _m.tanh(v * 1.15) / _m.tanh(1.15)
peak = 0.0
for i in range(N):
    L[i] = soft(L[i]); R[i] = soft(R[i])
    peak = max(peak, abs(L[i]), abs(R[i]))
scale = 0.94 / (peak or 1.0)

frames = bytearray()
for i in range(N):
    l = int(max(-1.0, min(1.0, L[i] * scale)) * 32767)
    r = int(max(-1.0, min(1.0, R[i] * scale)) * 32767)
    frames += struct.pack('<hh', l, r)

with wave.open('music-fast.wav', 'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(bytes(frames))

print('music-fast.wav: %.1f 秒, BPM %d, 峰值 %.2f' % (N / SR, int(BPM), peak))