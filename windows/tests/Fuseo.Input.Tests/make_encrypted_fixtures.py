# Fuseo.Input.Tests 用の合成パスワード付き PDF（中身は黒い四角だけ）を作る。
# 実行（Mac）: uv venv で pikepdf を入れ、`python -I make_encrypted_fixtures.py Fixtures`。パスワードはテスト専用の固定値。
import sys
import pikepdf

out = sys.argv[1]
for name, R, aes in [("encrypted-aes256.pdf", 6, True), ("encrypted-rc4-128.pdf", 3, False)]:
    pdf = pikepdf.new()
    pdf.add_blank_page(page_size=(200, 100))
    page = pdf.pages[0]
    # 左半分に黒い四角（中央より左＝暗、右＝白）
    page.obj.Contents = pdf.make_stream(b"0 g 10 10 80 80 re f")
    pdf.docinfo = pdf.make_indirect(pikepdf.Dictionary())
    pdf.save(
        f"{out}/{name}",
        encryption=pikepdf.Encryption(user="fuseo-test-pw", owner="fuseo-owner-pw", R=R, aes=aes, metadata=(R >= 4)),
    )
    print("wrote", name)
