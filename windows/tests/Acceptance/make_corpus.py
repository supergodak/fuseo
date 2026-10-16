# W8 受け入れ用の合成書類（すべて「見本」・実在の値なし）を作る。macOS のフォント（Arial Unicode・Hiragino Sans GB）を使う。
#   python -I make_corpus.py <outdir>     # 依存: pymupdf qrcode pillow piexif numpy
# 出力:
#   kojo-sample.pdf      控除証明書ふう 2 ページ（埋め込みフォント・フォーム記入値・注釈・QR・XMP/Info・2 ページ目は /Rotate 90 と CropBox）
#   kojo-locked.pdf      同じ中身を AES-256 で暗号化（パスワード: fuseo-w8-pw）
#   card-photo.jpg       見本カードを机の上で斜めに撮った体の写真（EXIF Orientation=6・GPS・機種・撮影日時・サムネイル付き）
#   expected.json        塗るべき欄の位置（表示されるページ／画像の上の正規化座標・左上原点）と、出力に残ってはいけない文字列
import json, sys, io, os
import pymupdf
import qrcode
import numpy as np
from PIL import Image, ImageDraw, ImageFont
import piexif

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
JP = "/Library/Fonts/Arial Unicode.ttf"
JPB = "/Library/Fonts/Arial Unicode.ttf"

SECRETS = ["見本 太郎", "見本町一丁目2番3号", "MIHON00001234", "見本 花子", "FUSEO-W8-QR", "W8 Sample Author", "W8 Sample Title"]

def qr_png(text):
    img = qrcode.make(text, border=1).convert("RGB")
    b = io.BytesIO(); img.save(b, "PNG"); return b.getvalue()

def build_pdf():
    doc = pymupdf.open()
    fields = []
    for pno in range(2):
        page = doc.new_page(width=595, height=842)
        page.insert_font(fontname="hira", fontfile=JP)
        page.insert_font(fontname="hirab", fontfile=JPB)
        page.insert_text((60, 80), "令和8年分 生命保険料控除証明書（見本）", fontname="hirab", fontsize=18)
        page.draw_rect(pymupdf.Rect(50, 110, 545, 420), color=(0, 0, 0), width=0.8)
        rows = [("契約者氏名", "見本 太郎"), ("住所", "東京都千代田区見本町一丁目2番3号"),
                ("証券番号", "MIHON00001234"), ("保険の種類", "定期保険（見本）"),
                ("申告額", "84,000 円")]
        for i, (k, v) in enumerate(rows):
            y = 150 + i * 50
            page.draw_line((50, y + 15), (545, y + 15), color=(0.6, 0.6, 0.6), width=0.5)
            page.insert_text((65, y), k, fontname="hira", fontsize=11)
            page.insert_text((200, y), v, fontname="hira", fontsize=13)
        page.insert_image(pymupdf.Rect(440, 440, 540, 540), stream=qr_png("FUSEO-W8-QR"))
        page.insert_text((60, 470), "電子申告用の QR（見本）", fontname="hira", fontsize=10)
        # 記入済みのフォーム欄（値は外観ストリームにだけ描かれる＝注釈を描かないと出力から消える）
        w = pymupdf.Widget()
        w.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT
        w.field_name = f"writer{pno}"
        w.rect = pymupdf.Rect(200, 560, 420, 590)
        w.field_value = "Mihon Hanako" if pno else "見本 花子"
        w.text_font = "japan" if not pno else "Helv"
        w.text_fontsize = 14
        w.border_color = (0.2, 0.2, 0.8)
        page.add_widget(w)
        page.insert_text((65, 580), "記入者", fontname="hira", fontsize=11)
        # 押印の代わりの注釈（FreeText）
        a = page.add_freetext_annot(pymupdf.Rect(440, 600, 540, 640), "見本印", fontsize=14,
                                    fontname="japan", text_color=(0.8, 0, 0))
        a.update()
        page.insert_text((60, 760), "この書類は Fuseo の受け入れ確認用の合成見本です。", fontname="hira", fontsize=9)
        if pno == 1:
            page.set_rotation(90)
            page.set_cropbox(pymupdf.Rect(30, 40, 565, 800))
    doc.set_metadata({"author": "W8 Sample Author", "title": "W8 Sample Title", "subject": "W8 subject",
                      "creator": "W8 creator", "producer": "W8 producer", "keywords": "W8 keyword"})
    doc.set_xml_metadata('<?xpacket begin=""?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
                         '<rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>W8 Sample Author</dc:creator></rdf:Description>'
                         '</rdf:RDF></x:xmpmeta><?xpacket end="w"?>')
    doc.subset_fonts()
    path = f"{out}/kojo-sample.pdf"
    doc.save(path, garbage=3, deflate=True)
    doc.save(f"{out}/kojo-locked.pdf", garbage=3, deflate=True, encryption=pymupdf.PDF_ENCRYPT_AES_256,
             user_pw="fuseo-w8-pw", owner_pw="fuseo-w8-owner")
    # 欄の位置（表示されるページの上・正規化・左上原点）。search_for は回転前の座標なので rotation_matrix で写す。
    doc = pymupdf.open(path)
    pages = []
    # 文字の位置は回転・CropBox の無い 1 ページ目で探す（2 ページ目も回す前は同じ配置）。
    hits0 = {k: doc[0].search_for(k)[0] for k in ["見本 太郎", "見本町一丁目2番3号", "MIHON00001234", "申告額"]}
    for page in doc:
        W, H = page.rect.width, page.rect.height
        mat = page.rotation_matrix
        def norm(r):
            r = (pymupdf.Rect(r) - (page.cropbox.x0, page.cropbox.y0, page.cropbox.x0, page.cropbox.y0)) * mat
            r.normalize()
            return [round(r.x0 / W, 5), round(r.y0 / H, 5), round(r.width / W, 5), round(r.height / H, 5)]
        items = {}
        for key in ["見本 太郎", "見本町一丁目2番3号", "MIHON00001234"]:
            items[key] = norm(hits0[key])
        items["qr"] = norm(pymupdf.Rect(440, 440, 540, 540))
        items["writer"] = norm(pymupdf.Rect(200, 560, 420, 590))
        items["stamp"] = norm(pymupdf.Rect(440, 600, 540, 640))
        keep = norm(hits0["申告額"])  # 塗らない欄（出力に残ること）
        pages.append({"rotation": page.rotation, "sizePt": [W, H], "mask": items, "keep": keep})
    return pages

