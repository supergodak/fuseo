# W9-5 回帰コーパス: 合成カード 5 種 ×（正対・斜め写真・幅 800px・180°）＋ 文字の多い A4 2 枚と cards-expected.json を作る。
# Acceptance の cards モード（`dotnet run -c Release -- cards <dir>`）が、本番の部品（PageSource → PageAnalyzer →
# CandidateBuilder・QR・顔）で読み、種別・向き・検出子ごとの候補数・番号の位置を確かめる（docs/windows-design.md §10.19-3）。
#
#   Mac: python -I make_cards.py <outdir> [--seed N]
#     必要: pillow numpy qrcode（uv pip）。フォントは macOS のヒラギノ角ゴシック（W4・W6）。
#
# - 値はすべて見本（氏名「見本 太郎」等）。番号は**実行時に乱数で作り**、チェックデジットだけ正しくする（ファイルには書かない。
#   コーパスと cards-expected.json は**リポジトリの外**に作ること。seed は既定で毎回変わり、cards-expected.json に残る）。
# - 実物の書類（fixtures-private）は使わない。
import argparse
import json
import os
import random

import numpy as np
import qrcode
from PIL import Image, ImageDraw, ImageFont

FONT = "/System/Library/Fonts/ヒラギノ角ゴシック W4.ttc"
FONT_BOLD = "/System/Library/Fonts/ヒラギノ角ゴシック W6.ttc"
W, H = 1712, 1080  # ID-1（85.60×53.98mm）を約 508dpi
A4 = (2480, 3508)  # 300dpi


def font(size, bold=False):
    return ImageFont.truetype(FONT_BOLD if bold else FONT, size)


class Numbers:
    """実行時に作る見本の番号（チェックデジットの規則は Core の Checkdigits と同じ）。"""

    def __init__(self, rnd):
        self.rnd = rnd

    def digits(self, n):
        return "".join(str(self.rnd.randint(0, 9)) for _ in range(n))

    def my_number(self):
        body = [int(c) for c in self.digits(11)]
        s = sum(body[11 - n] * ((n + 1) if n <= 6 else (n - 5)) for n in range(1, 12))
        rem = s % 11
        return "".join(map(str, body)) + str(0 if rem <= 1 else 11 - rem)

    def license_number(self):
        while True:
            body = [int(c) for c in self.digits(10)]
            s, w = 0, 2
            for p in reversed(body):
                s += p * w
                w = 2 if w == 7 else w + 1
            c = (11 - s % 11) % 11
            if c <= 9:
                return "".join(map(str, body)) + str(c) + "0"

    def zairyu_number(self):
        letters = "ABCDEFGHJKLMNPRSTUVWXYZ"  # 読み違いやすい I・O・Q は避ける（数字の補正の対象と紛れない）
        pick = lambda: "".join(self.rnd.choice(letters) for _ in range(2))
        return pick() + self.digits(8) + pick()


def norm(box, w=W, h=H):
    x0, y0, x1, y1 = box
    return [round(x0 / w, 5), round(y0 / h, 5), round((x1 - x0) / w, 5), round((y1 - y0) / h, 5)]


def text(d, xy, s, size, bold=False, fill=(10, 10, 10)):
    """描いて、描いた文字の外接矩形（ピクセル）を返す。"""
    f = font(size, bold)
    d.text(xy, s, font=f, fill=fill)
    return d.textbbox(xy, s, font=f)


def base(bg):
    im = Image.new("RGB", (W, H), bg)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((4, 4, W - 5, H - 5), 40, outline=(150, 150, 150), width=4)
    return im, d


def face(d, box):
    """顔写真の欄（灰色のシルエット。顔検出は合成では当てにしない）。"""
    x0, y0, x1, y1 = box
    d.rectangle(box, fill=(205, 210, 215))
    cx = (x0 + x1) // 2
    d.ellipse((cx - 110, y0 + 80, cx + 110, y0 + 330), fill=(150, 155, 165))
    d.ellipse((cx - 190, y0 + 330, cx + 190, y1 + 200), fill=(150, 155, 165))


def card_mynumber_back(num):
    n = num.my_number()
    im, d = base((232, 240, 246))
    text(d, (80, 70), "個人番号", 56, True, (30, 30, 30))
    box = text(d, (80, 170), f"{n[0:4]}  {n[4:8]}  {n[8:12]}", 120, True)
    text(d, (80, 360), "氏名", 44, fill=(60, 60, 60))
    text(d, (240, 350), "見本　花子", 64)
    text(d, (80, 470), "生年月日", 44, fill=(60, 60, 60))
    text(d, (300, 465), "平成元年　1月　1日生", 52)
    qr = qrcode.make("FUSEO-SAMPLE-CARD", border=1).convert("RGB").resize((380, 380))
    im.paste(qr, (1250, 560))
    text(d, (80, 900), "この面は見本です", 36, fill=(90, 90, 90))
    return im, {"type": "myNumberCardBack", "min": {"myNumber12": 1, "qrBarcode": 1},
                "numberDetector": "myNumber12", "numberBox": norm(box)}, [n]


