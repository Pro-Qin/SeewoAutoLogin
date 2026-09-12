# 宣传素材

两个版本，同一套视觉语言（深色 + 蓝绿光晕 + 苹果风排版）：

| 文件 | 形式 | 说明 |
|---|---|---|
| index.html | 网页 | 9 屏时间轴动画，自动播放；空格暂停、左右键切换、R 重播，?scene=N 直达某一屏 |
| film.html → seewo-promo-15s.mp4 | 视频 | 15 秒短片，1280x720 @30fps，H.264 + AAC |

## 短片内容（15 秒 / 6 段）

1. 0.0-2.6s 品牌开场（Logo + 标题渐显）
2. 2.6-5.0s 痛点：上课前先输一遍账号密码
3. 5.0-8.0s 真实界面展示：一个窗口管完（设置 / 帮助与维护）
4. 8.0-11.0s 效果展示：打开希沃，点头像就进
5. 11.0-13.4s 三个卖点：8 MB / 开机 0 次 UAC / 30 秒教程
6. 13.4-15.0s 结尾：下载地址与开源许可

## 重新生成

```bash
cd docs/promo

# 1) 配乐（纯标准库，输出 music.wav，15.2 秒）
python make-music.py

# 2) 逐帧渲染（需要 Edge，输出 frames/*.jpg，约 1 分钟）
node render-film.mjs

# 3) 合成（本机 ffmpeg 若无 libx264，可用 libopenh264）
ffmpeg -y -framerate 30 -i frames/%05d.jpg -i music.wav \
  -c:v libopenh264 -b:v 6M -pix_fmt yuv420p \
  -c:a aac -b:a 192k -shortest -movflags +faststart seewo-promo-15s.mp4
```

## 换成自己的音乐

覆盖 music.wav（或改 -i 指向的文件）后重跑第 3 步即可；时长比视频长也没关系，-shortest 会按视频长度截断。

注意：Apple 发布会使用的曲目均有版权，不能随本项目分发。仓库里这段配乐由 make-music.py 用正弦波合成，
无版权素材，可自由使用与修改；若要换成商业曲目，请自行确认授权（例如购买素材库授权）。

## 素材来源

assets/ 下的界面图取自软件真实运行截图（教程聚光灯、设置页、希沃登录界面）。更换界面截图后重新渲染即可。
