from PIL import Image, ImageFilter
import sys
im = Image.open(sys.argv[1]).convert('RGB')
w, h = im.size
# 只糊掉自检栏最右侧的「巡检完成 N 正常 / M 异常」（宣传片里不该出现「异常」）
box = (648, 96, 800, 130)
im.paste(im.crop(box).filter(ImageFilter.GaussianBlur(10)), box)
im.save(sys.argv[2], 'PNG')
print('处理完成:', im.size, '仅模糊', box)