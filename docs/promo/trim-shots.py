# 裁掉截图自带的 Windows 标题栏与边框，只保留窗口内容，避免与设计里的装饰条重复
from PIL import Image
import os

SRC = r'C:\Users\Qin_zzq\AppData\Local\Temp'
DST = r'C:\Users\Qin_zzq\Desktop\program\Seewo_fastlogin\seewoautologin\SeewoAutoLogin\docs\promo\assets'

JOBS = [
    ('settings_help.png', 'ui-settings.png'),   # 设置页 / 帮助与维护
    ('k2_about.png',      'ui-about.png'),      # 关于页 / 版本与更新
    ('s_backups.png',     'ui-backup.png'),     # 配置备份列表
]

TOP = 33      # Windows 标题栏高度（窗口 864x600，内容区从 y=33 开始）
SIDE = 1

for src, dst in JOBS:
    p = os.path.join(SRC, src)
    if not os.path.exists(p):
        print('MISS', src)
        continue
    im = Image.open(p).convert('RGB')
    w, h = im.size
    box = (SIDE, TOP, w - SIDE, h - SIDE)
    out = im.crop(box)
    out.save(os.path.join(DST, dst), 'PNG')
    print('%-22s %dx%d -> %dx%d  => %s' % (src, w, h, out.size[0], out.size[1], dst))