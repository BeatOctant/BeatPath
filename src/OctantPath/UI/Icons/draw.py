# OctantPath の自前のボタンの絵（白一色、細めの線、角は尖らせる。2026-10-16）。ゲームの絵は写さず、作風だけ揃える
# 使い方: python3 draw.py <出力先>
import sys, math
from PIL import Image, ImageDraw
S = 512
W = 34  # 線の太さ（512 四方のとき）
out = sys.argv[1]

def canvas():
    im = Image.new("L", (S, S), 0)
    return im, ImageDraw.Draw(im)

def stroke(d, pts, w=W):
    # 折れ線を、角は尖らせて（マイター）、端は切りっぱなしで塗る
    h = w / 2
    n = len(pts)
    def unit(a, b):
        dx, dy = b[0] - a[0], b[1] - a[1]
        l = math.hypot(dx, dy)
        return dx / l, dy / l
    left, right = [], []
    for i in range(n):
        if i == 0:
            ux, uy = unit(pts[0], pts[1]); nx, ny = -uy, ux; m = 1
        elif i == n - 1:
            ux, uy = unit(pts[-2], pts[-1]); nx, ny = -uy, ux; m = 1
        else:
            a = unit(pts[i - 1], pts[i]); b = unit(pts[i], pts[i + 1])
            n1 = (-a[1], a[0]); n2 = (-b[1], b[0])
            sx, sy = n1[0] + n2[0], n1[1] + n2[1]
            l = math.hypot(sx, sy)
            nx, ny = sx / l, sy / l
            m = 1 / (nx * n1[0] + ny * n1[1])
        x, y = pts[i]
        left.append((x + nx * h * m, y + ny * h * m))
        right.append((x - nx * h * m, y - ny * h * m))
    d.polygon(left + right[::-1], fill=255)

def save(im, name):
    a = im.resize((128, 128), Image.LANCZOS)
    rgba = Image.new("RGBA", a.size, (255, 255, 255, 0))
    rgba.putalpha(a)
    rgba.save(f"{out}/{name}.png")

# 取得: 下向きの矢印と、受ける台
im, d = canvas()
stroke(d, [(256, 72), (256, 342)])
stroke(d, [(156, 242), (256, 342), (356, 242)])
stroke(d, [(116, 422), (396, 422)])
save(im, "download")

# 入れ替え: 上は右へ、下は左へ
im, d = canvas()
stroke(d, [(100, 180), (400, 180)]); stroke(d, [(330, 110), (400, 180), (330, 250)])
stroke(d, [(412, 332), (112, 332)]); stroke(d, [(182, 262), (112, 332), (182, 402)])
save(im, "swap")

# 戻る: 左向きの山形
im, d = canvas()
# 山形は先の側が軽く見えるので、幅の真ん中より右（+40）に置くと、板の真ん中に見える。板に対して小さめ（2026-10-16）
stroke(d, [(364, 144), (252, 256), (364, 368)], W + 2)
save(im, "back")

# 固定と移動の切り替え: 押しピンを時計回りに傾けた輪郭（固定中も移動中も同じ絵。2026-10-16）
def rotate(pts, deg, c=(256, 256)):
    t = math.radians(deg)
    return [(c[0] + (x - c[0]) * math.cos(t) - (y - c[1]) * math.sin(t), c[1] + (x - c[0]) * math.sin(t) + (y - c[1]) * math.cos(t)) for x, y in pts]

def offset(poly, dist):
    # 閉じた多角形（時計回り）を、角を尖らせたまま外へ dist だけ広げる（負なら内へ）
    n = len(poly)
    out = []
    for i in range(n):
        p0, p1, p2 = poly[i - 1], poly[i], poly[(i + 1) % n]
        def normal(a, b):
            dx, dy = b[0] - a[0], b[1] - a[1]
            l = math.hypot(dx, dy)
            return dy / l, -dx / l
        n1, n2 = normal(p0, p1), normal(p1, p2)
        sx, sy = n1[0] + n2[0], n1[1] + n2[1]
        l = math.hypot(sx, sy)
        nx, ny = sx / l, sy / l
        m = 1 / (nx * n1[0] + ny * n1[1])
        out.append((p1[0] + nx * dist * m, p1[1] + ny * dist * m))
    return out

def ring(d, poly, w=W):
    d.polygon(offset(poly, w / 2), fill=255)
    d.polygon(offset(poly, -w / 2), fill=0)

im, d = canvas()
# 上が頭（帽子）、くびれた胴、広い鍔（つば）。胴の左右はゆるく内へ反らせて、ふっくらさせる（2026-10-16 の写真のピン）。
# 真ん中を (256, 256) 付近に置いて 40 度回し、針は鍔から下へ
def curve(p0, p1, p2, steps=10):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0], (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1]) for t in (i / steps for i in range(1, steps + 1))]
right = [(332, 60), (332, 118), (306, 118)] + curve((306, 118), (288, 200), (372, 252)) + [(372, 290)]
left = [(140, 290), (140, 252)] + curve((140, 252), (224, 200), (206, 118)) + [(180, 118), (180, 60)]
head = [(x, y - 10) for x, y in right + left]
shift = lambda pts: [(x - 30, y + 26) for x, y in pts]  # 回したあと、右上に寄った分を戻す
ring(d, shift(rotate(head, 40)), W - 6)
stroke(d, shift(rotate([(256, 280 + W / 2), (256, 470)], 40)), W - 6)
save(im, "pin")

# 遊ぶ: 右向きの三角の輪郭（下見の行で、取得が済んだ曲の ↓ と入れ替わる。2026-10-22）。三角は重心が左に寄って見えるので、少し右に置く
im, d = canvas()
ring(d, [(176, 104), (412, 256), (176, 408)])
save(im, "play")

# 外す: ✕（下見の行から入れ替えずに外す。2026-10-22）
im, d = canvas()
stroke(d, [(136, 136), (376, 376)]); stroke(d, [(376, 136), (136, 376)])
save(im, "discard")

# 足す: ＋（下見の一覧の一番下の行。2026-10-22）
im, d = canvas()
stroke(d, [(256, 100), (256, 412)]); stroke(d, [(100, 256), (412, 256)])
save(im, "add")

# 置き直す: 左回りの丸い矢印（板を最初の位置へ戻す。ピンの横に、移動中だけ出す。2026-10-24）
im, d = canvas()
R = 150
arc = [(244 + R * math.cos(math.radians(a)), 268 - R * math.sin(math.radians(a))) for a in range(110, 110 + 290 + 1, 10)]
stroke(d, arc)
# 矢じりは、弧の終わり（右上）で進む向き（左上）へ尖らせる
end = math.radians(110 + 290)
ex, ey = arc[-1]
tx, ty = -math.sin(end), -math.cos(end)  # 進む向き
nx, ny = -ty, tx
tip = (ex + tx * 20, ey + ty * 20)
stroke(d, [(tip[0] - tx * 72 + nx * 72, tip[1] - ty * 72 + ny * 72), tip, (tip[0] - tx * 72 - nx * 72, tip[1] - ty * 72 - ny * 72)])
save(im, "reset")
