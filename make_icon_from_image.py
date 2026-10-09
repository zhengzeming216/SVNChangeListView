"""把一张方形原图转成 VSIX 需要的图标素材。

用法:
    python make_icon_from_image.py <原图路径>

产出（写入 Resources/）:
    Icon.png      128x128  VSIX Icon（source.extension.vsixmanifest 的 <Icon>）
    Icon-256.png  256x256  备用（VS Marketplace 页面图标等）

要点：
- 输入必须是正方形（脚本会自动居中裁成正方形，避免拉伸变形）；
- 源图没有透明通道时（例如 JPG、满幅渐变底），自动加圆角并让圆角之外透明，
  否则在 VS 深色主题下会露出一个方块底色；
- 统一用 LANCZOS 重采样，缩小后做一次轻微锐化，保证小尺寸下边缘不糊。

注意：Resources/Preview.png 是扩展管理器的大横幅（1280x800），不是方形图标，
不由本脚本生成，仍由 Resources/gen_assets.py 维护。
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "Resources")

TARGETS = [
    ("Icon.png", 128),
    ("Icon-256.png", 256),
]

CORNER_RATIO = 0.22  # 圆角半径 / 边长


def square(img):
    """居中裁成正方形（不拉伸）。"""
    w, h = img.size
    side = min(w, h)
    left = (w - side) // 2
    top = (h - side) // 2
    return img.crop((left, top, left + side, top + side))


def has_transparency(img):
    """源图是否真的带透明（有任一像素 alpha < 250 就算）。"""
    if img.mode not in ("RGBA", "LA"):
        return False
    alpha = img.getchannel("A")
    return alpha.getextrema()[0] < 250


def rounded_mask(size, ratio=CORNER_RATIO, ss=4):
    """超采样绘制的圆角方形蒙版，边缘平滑。"""
    S = size * ss
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * ratio), fill=255)
    return m.resize((size, size), Image.LANCZOS)


def resize(img, size, round_corner):
    small = img.resize((size, size), Image.LANCZOS)
    if round_corner:
        small.putalpha(rounded_mask(size))
    if size <= 256:
        # 缩小后轻微锐化，抵消重采样造成的发虚
        small = small.filter(ImageFilter.UnsharpMask(radius=1.0, percent=60, threshold=2))
    return small


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 1

    src = args[0]
    if not os.path.isfile(src):
        print("找不到原图: " + src)
        return 1

    force_round = "--round" in sys.argv
    no_round = "--no-round" in sys.argv

    os.makedirs(RES, exist_ok=True)
    img = Image.open(src).convert("RGBA")
    img = square(img)

    round_corner = False if no_round else (force_round or not has_transparency(img))
    print("原图 {} -> 裁成正方形 {}x{}，圆角处理: {}".format(
        os.path.basename(src), img.width, img.height,
        "是（源图无透明通道）" if round_corner else "否（源图自带透明）"))

    for name, size in TARGETS:
        out = os.path.join(RES, name)
        resize(img, size, round_corner).save(out, "PNG", optimize=True)
        print("  写出 {}  {}x{}  {} bytes".format(name, size, size, os.path.getsize(out)))

    print("完成。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
