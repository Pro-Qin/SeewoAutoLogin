# 三个图标方案：统一去掉渐变/高光/完美 squircle，改用平涂 + 硬边几何 + 有叙事的构图
from PIL import Image, ImageDraw
import os

S = 512
OUT = r'C:\Users\Qin_zzq\Desktop\program\Seewo_fastlogin\seewoautologin\SeewoAutoLogin\docs\promo\icon-drafts'
os.makedirs(OUT, exist_ok=True)

def canvas(bg, radius=128):
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=bg)
    return im, d

# ---------- 方案 A：门缝透光 ----------
# 一扇门，右侧裂开一条缝，里面透出光 —— 隐喻「打开、进入」
def draft_a():
    im, d = canvas((15, 23, 42, 255))                      # 深墨蓝
    # 门体：轻微不对称，右边缘比左边缘圆一些，避免呆板的对称矩形
    d.rounded_rectangle([150, 122, 322, 390], radius=34, fill=(38, 52, 74, 255))
    # 门缝：一条竖直的光，紧贴门内右侧
    d.rounded_rectangle([288, 138, 316, 374], radius=14, fill=(255, 184, 77, 255))
    # 地面的一道反光，让光「落下来」
    d.rounded_rectangle([288, 392, 372, 404], radius=6, fill=(255, 184, 77, 120))
    return im

# ---------- 方案 B：从列表里选中一个 ----------
# 左侧三个空心点（其他账号），右侧一个实心暖橙点（当前账号），
def draft_b():
    im, d = canvas((17, 24, 39, 255))
    for i, y in enumerate((148, 256, 364)):
        d.ellipse([132 - 34, y - 34, 132 + 34, y + 34], outline=(243, 239, 231, 255), width=16)
    # 被选中的那个：实心 + 一圈细环
    d.ellipse([352 - 46, 256 - 46, 352 + 46, 256 + 46], fill=(255, 107, 53, 255))
    d.ellipse([352 - 74, 256 - 74, 352 + 74, 256 + 74], outline=(255, 107, 53, 130), width=10)
    # 连接两列的一条短横线，暗示「选中了左边某一个」
    d.rounded_rectangle([186, 250, 296, 262], radius=6, fill=(243, 239, 231, 90))
    return im

# ---------- 方案 C：一枚朱砂印记 ----------
# 米白底 + 朱砂圆角方印 + 一个缺口，东方气质，和「老师 / 教室」的语境贴近
def draft_c():
    im, d = canvas((243, 239, 231, 255))                   # 米白纸感底
    d.rounded_rectangle([96, 96, 416, 416], radius=64, outline=(198, 62, 38, 255), width=26)
    # 印章里的两笔：一个横折 + 一个点，抽象成「开启」的意象
    d.rounded_rectangle([176, 200, 336, 226], radius=13, fill=(198, 62, 38, 255))
    d.rounded_rectangle([176, 200, 202, 330], radius=13, fill=(198, 62, 38, 255))
    d.ellipse([292, 274, 348, 330], fill=(198, 62, 38, 255))
    return im

names = {'A_门缝透光': draft_a, 'B_选中一个': draft_b, 'C_朱砂印': draft_c}
for name, fn in names.items():
    im = fn()
    im.save(os.path.join(OUT, name + '.png'))
    print('生成', name)

# 拼一张对比图：大图 + 小尺寸实拍效果（32px 与 16px 放大）
W, H = 512 * 3 + 80 * 2 + 60, 512 + 220
sheet = Image.new('RGB', (W, H), (24, 24, 27))
x = 60
for name, fn in names.items():
    im = fn()
    sheet.paste(im, (x, 40), im)
    # 32px / 16px 实际显示效果
    for j, px in enumerate((32, 16)):
        small = im.resize((px, px), Image.LANCZOS)
        big = small.resize((120, 120), Image.NEAREST)
        sheet.paste(big, (x + j * 140, 512 + 70))
    x += 512 + 80
sheet.save(os.path.join(OUT, '_compare.png'))
print('对比图已生成')