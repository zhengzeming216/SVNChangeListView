import math
from PIL import Image, ImageDraw, ImageFont

FONT = "C:/Windows/Fonts/msyh.ttc"          # Microsoft YaHei (CJK-capable)
FONT_BOLD = "C:/Windows/Fonts/msyhbd.ttc"

def font(size, bold=False):
    try:
        return ImageFont.truetype(FONT_BOLD if bold else FONT, size)
    except Exception:
        try:
            return ImageFont.truetype(FONT, size)
        except Exception:
            return ImageFont.load_default()

def vgrad(draw, box, c_top, c_bot):
    x0, y0, x1, y1 = box
    for y in range(y0, y1):
        t = (y - y0) / max(1, (y1 - y0))
        r = int(c_top[0] + (c_bot[0] - c_top[0]) * t)
        g = int(c_top[1] + (c_bot[1] - c_top[1]) * t)
        b = int(c_top[2] + (c_bot[2] - c_top[2]) * t)
        draw.line([(x0, y), (x1, y)], fill=(r, g, b))

# ---------------- Icon ----------------
# Icon.png / Icon-256.png 已改由 ../make_icon_from_image.py 从原图生成
# （会自动补 22% 圆角蒙版并让圆角外透明）。此处不再生成图标，避免覆盖新图。
# 本脚本现在只负责 Preview.png（扩展管理器横幅）。

# ---------------- Preview 1280x800 ----------------
W, H = 1280, 800
prev = Image.new("RGB", (W, H), (243, 245, 249))
pd = ImageDraw.Draw(prev)
# left accent bar
pd.rectangle([0, 0, 14, H], fill=(31, 78, 121))

# Title
pd.text((70, 70), "SVN ChangeList View", font=font(54, True), fill=(31, 78, 121))
pd.text((72, 138), "按 SVN changelist 分组管理待提交文件。", font=font(24), fill=(90, 100, 115))

# Feature bullets
features = [
    "三组折叠视图，组内树状浏览",
    "ignore-on-commit 用 svn status --cl 单独查询",
    "面包屑目录 + 状态栏待提交数量徽标",
]
fy = 220
green = (46, 160, 90)
for txt in features:
    pd.ellipse([78, fy + 5, 94, fy + 21], fill=green)
    pd.line([(83, fy + 14), (88, fy + 19)], fill=(255, 255, 255), width=3)
    pd.line([(88, fy + 19), (92, fy + 11)], fill=(255, 255, 255), width=3)
    pd.text((108, fy), txt, font=font(24), fill=(40, 48, 60))
    fy += 56

pd.text((72, 430), "需要 TortoiseSVN 的 svn.exe · 支持 VS 2022 / 2026",
        font=font(20), fill=(130, 138, 150))

# Right mock panel
px, py, pw, ph = 720, 90, 480, 600
# card shadow
pd.rounded_rectangle([px + 6, py + 8, px + pw + 6, py + ph + 8], radius=16, fill=(210, 216, 224))
pd.rounded_rectangle([px, py, px + pw, py + ph], radius=16, fill=(255, 255, 255))
# header bar
pd.rounded_rectangle([px, py, px + pw, py + 46], radius=16, fill=(31, 78, 121))
pd.rectangle([px, py + 24, px + pw, py + 46], fill=(31, 78, 121))
pd.text((px + 18, py + 12), "SVN ChangeList View", font=font(22, True), fill=(255, 255, 255))

def group(draw, gx, gy, name, items, indent=24):
    draw.rounded_rectangle([gx, gy, gx + pw - 24, gy + 30], radius=6, fill=(232, 238, 246))
    draw.text((gx + 12, gy + 5), name, font=font(18, True), fill=(31, 78, 121))
    yy = gy + 40
    for it in items:
        draw.text((gx + indent, yy), "• " + it, font=font(17), fill=(60, 68, 80))
        yy += 26
    return yy + 8

gy = py + 64
gy = group(pd, px + 14, gy, "Changes (3)", ["GFMSCommon/Dependency.cs", "Build.cs", "Config.xml"])
gy = group(pd, px + 14, gy, 'Changelist "ignore-on-commit"', ["DraftNotes.txt"])
gy = group(pd, px + 14, gy, "Unversioned", ["temp.log", "cache.bin"])

# status bar pill bottom-right of card
pill_y = py + ph - 42
pd.rounded_rectangle([px + pw - 96, pill_y, px + pw - 16, pill_y + 26], radius=13,
                     fill=(31, 78, 121))
pd.text((px + pw - 70, pill_y + 3), "3", font=font(18, True), fill=(255, 255, 255))

prev.save("C:/Users/station167/WorkBuddy/2026-09-24-17-19-59/SvnChangelistView/Resources/Preview.png")
print("Preview.png written", prev.size)
