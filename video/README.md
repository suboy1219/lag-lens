# LagLens 宣传视频

竖版 9:16 成片，中英各一套，均为 1080 × 1920 / 30 fps / H.264 + AAC。

## 目录

```
video/
├── zh/   快手版：中文配音、中文字幕、中文封面
└── en/   YouTube 版：英文配音、英文字幕、英文封面
```

## zh/

| 文件 | 说明 |
| --- | --- |
| `lag-lens-kuaishou-1080x1920.mp4` | 成片，约 48 秒 |
| `lag-lens-kuaishou-cover-1080x1920.png` | 封面 |
| `lag-lens-kuaishou-文案.md` | 标题、正文、话题标签、分镜 |

## en/

| 文件 | 说明 |
| --- | --- |
| `lag-lens-youtube-en-1080x1920.mp4` | 成片，48.63 秒，faststart 已开启 |
| `lag-lens-youtube-en-cover-1080x1920.png` | 封面 |
| `lag-lens-youtube-en.md` | 标题备选、描述、标签、分镜、上传设置 |

## 注意

- 两套都是 9:16，适合快手、YouTube Shorts 和竖版信息流；桌面端 16:9 播放会左右留黑边。
- 英文版里的软件面板是按原布局用模拟数据重绘的英文界面，当前软件本身只有中文 UI。
- 视频体积较大（单条 20 MB 以上），已在 `.git/info/exclude` 中排除 `video/`，避免误提交进仓库。
