# 混音：BGM + 点睛旁白（旁白出现时自动压低音乐，即 ducking）
# 旁白时间点 = 拍点（BPM 140）：hit 6拍 / brand 8拍 / seewo 18拍 / repo 25拍
import wave, struct, json, math

SR = 44100
BEAT = 60.0 / 140.0

def read_wav(path):
    with wave.open(path, 'rb') as w:
        ch, sw, fr, n = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        data = w.readframes(n)
    assert sr_ok(fr), fr
    if sw != 2:
        raise SystemExit('只支持 16-bit PCM: ' + path)
    vals = struct.unpack('<' + 'h' * (len(data) // 2), data)
    if ch == 2:
        L = [vals[i] / 32768.0 for i in range(0, len(vals), 2)]
        R = [vals[i + 1] / 32768.0 for i in range(0, len(vals), 2)]
        return L, R
    m = [v / 32768.0 for v in vals]
    return m[:], m[:]

def sr_ok(fr): return fr == SR

bgL, bgR = read_wav('music-v3.wav')
N = len(bgL)

# 旁白：(文件, 起始拍)
CUES = [('voices/v2/hit.wav', 6), ('voices/v2/brand.wav', 8),
        ('voices/v2/seewo.wav', 18), ('voices/v2/repo.wav', 25)]

placements = []
voice_tracks = []
for path, beat in CUES:
    vL, _ = read_wav(path)
    start = beat * BEAT
    placements.append((start, len(vL) / SR))
    voice_tracks.append((start, vL))

# ---- ducking 包络：旁白前后各留一点缓冲，快降慢升 ----
target = [1.0] * N
for start, dur in placements:
    s = max(0, int((start - 0.10) * SR))
    e = min(N, int((start + dur + 0.16) * SR))
    for i in range(s, e):
        target[i] = 0.40

env = 1.0
for i in range(N):
    k = 0.0022 if target[i] < env else 0.0004
    env += (target[i] - env) * k
    bgL[i] *= env
    bgR[i] *= env

# ---- 叠加旁白（旁白整体提一点，避免被鼓点盖住）----
VOICE_GAIN = 1.35
for start, v in voice_tracks:
    s0 = int(start * SR)
    for i, val in enumerate(v):
        k = s0 + i
        if 0 <= k < N:
            bgL[k] += val * VOICE_GAIN
            bgR[k] += val * VOICE_GAIN

# ---- 软限幅 + 归一化 ----
def soft(x): return math.tanh(x * 1.1) / math.tanh(1.1)
peak = 0.0
for i in range(N):
    bgL[i] = soft(bgL[i]); bgR[i] = soft(bgR[i])
    peak = max(peak, abs(bgL[i]), abs(bgR[i]))
scale = 0.95 / (peak or 1.0)

frames = bytearray()
for i in range(N):
    frames += struct.pack('<hh',
        int(max(-1.0, min(1.0, bgL[i] * scale)) * 32767),
        int(max(-1.0, min(1.0, bgR[i] * scale)) * 32767))

with wave.open('final-audio.wav', 'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(bytes(frames))

print('final-audio.wav: %.2f 秒；旁白 %d 句已按拍点混入（ducking 至 40%%）' % (N / SR, len(CUES)))