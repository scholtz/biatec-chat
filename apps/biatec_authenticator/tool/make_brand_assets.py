"""Regenerates the brand assets from the one mark (a shield with a check): the iOS icons (light, dark, tinted), the iOS launch image, the legacy Android
launcher icons, and the Android vector drawables for the adaptive icon, its monochrome layer and the splash.

    python tool/make_brand_assets.py        (needs Pillow)

The same mark is drawn in Dart by lib/ui/brand_mark.dart; test/ui/brand_assets_test.dart checks that the shapes agree.
"""
import json
import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLUE = (0x1B, 0x5E, 0x8C, 255)
WHITE = (255, 255, 255, 255)
NIGHT = (0x0F, 0x1A, 0x24, 255)
SKY = (0x9C, 0xCB, 0xEF, 255)

# The mark in a 100 x 100 box (the same numbers as BrandMarkPainter).
SHIELD = [("M", 50, 6), ("L", 88, 20), ("L", 88, 48),
          ("C", 88, 72, 72, 88, 50, 96), ("C", 28, 88, 12, 72, 12, 48),
          ("L", 12, 20), ("Z",)]
CHECK = [(31, 50), (45, 64), (70, 36)]


def bezier(p0, p1, p2, p3, n=24):
    out = []
    for i in range(1, n + 1):
        t = i / n
        u = 1 - t
        out.append((u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
                    u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1]))
    return out


def shield_points():
    pts, cur = [], None
    for seg in SHIELD:
        if seg[0] in "ML":
            cur = (seg[1], seg[2])
            pts.append(cur)
        elif seg[0] == "C":
            pts += bezier(cur, (seg[1], seg[2]), (seg[3], seg[4]), (seg[5], seg[6]))
            cur = (seg[5], seg[6])
    return pts