def card_mynumber_front(num):
    im, d = base((240, 244, 236))
    text(d, (80, 60), "個人番号カード", 64, True, (20, 20, 20))
    text(d, (80, 200), "氏名", 40, fill=(60, 60, 60))
    text(d, (220, 190), "見本　太郎", 64)
    text(d, (80, 330), "住所", 40, fill=(60, 60, 60))
    text(d, (220, 320), "東京都千代田区見本町一丁目2番3号", 52)
    text(d, (80, 470), "昭和60年 1月 1日生", 48)
    text(d, (700, 470), "性別 男", 48)
    text(d, (80, 600), "2036年 1月 1日まで有効", 48)
    text(d, (80, 760), "臓器提供意思", 36, fill=(60, 60, 60))
    face(d, (1220, 180, 1620, 700))
    # カード 1 枚の画像では性別・臓器提供の固定領域が候補になる（写真では切り抜くまで出ない＝ cards モードが写真では 0 を期待）。
    return im, {"type": "myNumberCardFront", "min": {}, "position": 2, "max": {"myNumber12": 0}}, []


def card_license(num):
    n = num.license_number()
    im, d = base((236, 246, 236))
    text(d, (80, 60), "氏名", 40, fill=(60, 60, 60))
    text(d, (200, 50), "見本　太郎", 64)
    text(d, (760, 62), "昭和60年 1月 1日生", 46)
    text(d, (80, 170), "住所", 40, fill=(60, 60, 60))
    text(d, (200, 160), "東京都千代田区見本町一丁目2番3号", 52)
    text(d, (80, 280), "交付　令和06年01月01日", 44)
    text(d, (80, 400), "2031年(令和13年)02月01日まで有効", 52, True)
    text(d, (80, 760), "第　", 60, True)
    f = font(60, True)
    x = 80 + d.textlength("第　", font=f)
    box = text(d, (x, 760), n, 60, True)
    text(d, (box[2] + d.textlength("　", font=f), 760), "号", 60, True)
    text(d, (80, 900), "運転免許証", 72, True)
    text(d, (620, 920), "東京都公安委員会", 44)
    face(d, (1220, 300, 1620, 820))
    return im, {"type": "menkyoshoFront", "min": {"licenseNumber12": 1},
                "numberDetector": "licenseNumber12", "numberBox": norm(box)}, [n]


def card_hokensho(num):
    kigo, bango, insurer = num.digits(4), num.digits(4), num.digits(8)
    im, d = base((246, 240, 230))
    text(d, (80, 60), "健康保険被保険者証　本人（被保険者）", 56, True, (20, 20, 20))
    text(d, (80, 200), f"記号　{kigo}　番号　{bango}", 56)
    text(d, (80, 320), "氏名　見本　太郎", 56)
    text(d, (80, 440), "生年月日　昭和60年1月1日", 52)
    f = font(52)
    label = "保険者番号　"
    text(d, (80, 780), label, 52)
    box = text(d, (80 + d.textlength(label, font=f), 780), insurer, 52)
    text(d, (80, 880), "保険者名称　見本健康保険組合", 52)
    return im, {"type": "hokensho", "min": {"insurerNumber": 1, "kigoBango": 2},
                "numberDetector": "insurerNumber", "numberBox": norm(box)}, [insurer]


def card_zairyu(num):
    n = num.zairyu_number()
    im, d = base((238, 238, 246))
    text(d, (80, 60), "在留カード", 72, True, (20, 20, 20))
    f = font(56, True)
    text(d, (900, 70), "番号 ", 56, True)
    box = text(d, (900 + d.textlength("番号 ", font=f), 70), n, 56, True)
    text(d, (80, 220), "氏名 SAMPLE TARO", 60)
    text(d, (80, 340), "生年月日 1990年01月01日　性別 男", 48)
    text(d, (80, 460), "国籍・地域 見本国", 48)
    text(d, (80, 580), "在留資格 見本", 48)
    face(d, (1250, 220, 1620, 700))
    return im, {"type": "zairyuCardFront", "min": {"zairyuNumber": 1},
                "numberDetector": "zairyuNumber", "numberBox": norm(box)}, [n]