def build_photo():
    # 見本カード（ID-1 比率 85.6×54）
    cw, ch = 1712, 1080
    card = Image.new("RGB", (cw, ch), (236, 242, 236))
    d = ImageDraw.Draw(card)
    f = ImageFont.truetype(JP, 64); fb = ImageFont.truetype(JPB, 80)
    d.rectangle([0, 0, cw - 1, ch - 1], outline=(60, 90, 60), width=10)
    d.text((70, 50), "見本カード（Fuseo 受け入れ用）", font=fb, fill=(20, 60, 20))
    d.text((70, 260), "氏名", font=f, fill=(60, 60, 60)); d.text((300, 250), "見本 太郎", font=fb, fill=(0, 0, 0))
    d.text((70, 420), "番号", font=f, fill=(60, 60, 60)); d.text((300, 410), "MIHON00001234", font=fb, fill=(0, 0, 0))
    d.text((70, 580), "有効期限", font=f, fill=(60, 60, 60)); d.text((360, 570), "2030年1月1日", font=fb, fill=(0, 0, 0))
    d.ellipse([1270, 230, 1590, 640], fill=(150, 160, 170))  # 顔写真の代わり
    q = qrcode.make("FUSEO-W8-QR", border=1).convert("RGB").resize((300, 300), Image.NEAREST)
    card.paste(q, (1300, 700))
    # 欄の位置（カードの上の正規化）
    def cn(x0, y0, x1, y1): return [x0 / cw, y0 / ch, (x1 - x0) / cw, (y1 - y0) / ch]
    card_items = {"name": cn(290, 240, 760, 360), "number": cn(290, 400, 1050, 520), "face": cn(1250, 210, 1610, 660),
                  "qr": cn(1314, 714, 1586, 986)}  # QR の暗いモジュールの範囲（余白 1 モジュールの内側）
    card_keep = cn(350, 560, 900, 680)
    # 机（なめらかな木目ふうの勾配）に斜めに置く。表示上の寸法 4032×3024。
    W, H = 4032, 3024
    yy, xx = np.mgrid[0:H, 0:W]
    bg = np.stack([150 + 30 * np.sin(xx / 97.0) * np.cos(yy / 413.0), 110 + 20 * np.sin(yy / 61.0 + xx / 900.0),
                   80 + 0 * xx], -1).clip(0, 255).astype(np.uint8)
    photo = Image.fromarray(bg)
    corners = [(900, 620), (3250, 820), (3080, 2420), (760, 2230)]  # TL TR BR BL（表示座標）
    # 射影変換で貼る（PIL の PERSPECTIVE は出力→入力の係数）
    def coeffs(dst, src):
        A = []; B = []
        for (x, y), (u, v) in zip(dst, src):
            A += [[x, y, 1, 0, 0, 0, -u * x, -u * y], [0, 0, 0, x, y, 1, -v * x, -v * y]]; B += [u, v]
        return np.linalg.solve(np.array(A, float), np.array(B, float)).tolist()
    src = [(0, 0), (cw, 0), (cw, ch), (0, ch)]
    warped = card.transform((W, H), Image.PERSPECTIVE, coeffs(corners, src), Image.BICUBIC)
    mask = Image.new("L", (W, H), 0); ImageDraw.Draw(mask).polygon(corners, fill=255)
    photo.paste(warped, (0, 0), mask)
    # 保存する画素は「表示を反時計回りに 90° 回したもの」＋ Orientation=6（表示時に時計回り 90°）
    stored = photo.transpose(Image.ROTATE_90)
    thumb = io.BytesIO(); stored.resize((120, 160)).save(thumb, "JPEG")
    exif = {"0th": {piexif.ImageIFD.Make: b"W8Make", piexif.ImageIFD.Model: b"W8 Sample Camera",
                    piexif.ImageIFD.Orientation: 6, piexif.ImageIFD.Artist: b"W8 Sample Author"},
            "Exif": {piexif.ExifIFD.DateTimeOriginal: b"2026:10:17 09:00:00"},
            "GPS": {piexif.GPSIFD.GPSLatitudeRef: b"N", piexif.GPSIFD.GPSLatitude: ((35, 1), (40, 1), (0, 1)),
                    piexif.GPSIFD.GPSLongitudeRef: b"E", piexif.GPSIFD.GPSLongitude: ((139, 1), (45, 1), (0, 1))},
            "1st": {}, "thumbnail": thumb.getvalue()}
    stored.save(f"{out}/card-photo.jpg", "JPEG", quality=92, exif=piexif.dump(exif))
    quad = [[x / W, y / H] for x, y in corners]
    return {"displaySize": [W, H], "quad": quad, "card": card_items, "keep": card_keep}

expected = {"secrets": SECRETS, "password": "fuseo-w8-pw", "pdf": build_pdf(), "photo": build_photo()}
with open(f"{out}/expected.json", "w", encoding="utf-8") as fp:
    json.dump(expected, fp, ensure_ascii=False, indent=1)
print(json.dumps(expected, ensure_ascii=False)[:1500])