def draw_mark(size, scale, shield_color, check_color, bg=None, rounded=0):
    """The mark centred in a size x size picture; scale 1 is the 100-box filling it."""
    ss = 4
    big = size * ss
    img = Image.new("RGBA", (big, big), bg or (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = big / 100 * scale
    off = (big - 100 * k) / 2

    def tr(p):
        return (off + p[0] * k, off + p[1] * k)

    d.polygon([tr(p) for p in shield_points()], fill=shield_color)
    width = int(9 * k)
    pts = [tr(p) for p in CHECK]
    d.line(pts, fill=check_color, width=width, joint="curve")
    for p in (pts[0], pts[-1]):
        d.ellipse([p[0] - width / 2, p[1] - width / 2, p[0] + width / 2, p[1] + width / 2], fill=check_color)
    img = img.resize((size, size), Image.LANCZOS)
    if rounded:
        mask = Image.new("L", (size, size), 0)
        ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=int(size * rounded), fill=255)
        img.putalpha(mask)
    return img


def save(img, path, flat=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if flat:
        img = img.convert("RGB")
    img.save(path)


def ios():
    base = os.path.join(ROOT, "ios/Runner/Assets.xcassets")
    icons = os.path.join(base, "AppIcon.appiconset")
    with open(os.path.join(icons, "Contents.json")) as f:
        contents = json.load(f)
    seen = set()
    for image in contents["images"]:
        name = image.get("filename")
        if not name or name in seen or name == "Icon-App-1024x1024@1x.png":
            continue
        seen.add(name)
        px = round(float(image["size"].split("x")[0]) * int(image["scale"][0]))
        save(draw_mark(px, 0.62, WHITE, BLUE, bg=BLUE), os.path.join(icons, name), flat=True)
    save(draw_mark(1024, 0.62, WHITE, BLUE, bg=BLUE), os.path.join(icons, "Icon-App-1024x1024@1x.png"), flat=True)
    save(draw_mark(1024, 0.62, SKY, NIGHT, bg=NIGHT), os.path.join(icons, "Icon-App-1024x1024-dark.png"), flat=True)
    # The tinted variant is greyscale: the system applies the tint.
    save(draw_mark(1024, 0.62, (255, 255, 255, 255), (60, 60, 60, 255), bg=(40, 40, 40, 255)).convert("L"),
         os.path.join(icons, "Icon-App-1024x1024-tinted.png"))
    marketing = [i for i in contents["images"] if i.get("idiom") == "ios-marketing"]
    contents["images"] = [i for i in contents["images"]
                          if not (i.get("idiom") == "ios-marketing" and i.get("appearances"))]
    if marketing:
        contents["images"].append({
            "filename": "Icon-App-1024x1024-dark.png", "idiom": "universal", "platform": "ios", "size": "1024x1024",
            "appearances": [{"appearance": "luminosity", "value": "dark"}]})
        contents["images"].append({
            "filename": "Icon-App-1024x1024-tinted.png", "idiom": "universal", "platform": "ios", "size": "1024x1024",
            "appearances": [{"appearance": "luminosity", "value": "tinted"}]})
    with open(os.path.join(icons, "Contents.json"), "w") as f:
        json.dump(contents, f, indent=2)
        f.write("\n")
    # Launch image: the mark alone on transparency, so it shows on the light and the dark background alike.
    launch = os.path.join(base, "LaunchImage.imageset")
    for name, px in (("LaunchImage.png", 168), ("LaunchImage@2x.png", 336), ("LaunchImage@3x.png", 504)):
        save(draw_mark(px, 0.9, BLUE, WHITE), os.path.join(launch, name))


def android_legacy():
    res = os.path.join(ROOT, "android/app/src/main/res")
    for dpi, px in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
        save(draw_mark(px, 0.62, WHITE, BLUE, bg=BLUE, rounded=0.22), os.path.join(res, f"mipmap-{dpi}", "ic_launcher.png"))


def vector_paths(scale, center):
    """Path data of shield and check in a viewport of 108, the mark scaled by `scale` and centred."""
    def tr(x, y):
        return (center + (x - 50) * scale, center + (y - 51) * scale)

    def pt(x, y):
        a, b = tr(x, y)
        return f"{a:.2f},{b:.2f}"

    d = []
    for seg in SHIELD:
        if seg[0] == "M":
            d.append("M" + pt(seg[1], seg[2]))
        elif seg[0] == "L":
            d.append("L" + pt(seg[1], seg[2]))
        elif seg[0] == "C":
            d.append("C" + " ".join(pt(seg[i], seg[i + 1]) for i in (1, 3, 5)))
        else:
            d.append("Z")
    check = "M" + pt(*CHECK[0]) + " L" + pt(*CHECK[1]) + " L" + pt(*CHECK[2])
    return " ".join(d), check


def vector(shield_fill, check_stroke, scale, viewport=108, outline=None):
    """A vector drawable of the mark. With `outline` the shield is drawn as an outline of that colour (the monochrome layer has only alpha, so a filled shield would hide the check)."""
    shield, check = vector_paths(scale, viewport / 2)
    shield_attrs = (f'android:fillColor="{shield_fill}"' if outline is None else
                    f'android:strokeColor="{outline}"\n        android:strokeWidth="{5 * scale:.2f}"\n        android:strokeLineJoin="round"')
    return f"""<?xml version="1.0" encoding="utf-8"?>
<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="{viewport}dp"
    android:height="{viewport}dp"
    android:viewportWidth="{viewport}"
    android:viewportHeight="{viewport}">
    <path
        {shield_attrs}
        android:pathData="{shield}" />
    <path
        android:pathData="{check}"
        android:strokeColor="{check_stroke}"
        android:strokeWidth="{9 * scale:.2f}"
        android:strokeLineCap="round"
        android:strokeLineJoin="round" />
</vector>
"""


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as f:
        f.write(text)


def android_xml():
    res = os.path.join(ROOT, "android/app/src/main/res")
    # The adaptive icon's foreground sits inside the 66 dp safe zone of the 108 dp canvas.
    write(os.path.join(res, "drawable/ic_launcher_foreground.xml"), vector("#FFFFFFFF", "#FF1B5E8C", 0.5))
    write(os.path.join(res, "drawable/ic_launcher_monochrome.xml"), vector(None, "#FF000000", 0.5, outline="#FF000000"))
    write(os.path.join(res, "drawable/ic_splash_mark.xml"), vector("#FF1B5E8C", "#FFFFFFFF", 0.8))
    write(os.path.join(res, "mipmap-anydpi-v26/ic_launcher.xml"), """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background" />
    <foreground android:drawable="@drawable/ic_launcher_foreground" />
    <monochrome android:drawable="@drawable/ic_launcher_monochrome" />
</adaptive-icon>
""")
    write(os.path.join(res, "values/colors.xml"), """<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="ic_launcher_background">#1B5E8C</color>
    <color name="splash_background">#FFFFFF</color>
</resources>
""")
    write(os.path.join(res, "values-night/colors.xml"), """<?xml version="1.0" encoding="utf-8"?>
<resources>
    <color name="ic_launcher_background">#1B5E8C</color>
    <color name="splash_background">#0F1A24</color>
</resources>
""")
    write(os.path.join(res, "drawable/launch_background.xml"), """<?xml version="1.0" encoding="utf-8"?>
<!-- Before API 21 only the background colour is shown. -->
<layer-list xmlns:android="http://schemas.android.com/apk/res/android">
    <item android:drawable="@color/splash_background" />
</layer-list>
""")
    write(os.path.join(res, "drawable-v21/launch_background.xml"), """<?xml version="1.0" encoding="utf-8"?>
<!-- The brand mark centred on the background, light or dark with the system. -->
<layer-list xmlns:android="http://schemas.android.com/apk/res/android">
    <item android:drawable="@color/splash_background" />
    <item
        android:width="96dp"
        android:height="96dp"
        android:gravity="center"
        android:drawable="@drawable/ic_splash_mark" />
</layer-list>
""")
    for folder, parent in (("values-v31", "Theme.AppCompat.Light.NoActionBar"), ("values-night-v31", "Theme.AppCompat.NoActionBar")):
        write(os.path.join(res, folder, "styles.xml"), f"""<?xml version="1.0" encoding="utf-8"?>
<resources>
    <!-- Android 12 and newer draw their own splash from these attributes. -->
    <style name="LaunchTheme" parent="{parent}">
        <item name="android:windowSplashScreenBackground">@color/splash_background</item>
        <item name="android:windowSplashScreenAnimatedIcon">@drawable/ic_splash_mark</item>
        <item name="android:windowBackground">@drawable/launch_background</item>
    </style>
    <style name="NormalTheme" parent="{parent}">
        <item name="android:windowBackground">?android:colorBackground</item>
    </style>
</resources>
""")


if __name__ == "__main__":
    ios()
    android_legacy()
    android_xml()
    print("brand assets written")
