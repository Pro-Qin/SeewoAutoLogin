from PIL import Image
SRC = r'C:\Users\Qin_zzq\AppData\Local\Temp\ui_main_healthy.png'
DST = r'C:\Users\Qin_zzq\Desktop\program\Seewo_fastlogin\seewoautologin\SeewoAutoLogin\docs\promo\assets\ui-main.png'
im = Image.open(SRC).convert('RGB')
w, h = im.size
im = im.crop((1, 33, w - 1, h - 1))
im = im.crop((0, 0, im.size[0], 238))   # 只到列头为止，卡片行不进入画面
im.save(DST, 'PNG')
print('ui-main.png:', im.size)