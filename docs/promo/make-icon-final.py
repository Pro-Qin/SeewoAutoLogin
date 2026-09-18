from PIL import Image, ImageDraw
import os
OUT = r'C:\Users\Qin_zzq\Desktop\program\Seewo_fastlogin\seewoautologin\SeewoAutoLogin\docs\promo\icon-drafts'
ASSETS = r'C:\Users\Qin_zzq\Desktop\program\Seewo_fastlogin\seewoautologin\SeewoAutoLogin\Resources'
PAPER = (243, 239, 231, 255)
VERMILION = (190, 58, 34, 255)

def draw_icon(size):
    im = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    r = int(size * 0.225)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=PAPER)

    if size <= 20:
        # 极小尺寸：去掉外框，只留一个粗箭头（框在这里只会糊成一团）
        aw = max(2, int(size * 0.17))
        ax0 = int(size * 0.20); ax1 = int(size * 0.82)
        cy = size // 2
        head = int(size * 0.26)
        d.rounded_rectangle([ax0, cy - aw // 2, ax1 - head // 2, cy + aw // 2], radius=aw // 2, fill=VERMILION)
        d.polygon([(ax1 - head, cy - head), (ax1, cy), (ax1 - head, cy + head)], fill=VERMILION)
        return im

    if size <= 40:
        # 小尺寸：保留框但加粗、缩小内边距
        w = max(2, int(size * 0.095))
        m = int(size * 0.16)
        d.rounded_rectangle([m, m, size - 1 - m, size - 1 - m], radius=int(size * 0.10),
                            outline=VERMILION, width=w)
        aw = max(2, int(size * 0.10))
        ax0 = int(size * 0.30); ax1 = int(size * 0.72)
        cy = size // 2; head = int(size * 0.15)
        d.rounded_rectangle([ax0, cy - aw // 2, ax1 - head // 2, cy + aw // 2], radius=aw // 2, fill=VERMILION)
        d.polygon([(ax1 - head, cy - head), (ax1, cy), (ax1 - head, cy + head)], fill=VERMILION)
        return im

    # 常规尺寸：完整构图
    w = max(3, int(size * 0.058))
    m = int(size * 0.175)
    d.rounded_rectangle([m, m, size - 1 - m, size - 1 - m], radius=int(size * 0.11),
                        outline=VERMILION, width=w)
    aw = max(3, int(size * 0.062))
    ax0 = int(size * 0.325); ax1 = int(size * 0.695)
    cy = size // 2; head = int(size * 0.115)
    d.rounded_rectangle([ax0, cy - aw // 2, ax1 - head // 2, cy + aw // 2], radius=aw // 2, fill=VERMILION)
    d.polygon([(ax1 - head, cy - head), (ax1, cy), (ax1 - head, cy + head)], fill=VERMILION)
    return im

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [draw_icon(s) for s in sizes]
ico = os.path.join(ASSETS, 'app.ico')
imgs[-1].save(ico, format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
print('app.ico:', os.path.getsize(ico), 'bytes')
draw_icon(1024).save(os.path.join(OUT, 'app-icon-1024.png'))

bar = Image.new('RGB', (1000, 260), (32, 32, 36))
d = ImageDraw.Draw(bar)
d.rectangle([0, 170, 1000, 260], fill=(20, 20, 24))
x = 40
for s in (16, 20, 24, 32, 48, 64):
    ic = draw_icon(s)
    d.text((x, 40), str(s) + 'px', fill=(150, 150, 155))
    bar.paste(ic, (x, 205 - s // 2), ic)
    x += max(s, 60) + 40
big = draw_icon(128)
bar.paste(big, (820, 20), big)
bar.save(os.path.join(OUT, '_final_preview.png'))
print('预览已更新')