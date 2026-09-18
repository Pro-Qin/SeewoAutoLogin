# 第二轮：保留最好认的「朱砂印」路线，并把语义做明确；另加两个高对比版本
from PIL import Image, ImageDraw
import os
S = 512
OUT = r'C:\Users\Qin_zzq\Desktop\program\Seewo_fastlogin\seewoautologin\SeewoAutoLogin\docs\promo\icon-drafts'

def canvas(bg, radius=128):
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=bg)
    return im, d

def arrow_right(d, x, y, w, h, color, thickness):
    """一个硬边箭头：进入 / 登录"""
    d.rounded_rectangle([x, y + h // 2 - thickness // 2, x + w - h // 2, y + h // 2 + thickness // 2],
                        radius=thickness // 2, fill=color)
    d.polygon([(x + w - h // 2 - 6, y), (x + w, y + h // 2), (x + w - h // 2 - 6, y + h)], fill=color)

# ---------- C2：朱砂印 + 进入箭头（印章构图，语义明确） ----------
def draft_c2():
    im, d = canvas((243, 239, 231, 255))
    d.rounded_rectangle([92, 92, 420, 420], radius=72, outline=(190, 58, 34, 255), width=30)
    arrow_right(d, 168, 216, 176, 80, (190, 58, 34, 255), 34)
    return im

# ---------- D：门与光（高对比：浅色门 + 暖光） ----------
def draft_d():
    im, d = canvas((17, 24, 39, 255))
    # 门：米白实心，略窄长，圆角偏硬
    d.rounded_rectangle([150, 116, 302, 396], radius=22, fill=(243, 239, 231, 255))
    # 门开了一条缝，暖光透出来
    d.rounded_rectangle([318, 116, 356, 396], radius=19, fill=(255, 176, 64, 255))
    return im

# ---------- E：列表里选中一个（方块版，小尺寸更稳） ----------
def draft_e():
    im, d = canvas((17, 24, 39, 255))
    for y in (140, 236, 332):
        d.rounded_rectangle([120, y, 268, y + 44], radius=22, fill=(243, 239, 231, 255))
    d.ellipse([300, 196, 404, 300], fill=(255, 107, 53, 255))
    return im

drafts = [('C2_朱砂印_进入', draft_c2), ('D_门与光', draft_d), ('E_列表选中', draft_e)]
for name, fn in drafts:
    im = fn()
    im.save(os.path.join(OUT, name + '.png'))
    print('生成', name)

W, H = 512 * 3 + 80 * 2 + 60, 512 + 230
sheet = Image.new('RGB', (W, H), (24, 24, 27))
x = 60
for name, fn in drafts:
    im = fn()
    sheet.paste(im, (x, 40), im)
    for j, px in enumerate((48, 32, 16)):
        small = im.resize((px, px), Image.LANCZOS)
        big = small.resize((110, 110), Image.NEAREST)
        sheet.paste(big, (x + j * 120, 512 + 80))
    x += 512 + 80
sheet.save(os.path.join(OUT, '_compare2.png'))
print('第二轮对比图已生成')