def doc_page(num, with_number):
    im = Image.new("RGB", A4, "white")
    d = ImageDraw.Draw(im)
    text(d, (200, 160), "令和8年分 給与所得者の扶養控除等（異動）申告書（見本）", 64, True, "black")
    y, n, box = 360, None, None
    if with_number:
        n = num.my_number()
    for i in range(60):
        if with_number and i == 6:
            f = font(44)
            label = "あなたの個人番号　"
            text(d, (200, y), label, 44, fill="black")
            box = text(d, (200 + d.textlength(label, font=f), y), n, 44, fill="black")
        else:
            text(d, (200, y), f"{i + 1:02d}　見本の記載事項です。控除対象扶養親族の氏名・続柄・生年月日・住所又は居所（見本）", 38, fill="black")
        y += 52
    if with_number:
        return im, {"type": "generic", "min": {"myNumber12": 1}, "max": {"myNumber12": 1},
                    "numberDetector": "myNumber12", "numberBox": norm(box, *A4)}, [n]
    return im, {"type": "generic", "min": {}, "maxTotal": 0}, []


def photo(card, seed):
    """机（灰色のざらつき）の上に、少し斜め・台形に置いた写真（1600×1200・切り抜いていない）。"""
    wp, hp = 1600, 1200
    bg = np.full((hp, wp, 3), 120, np.uint8)
    bg += np.random.default_rng(seed).integers(0, 25, (hp, wp, 1)).astype(np.uint8)
    desk = Image.fromarray(bg)
    dst = [(260, 250), (1330, 200), (1380, 900), (220, 960)]
    src = [(0, 0), (W, 0), (W, H), (0, H)]
    a = []
    for (x, y), (u, v) in zip(dst, src):
        a.append([x, y, 1, 0, 0, 0, -u * x, -u * y])
        a.append([0, 0, 0, x, y, 1, -v * x, -v * y])
    coeffs = np.linalg.solve(np.array(a, float), np.array([c for p in src for c in p], float))
    warped = card.transform((wp, hp), Image.PERSPECTIVE, tuple(coeffs), Image.BICUBIC, fillcolor=(0, 0, 0))
    mask = Image.new("L", card.size, 255).transform((wp, hp), Image.PERSPECTIVE, tuple(coeffs), Image.BILINEAR)
    desk.paste(warped, (0, 0), mask)
    return desk


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("outdir")
    ap.add_argument("--seed", type=int, default=None, help="番号の乱数の種（省略時は毎回変わる）")
    args = ap.parse_args()
    seed = args.seed if args.seed is not None else random.SystemRandom().randrange(1, 2**31)
    num = Numbers(random.Random(seed))
    out = args.outdir
    os.makedirs(out, exist_ok=True)

    files = {}
    secrets = []
    for name, make in [("mynumB", card_mynumber_back), ("mynumF", card_mynumber_front), ("license", card_license),
                       ("hoken", card_hokensho), ("zairyu", card_zairyu)]:
        im, exp, nums = make(num)
        secrets += nums
        # 正対（カード 1 枚）・幅 800px・180°（逆さ: アプリが正立させる＝候補の位置は正対と同じ正規化座標）
        for suffix, img, turns in [("r0.png", im, 0), ("small.png", im.resize((800, 505), Image.LANCZOS), 0),
                                   ("r180.png", im.rotate(180), 2)]:
            img.save(os.path.join(out, f"{name}-{suffix}"))
            files[f"{name}-{suffix}"] = {"position": 0, **exp, "quarterTurns": turns, "cardFramed": True}
        # 斜め写真: 切り抜いていないので固定領域・位置推定は出ない（position=0）。番号の位置は台形の中なので確かめない。
        e = {k: v for k, v in exp.items() if k not in ("numberBox", "position")}
        e.update(quarterTurns=0, cardFramed=False, position=0)
        photo(im, len(name)).save(os.path.join(out, f"{name}-photo.jpg"), quality=90)
        files[f"{name}-photo.jpg"] = e
    for name, with_number in [("doc-number", True), ("doc-plain", False)]:
        im, exp, nums = doc_page(num, with_number)
        secrets += nums
        im.save(os.path.join(out, f"{name}.png"))
        files[f"{name}.png"] = dict(exp, quarterTurns=0, cardFramed=False, position=0)

    expected = {"seed": seed, "secrets": secrets, "files": files}
    with open(os.path.join(out, "cards-expected.json"), "w", encoding="utf-8") as fp:
        json.dump(expected, fp, ensure_ascii=False, indent=1)
    print(f"{len(files)} files (seed {seed}) -> {out}")


if __name__ == "__main__":
    main()
