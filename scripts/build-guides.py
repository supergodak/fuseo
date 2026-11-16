#!/usr/bin/env python3
"""書類別の伏せ方ガイド（site/guide/*.html）と使い方（site/manual/*.html）を作る。  python3 scripts/build-guides.py
使い方は docs/user-manual*.md を HTML にする（要 markdown: uv pip install markdown。無ければ使い方だけ飛ばす）。
内容の根拠は docs/zenn/article2-guide.md・article4-minimize.md（確認済みの整理）。site/ は git 管理外。"""
import html, os, json
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'site', 'guide')
BASE = 'https://fuseo.ati-mirai.co.jp/guide/'
UPDATED = '2026-11-14'

# 共通ナビ・フッタ（トップ・機能のご紹介と同じ並び）
HEADER = '''<header class="wrap">
  <div class="nav">
    <a href="../"><img src="../assets/icon-256.png" alt="" width="36" height="36"></a>
    <a class="brand" href="../">Fuseo（ふせお）</a>
    <span class="spacer"></span>
    <nav class="navlinks" aria-label="サイト内">
      <a href="../">トップ</a>
      <a href="../features.html">機能のご紹介</a>
      <a href="../guide/" aria-current="page">書類別ガイド</a>
      <a href="../collect/">集める方へ</a>
      <a href="../manual/">使い方</a>
      <a href="../#download">ダウンロード</a>
      <a href="https://github.com/supergodak/fuseo">GitHub</a>
    </nav>
  </div>
</header>'''
FOOTER = '''<footer class="wrap">
  <div class="links">
    <a href="../">トップ</a>
    <a href="../features.html">機能のご紹介</a>
    <a href="../guide/">書類別の伏せ方ガイド</a>
    <a href="../collect/">書類を集める方へ</a>
    <a href="../manual/">使い方</a>
    <a href="https://github.com/supergodak/fuseo">GitHub</a>
    <a href="https://github.com/supergodak/fuseo/blob/main/PRIVACY.md">プライバシー</a>
    <a href="../terms/">利用条件</a>
    <a href="https://tameo.ati-mirai.co.jp">姉妹アプリ Tameo</a>
  </div>
  <div>© 2026 ATI Inc. (ATI株式会社) · MIT License</div>
</footer>'''

CSS = """
:root{--celadon:#D5E2DF;--celadon-deep:#C2D5D0;--porcelain:#F7FAF9;--line:#B9CCC7;--china:#3B6CB4;--china-ink:#28518A;--sumi:#171B1E;--sumi-soft:#4A5359;--sand:#F3EAD4}
*{margin:0;padding:0;box-sizing:border-box}
body{background:var(--celadon);color:var(--sumi);font-family:"Hiragino Kaku Gothic ProN","Hiragino Sans","Yu Gothic",-apple-system,sans-serif;font-size:16px;line-height:1.9;-webkit-font-smoothing:antialiased}
.mincho{font-family:"Hiragino Mincho ProN","Yu Mincho",serif}
.wrap{max-width:760px;margin:0 auto;padding:0 24px}
a{color:var(--china);text-decoration:none}a:hover{color:var(--china-ink);text-decoration:underline}
:focus-visible{outline:3px solid var(--china);outline-offset:2px}
header.wrap{max-width:960px;padding:28px 24px 12px}
/* 共通ナビ（トップ・機能のご紹介・書類別ガイドで同じ並び。いまのページは太字＋下線） */
.nav{display:flex;align-items:center;gap:10px 12px;flex-wrap:wrap}
.nav img{width:36px;height:36px;border-radius:8px;display:block}
.nav .brand{font-weight:700;font-size:1.05rem;letter-spacing:.06em;color:var(--sumi)}
.nav a.brand:hover{text-decoration:none;color:var(--sumi)}
.nav .spacer{flex:1}
.navlinks{display:flex;flex-wrap:wrap;align-items:center;gap:2px 18px;font-size:.9rem}
.navlinks a{color:var(--sumi-soft);padding:4px 0;border-bottom:2px solid transparent}
.navlinks a:hover{color:var(--china-ink);text-decoration:none;border-bottom-color:var(--line)}
.navlinks a[aria-current="page"]{color:var(--sumi);font-weight:700;border-bottom-color:var(--sumi)}
footer .links{display:flex;flex-wrap:wrap;gap:6px 18px;margin-bottom:10px}
.crumb{font-size:.82rem;color:var(--sumi-soft);margin:26px 0 10px}
.eyebrow{display:flex;align-items:center;gap:10px;margin-bottom:12px}
.eyebrow .bar{width:34px;height:.85em;background:var(--sumi);border-radius:2px}.eyebrow .label{font-size:.78rem;letter-spacing:.26em;color:var(--sumi-soft)}
h1{font-size:clamp(1.6rem,3.6vw,2.2rem);font-weight:600;letter-spacing:.04em;line-height:1.5;margin-bottom:18px}
h2{font-size:1.3rem;font-weight:600;letter-spacing:.04em;margin:44px 0 14px;padding-top:6px}
p{margin:.9em 0}ul,ol{margin:.6em 0 .9em 1.4em}li{margin:.3em 0}
.answer{background:var(--porcelain);border:1px solid var(--line);border-left:6px solid var(--sumi);border-radius:10px;padding:18px 22px;margin:8px 0 6px}
.answer b{color:var(--sumi)}
table{width:100%;border-collapse:collapse;background:var(--porcelain);border:1px solid var(--line);font-size:.92rem;margin:14px 0}
th,td{padding:10px 12px;border-bottom:1px solid #DCE6E3;text-align:left;vertical-align:top}thead th{background:var(--celadon-deep)}
.tablewrap{overflow-x:auto}
.note{background:var(--sand);border:1px solid #E2D2AC;border-radius:10px;padding:14px 18px;font-size:.9rem;color:#4c4633;margin:18px 0}
.cta{background:var(--celadon-deep);border-radius:14px;padding:26px 24px;margin:44px 0 10px}
.cta h2{margin:0 0 8px;padding:0}.cta p{margin:.5em 0}
.btn{display:inline-block;background:var(--china);color:#fff;border-radius:10px;padding:11px 22px;font-weight:700;margin:8px 8px 0 0}
.btn:hover{background:var(--china-ink);color:#fff;text-decoration:none}
.btn.ghost{background:var(--porcelain);color:var(--china-ink);border:1.5px solid var(--china)}
.cards{display:grid;grid-template-columns:repeat(2,1fr);gap:14px;margin:18px 0}
.card{display:block;background:var(--porcelain);border:1px solid var(--line);border-radius:10px;padding:16px 18px;color:var(--sumi)}
.card:hover{border-color:var(--china);text-decoration:none}.card b{display:block;margin-bottom:2px}.card span{font-size:.85rem;color:var(--sumi-soft)}
.related{font-size:.92rem}
blockquote{background:var(--sand);border:1px solid #E2D2AC;border-radius:10px;padding:12px 18px;margin:14px 0;color:#4c4633}
blockquote p{margin:.3em 0}
h3{font-size:1.05rem;margin:26px 0 8px}
code{font-family:"SF Mono",Menlo,monospace;font-size:.88em;background:var(--porcelain);border:1px solid var(--line);border-radius:4px;padding:1px 5px;word-break:break-all}
hr{border:0;border-top:1px solid var(--line);margin:36px 0 12px}
.manual li>ul,.manual li>ol{margin-top:.2em}
.dlrow{display:flex;flex-wrap:wrap;gap:8px;margin:10px 0 0}
.tpl{background:var(--porcelain);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin:16px 0}
.tpl h3{margin:0 0 4px}.tpl p{margin:.3em 0;font-size:.92rem;color:var(--sumi-soft)}
.tpl details{margin-top:10px}.tpl summary{cursor:pointer;color:var(--china);font-size:.9rem}
.tpl pre{white-space:pre-wrap;font-family:inherit;font-size:.86rem;line-height:1.8;background:#fff;border:1px solid var(--line);border-radius:8px;padding:14px 16px;margin-top:8px}
.btn.small{padding:7px 14px;font-size:.88rem;margin:0}
.pick{display:grid;grid-template-columns:repeat(3,1fr);gap:14px;margin:22px 0}
@media (max-width:700px){.pick{grid-template-columns:1fr}}
footer{padding:44px 0 56px;font-size:.82rem;color:var(--sumi-soft)}
@media (max-width:700px){.cards{grid-template-columns:1fr}}
"""

NOTE = '<div class="note">この記事は一般的な整理です。<b>提出先から個別の指示がある場合は、必ずそちらが優先</b>です。迷ったら提出先に確認してください。原本の提出が必要な書類を、黒塗りした写しで代えることはできません。</div>'

HOW = """
<h2>伏せ方（紙の場合・デジタルの場合）</h2>
<p><b>紙でコピーするなら</b>、原本の該当箇所に付箋や紙片を重ねてからコピーします。原本に直接ペンで塗らないでください。</p>
<p><b>写真や PDF で出すなら</b>、次の 2 つを満たす方法で塗ります。</p>
<ul>
<li>画像の画素そのものを、不透明な黒で置き換える（上に図形や注釈を載せるだけの方法は、あとから外せることがあります）</li>
<li>撮影日時や位置情報などのメタデータを、書き出したファイルに残さない</li>
</ul>
<p>くわしくは「<a href="kuronuri-anzen.html">復元できない黒塗りのやり方</a>」にまとめました。</p>
"""

CTA = """
<div class="cta">
<h2 class="mincho">道具を使うなら</h2>
<p><b>Fuseo（ふせお）</b>は、この整理をそのまま道具にした無料アプリです（Mac・iPhone。Windows 版は近日公開）。{fit}
塗る場所は、書き出す前に必ず自分で確かめます。塗った部分は画素に焼き込み、メタデータは引き継ぎません。処理はすべて端末の中で完結し、書類がどこかへ送られることはありません。</p>
<p>自動の検出は外れることがあります。漏えいを防ぐ道具ではなく、出す前に写っている情報を減らすための道具です。</p>
<a class="btn" href="../#download">Fuseo を入手する（無料）</a><a class="btn ghost" href="../features.html">機能のご紹介</a>
</div>
"""

PAGES = [
 dict(slug='mynumber-card', title='マイナンバーカードのコピーは、どこを隠して出す？（表・裏）',
  desc='マイナンバーカードのコピーや写真を提出するとき、裏面の個人番号と QR コードは隠します。表面で隠す欄・隠さない欄と、復元できない隠し方をまとめました。',
  short='マイナンバーカード', sub='裏の個人番号と QR コード。表は性別・臓器提供欄',
  answer='<b>裏面は、個人番号（12 桁）と QR コードの両方を隠します。</b>そもそも裏面は、番号の提出を求められたときだけ出す面です。本人確認だけなら、表面で足りることがほとんどです。<br><b>表面は、顔写真・氏名・住所・生年月日は隠しません。</b>提出先の指示があれば、性別欄と臓器提供意思表示欄を隠します。',
  body="""
<h2>隠す欄・隠さない欄</h2>
<div class="tablewrap"><table><thead><tr><th>面</th><th>隠す</th><th>隠さない</th></tr></thead><tbody>
<tr><td>裏</td><td>個人番号（12 桁）、QR コード</td><td>氏名、生年月日</td></tr>
<tr><td>表</td><td>提出先の指示があれば、性別欄・臓器提供意思表示欄</td><td>顔写真、氏名、住所、生年月日、有効期限</td></tr>
</tbody></table></div>
<h2>なぜ隠すのか</h2>
<p>個人番号は、番号法で、法令に定めのある場合以外の<b>収集・保管が禁止</b>されています。隠すのは自分を守るためだけでなく、受け取る側が「持ってはいけないもの」を受け取らずに済むようにするためでもあります。</p>
<p>裏面の <b>QR コードにも個人番号が入っています</b>。番号だけ塗って QR コードを残すと、意味がありません。両方隠します。</p>
<p>表面の臓器提供意思表示欄や性別欄は、機微な情報として、オンラインの本人確認で「覆った状態で提出してください」と指示されることがあります。</p>
<h2>そもそも裏面は出してよいのか</h2>
<p>マイナンバーカードの裏面は、法令で認められた者以外がコピー・保管することを禁じられています。賃貸の契約や会員登録など、番号を使わない手続きで裏面を求められたら、「表面だけでよいですか」と確認してみてください。</p>
<p>申し込みの画面で、カードの <b>IC チップを読み取る方式</b>（公的個人認証）を選べるときは、そちらを選ぶと、カードの画像そのものを相手に渡さずに済みます。</p>
""", fit='マイナンバーカードの裏面を読み込むと、個人番号と QR コードを塗る候補として示します。'),
 dict(slug='hokensho', title='健康保険証・資格確認書のコピーは、記号・番号を隠して出す',
  desc='健康保険証や資格確認書のコピーを本人確認で出すときは、保険者番号・記号・番号・QR コードを隠します。根拠（告知要求制限）と隠し方をまとめました。',
  short='健康保険証・資格確認書', sub='保険者番号、記号・番号、QR コード',
  answer='<b>保険者番号、被保険者の記号・番号、QR コード（バーコード）を隠します。</b>氏名・生年月日・保険者の名称は隠しません。資格確認書も同じです。',
  body="""
<h2>隠す欄・隠さない欄</h2>
<div class="tablewrap"><table><thead><tr><th>隠す</th><th>隠さない</th></tr></thead><tbody>
<tr><td>保険者番号、記号、番号（枝番）、QR コード・バーコード</td><td>氏名、生年月日、保険者の名称、交付日・有効期限</td></tr>
</tbody></table></div>
<h2>なぜ隠すのか</h2>
<p>2020 年 10 月から、健康保険証の<b>保険者番号と被保険者の記号・番号は「告知要求制限」の対象</b>です（健康保険法など）。保険の給付と関係のない場面で、他人がこれらを告知するよう求めることが制限されています。</p>
<p>総務省も、オンラインの本人確認で保険証の写しを使うときは、記号・番号を「復元できない程度に」マスキングするよう案内しています。QR コードやバーコードに記号・番号が入っている場合があるので、あわせて隠します。</p>
<h2>よくある場面</h2>
<ul>
<li>携帯電話の契約や口座開設で、補助の本人確認書類として出すとき</li>
<li>賃貸の申し込みや、勤務先・学校への届出に添えるとき</li>
</ul>
<p>提出先の案内に「記号・番号はマスキングしてください」と書かれていることが多いので、まず案内を読みます。病院の受付など、保険の給付に関わる場面では隠しません。</p>
""", fit='保険証や資格確認書を読み込むと、保険者番号・記号・番号・QR コードを塗る候補として示します（見つからない欄は、矩形やブラシで手で塗れます）。'),
 dict(slug='menkyosho', title='運転免許証のコピーは、どこを隠す？隠してはいけない欄は？',
  desc='運転免許証のコピーを出すとき、裏面の臓器提供意思表示欄は隠してよく、備考欄（住所変更）は隠してはいけません。免許証番号の扱いもまとめました。',
  short='運転免許証', sub='裏の臓器提供欄。備考欄は隠さない',
  answer='<b>裏面の臓器提供意思表示欄は、隠してかまいません。</b>隠すよう指示されることもあります。<br><b>裏面の備考欄（住所変更などの記載）は、隠してはいけません。</b>表面の顔写真・氏名・住所・有効期限も隠しません。<br>免許証番号は、提出先が番号まで必要としていないときだけ隠します。',
  body="""
<h2>隠す欄・隠さない欄</h2>
<div class="tablewrap"><table><thead><tr><th>面</th><th>隠してよい</th><th>隠さない</th></tr></thead><tbody>
<tr><td>表</td><td>提出先が不要とする場合は、免許証番号</td><td>顔写真、氏名、住所、生年月日、有効期限、免許の条件</td></tr>
<tr><td>裏</td><td>臓器提供意思表示欄</td><td><b>備考欄</b>（住所・氏名の変更の記載）</td></tr>
</tbody></table></div>
<h2>なぜそうなるのか</h2>
<p>臓器提供の意思は機微な情報です。携帯電話会社などのオンラインの本人確認では、この欄を覆った状態で提出するよう指示されることがあります。</p>
<p>備考欄には、引っ越しや改姓のあとの新しい住所・氏名が書かれます。<b>いまの住所を確かめるために使われる欄</b>なので、ここを塗ると確認ができなくなります。</p>
<p>免許証番号を隠すかどうかは、法律で決まっているわけではなく、実務の判断になります。カーシェアやレンタカーの会員登録のように、番号そのものを確認する手続きでは隠せません。番号が要らない手続きなら、隠しておくほうが安全です。</p>
<h2>迷ったら</h2>
<p>提出先の案内に何も書かれていなければ、「免許証番号は確認に使いますか」と聞いてしまうのが早いです。</p>
""", fit='運転免許証を読み込むと、免許証番号を塗る候補として示します（塗るかどうかは選べます）。裏面は、臓器提供意思表示欄を候補にし、備考欄は残します。'),
 dict(slug='juminhyo', title='住民票の写しを提出するとき、個人番号・本籍・続柄はどうする？',
  desc='住民票の写しは、請求するときに個人番号・本籍・続柄を「記載しない」にできます。すでに記載のある写しを出すときの隠し方もまとめました。',
  short='住民票の写し', sub='個人番号・本籍・続柄は、取る段階で減らせる',
  answer='<b>いちばん確実なのは、請求するときに「個人番号・本籍・続柄を記載しない」写しを取ることです。</b>本人だけの写しにするか、世帯全員にするかも選べます。<br>すでに個人番号が記載された写しを、番号の要らない手続きに出すときは、<b>個人番号（12 桁）を隠します。</b>',
  body="""
<h2>取る段階で減らす</h2>
<p>住民票の写しは、市区町村の窓口やコンビニで請求するときに、次のことを選べます。</p>
<ul>
<li>個人番号（マイナンバー）を記載するか</li>
<li>本籍・筆頭者を記載するか</li>
<li>世帯主・続柄を記載するか</li>
<li>本人だけにするか、世帯全員にするか</li>
</ul>
<p>提出先が「本籍入り」「世帯全員」などを指定していなければ、<b>載せない写しを取る</b>のがいちばん確実です。塗る手間もかからず、塗り残しも起きません。</p>
<h2>すでに記載のある写しを出すとき</h2>
<p>個人番号は、番号法で、法令に定めのある場合以外の収集・保管が禁止されています。個人情報保護委員会も、金融機関などに提出する住民票の写しについて、個人番号をマスキングするよう案内しています。求められる水準は「<b>復元できない程度に</b>」です。</p>
<p>本籍や続柄、同じ世帯のほかの人の欄は、提出先が求めていなければ出さなくてよい情報です。ただし、手続きによっては「省略のないもの」が必要なので、案内を確かめてから判断します。</p>
""", fit='住民票の写しを読み込むと、個人番号（12 桁）を塗る候補として示します。本籍や続柄などは、矩形やブラシで手で塗れます。'),
 dict(slug='pdf-kuronuri', title='控除証明書などの PDF を黒塗りして提出する方法（消えない黒塗りにする）',
  desc='保険料控除証明書・残高証明書などの PDF を、見せなくてよい欄を黒塗りして提出する方法。PDF に黒い四角を置くだけでは消えていない理由と、安全なやり方をまとめました。',
  short='PDF の書類（控除証明書など）', sub='黒い四角を置くだけでは消えていない',
  answer='<b>PDF の編集ソフトで黒い四角（注釈や図形）を上に置くだけでは、下の文字は消えていません。</b>四角を外したり、文字をコピーしたりすると読めてしまいます。<br>安全なのは、<b>ページを画像にして、画素そのものを黒で置き換えてから、新しい PDF として書き出す</b>方法です。',
  body="""
<h2>PDF の黒塗りで起きること</h2>
<p>電子で届く PDF（保険料控除証明書、残高証明書、利用明細など）には、見た目の下に<b>文字のデータ</b>が入っています。上に黒い四角を置いても、文字のデータはそのまま残ります。</p>
<ul>
<li>四角は「注釈」や「図形」なので、選んで削除できる</li>
<li>黒い四角の上からドラッグすると、下の文字を選んでコピーできる</li>
<li>検索すると、隠したはずの番号が見つかる</li>
</ul>
<p>こうした黒塗りによる情報の漏えいは、官公庁や企業でも繰り返し起きています。</p>
<h2>安全なやり方</h2>
<ol>
<li>PDF の各ページを画像にする（文字のデータを持たない状態にする）</li>
<li>見せなくてよい欄を、不透明な黒で塗りつぶす（画素を置き換える）</li>
<li>画像から新しい PDF を作る。元の PDF の文書情報や注釈は引き継がない</li>
</ol>
<p>印刷して、紙の上で付箋を重ねてコピーし、スキャンし直す方法でも同じことができます。</p>
<h2>どの欄を伏せてよいか</h2>
<p>伏せてよいのは、提出先がその書類で確かめたいこと以外の欄です。たとえば控除証明書なら、控除の金額と契約者の氏名が確認の対象になることが多く、関係のない契約の情報や口座の情報は求められていないことがあります。判断に迷う欄は、提出先に確認してください。</p>
<p>年末調整で勤務先に出す原本のように、<b>原本の提出が必要な書類は、黒塗りした写しで代えることはできません</b>。</p>
""", fit='PDF をそのまま読み込めます。各ページを画像として扱うので、塗った所の下に文字のデータは残りません。パスワード付きの PDF にも対応しています。'),
 dict(slug='kuronuri-anzen', title='復元できない黒塗りのやり方。やりがちな危ない黒塗り 4 つ',
  desc='マーカーで薄く塗る、PDF に黒い四角を置く、図形を重ねる、メタデータを残す。あとから読めてしまう黒塗りの例と、復元できない黒塗りの条件をまとめました。',
  short='復元できない黒塗り', sub='注釈・半透明・図形・メタデータに注意',
  answer='<b>復元できない黒塗りの条件は 2 つです。</b>画像の画素そのものを不透明な黒で置き換えること。そして、撮影日時や位置情報などのメタデータを、書き出したファイルに残さないこと。',
  body="""
<h2>やりがちな危ない黒塗り</h2>
<div class="tablewrap"><table><thead><tr><th>やり方</th><th>何が起きるか</th></tr></thead><tbody>
<tr><td>PDF に注釈や図形の黒い四角を置く</td><td>四角を選んで削除できる。下の文字のデータが残っていれば、コピーや検索で読める</td></tr>
<tr><td>薄いマーカー、半透明のペンでなぞる</td><td>画像の明るさやコントラストを変えると、下の文字が読めることがある</td></tr>
<tr><td>資料作成ソフトで、画像の上に図形を重ねて送る</td><td>受け取った側が図形をずらすと、下が見える</td></tr>
<tr><td>塗った画像を、そのまま送る</td><td>塗りは完全でも、撮影日時や位置情報（Exif）が残っていることがある</td></tr>
</tbody></table></div>
<h2>安全な黒塗りの条件</h2>
<ul>
<li><b>画素を置き換える</b>: 塗った所の元の色の情報が、ファイルのどこにも残っていない状態にします。上に重ねるのではなく、塗りつぶした結果を 1 枚の画像として保存します</li>
<li><b>メタデータを引き継がない</b>: 元の写真の撮影日時・位置情報・サムネイル、元の PDF の文書情報を、書き出したファイルに入れません</li>
<li><b>少し広めに塗る</b>: 文字の縁が残らないよう、欄より少し大きく塗ります</li>
</ul>
<p>法令やガイドラインで求められる水準は「復元できない程度に」です。見た目が黒いことではなく、あとから読めないことが条件になります。</p>
<h2>紙でやるなら</h2>
<p>原本の該当箇所に付箋や紙片を重ねてコピーします。原本に直接ペンで塗らないでください。コピーした紙を塗る場合は、裏から透けないよう、塗ったものをもう一度コピーして出すと確実です。</p>
""", fit='塗った部分は、画素そのものを黒に置き換えて書き出します。保存の前に、書き出す画像そのもので仕上がりを確かめる画面を必ず通ります。'),
]

def page(p):
    related = ''.join(f'<li><a href="{q["slug"]}.html">{html.escape(q["title"])}</a></li>' for q in PAGES if q['slug'] != p['slug'])
    how = '' if p['slug'] in ('kuronuri-anzen', 'pdf-kuronuri') else HOW
    ld = json.dumps({"@context": "https://schema.org", "@type": "Article", "headline": p['title'], "description": p['desc'],
                     "dateModified": UPDATED, "inLanguage": "ja", "mainEntityOfPage": BASE + p['slug'] + '.html',
                     "author": {"@type": "Organization", "name": "ATI株式会社", "url": "https://www.ati-mirai.co.jp/"},
                     "publisher": {"@type": "Organization", "name": "Fuseo（ATI株式会社）"}}, ensure_ascii=False)
    return f"""<!DOCTYPE html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(p['title'])} — Fuseo</title>
<meta name="description" content="{html.escape(p['desc'])}">
<link rel="icon" type="image/png" href="../assets/icon-256.png">
<link rel="canonical" href="{BASE}{p['slug']}.html">
<meta property="og:title" content="{html.escape(p['title'])}"><meta property="og:description" content="{html.escape(p['desc'])}">
<meta property="og:type" content="article"><meta property="og:url" content="{BASE}{p['slug']}.html"><meta property="og:image" content="https://fuseo.ati-mirai.co.jp/assets/icon.png">
<link rel="stylesheet" href="guide.css">
<script type="application/ld+json">{ld}</script>
</head><body>
{HEADER}
<main class="wrap">
<p class="crumb"><a href="../">Fuseo</a> › <a href="./">書類別の伏せ方ガイド</a> › {html.escape(p['short'])}</p>
<div class="eyebrow"><span class="bar"></span><span class="label">GUIDE</span></div>
<h1 class="mincho">{html.escape(p['title'])}</h1>
<div class="answer">{p['answer']}</div>
{NOTE}
{p['body']}
{how}
{CTA.format(fit=p['fit'])}
<h2>ほかの書類</h2>
<ul class="related">{related}</ul>
<p class="crumb">最終更新: {UPDATED}</p>
</main>
{FOOTER}
</body></html>
"""

def index():
    cards = ''.join(f'<a class="card" href="{p["slug"]}.html"><b>{html.escape(p["short"])}</b><span>{html.escape(p["sub"])}</span></a>' for p in PAGES)
    t = '書類別の伏せ方ガイド — 本人確認書類のコピーは、どこを隠して出す？'
    d = 'マイナンバーカード、健康保険証、運転免許証、住民票、PDF の書類。コピーや写真を提出するときに隠す欄・隠さない欄と、その根拠、復元できない隠し方を書類別にまとめました。'
    return f"""<!DOCTYPE html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{t} | Fuseo</title><meta name="description" content="{d}">
<link rel="icon" type="image/png" href="../assets/icon-256.png"><link rel="canonical" href="{BASE}">
<meta property="og:title" content="{t}"><meta property="og:description" content="{d}"><meta property="og:type" content="website"><meta property="og:url" content="{BASE}"><meta property="og:image" content="https://fuseo.ati-mirai.co.jp/assets/icon.png">
<link rel="stylesheet" href="guide.css"></head><body>
{HEADER}
<main class="wrap">
<p class="crumb"><a href="../">Fuseo</a> › 書類別の伏せ方ガイド</p>
<div class="eyebrow"><span class="bar"></span><span class="label">GUIDE</span></div>
<h1 class="mincho">書類別の伏せ方ガイド</h1>
<p>本人確認書類のコピーや写真を提出するとき、<b>どの欄を隠して、どの欄は隠してはいけないのか</b>を、書類別にまとめました。考え方は一つです。提出先がその書類で確かめたいこと以外の欄は、出す前に伏せてかまいません。</p>
{NOTE}
<h2>早見表</h2>
<div class="tablewrap"><table><thead><tr><th>書類</th><th>隠す</th><th>隠さない</th></tr></thead><tbody>
<tr><td><a href="mynumber-card.html">マイナンバーカード（裏）</a></td><td>個人番号（12 桁）、QR コード</td><td>氏名、生年月日</td></tr>
<tr><td><a href="mynumber-card.html">マイナンバーカード（表）</a></td><td>指示があれば性別欄・臓器提供意思表示欄</td><td>顔写真（本人確認に必要なことが多い）</td></tr>
<tr><td><a href="hokensho.html">健康保険証・資格確認書</a></td><td>保険者番号、記号・番号、QR コード</td><td>氏名、生年月日、保険者の名称</td></tr>
<tr><td><a href="menkyosho.html">運転免許証（表）</a></td><td>提出先が不要とする場合は免許証番号</td><td>顔写真、住所、有効期限</td></tr>
<tr><td><a href="menkyosho.html">運転免許証（裏）</a></td><td>臓器提供意思表示欄</td><td>備考欄（住所変更の確認に必要）</td></tr>
<tr><td><a href="juminhyo.html">住民票の写し</a></td><td>個人番号（12 桁）。本籍・続柄は取る段階で省ける</td><td>提出先が指定する記載事項</td></tr>
<tr><td>在留カード</td><td>提出先が不要とする場合は在留カード番号</td><td>顔写真、在留資格、期限</td></tr>
</tbody></table></div>
<h2>書類別に読む</h2>
<div class="cards">{cards}</div>
{CTA.format(fit='書類の種類を判定して、上の表の欄を塗る候補として示します。')}
<p class="crumb">最終更新: {UPDATED}</p>
</main>
{FOOTER}
</body></html>
"""

os.makedirs(ROOT, exist_ok=True)
open(os.path.join(ROOT, 'guide.css'), 'w').write(CSS.strip() + '\n')
open(os.path.join(ROOT, 'index.html'), 'w').write(index())
for p in PAGES:
    open(os.path.join(ROOT, p['slug'] + '.html'), 'w').write(page(p))
print('pages:', 1 + len(PAGES), [p['slug'] for p in PAGES])


# ---- 使い方（docs/user-manual*.md → site/manual/）----
import re
MROOT = os.path.join(ROOT, '..', 'manual')
MBASE = 'https://fuseo.ati-mirai.co.jp/manual/'
MANUALS = [
    dict(src='user-manual.md', slug='mac', short='Mac 版', title='Fuseo Mac 版の使い方', sub='macOS 14 以降',
         desc='Fuseo Mac 版の使い方。読み込み、確認画面の見かた、手で塗る、書類への文字入れ、PDF、一括処理、書き出し、作業の保存と再開。'),
    dict(src='user-manual-windows.md', slug='windows', short='Windows 版', title='Fuseo for Windows の使い方', sub='近日公開（電子署名の準備中）',
         desc='Fuseo for Windows の使い方。入手と起動、画面の見かた、切り抜き・回転、書類への文字入れ、フォルダの一括処理、書き出し、保存先。'),
    dict(src='user-manual-ios.md', slug='ios', short='iPhone 版', title='Fuseo iPhone 版の使い方', sub='iOS 17 以降',
         desc='Fuseo iPhone 版の使い方。カメラ・写真・ファイルからの読み込み、確認と塗り、書き出しと共有、作業の保存と再開、設定。'),
]

def md_to_html(text):
    import markdown
    # サイト内のリンクに直す
    text = text.replace('(user-manual-windows.md)', '(windows.html)').replace('(user-manual-ios.md)', '(ios.html)').replace('(user-manual.md)', '(./)')
    text = text.replace('https://fuseo.ati-mirai.co.jp/features.html', '../features.html')
    # 段落の直後に続く箇条書き（空行なし）を箇条書きとして読ませる
    out, prev = [], ''
    for line in text.split('\n'):
        nested = re.match(r'^( +)(([-*]|\d+\.)\s.*)$', line)     # 入れ子の箇条書き: 字下げを 2 倍にする（markdown は 4 つ必要）
        if nested:
            line = nested.group(1) * 2 + nested.group(2)
        is_item = re.match(r'^\s*([-*]|\d+\.)\s', line)
        prev_item = re.match(r'^\s*([-*]|\d+\.)\s', prev) or prev.startswith(' ')
        if is_item and prev.strip() and not prev_item:
            out.append('')
        out.append(line); prev = line
    body = markdown.markdown('\n'.join(out), extensions=['tables', 'sane_lists'])
    body = re.sub(r'<h1>.*?</h1>', '', body, count=1, flags=re.S)       # 題名はページ側で出す
    return body.replace('<table>', '<div class="tablewrap"><table>').replace('</table>', '</table></div>')

def mhead(title, desc, url):
    return f"""<!DOCTYPE html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(title)} | Fuseo</title><meta name="description" content="{html.escape(desc)}">
<link rel="icon" type="image/png" href="../assets/icon-256.png"><link rel="canonical" href="{url}">
<meta property="og:title" content="{html.escape(title)}"><meta property="og:description" content="{html.escape(desc)}"><meta property="og:type" content="article"><meta property="og:url" content="{url}"><meta property="og:image" content="https://fuseo.ati-mirai.co.jp/assets/icon.png">
<link rel="stylesheet" href="../guide/guide.css"></head><body>
{HEADER.replace('<a href="../guide/" aria-current="page">', '<a href="../guide/">').replace('<a href="../manual/">', '<a href="../manual/" aria-current="page">')}
"""

def build_manuals():
    try:
        import markdown  # noqa: F401
    except ImportError:
        print('manual: markdown が無いので飛ばした（uv pip install markdown）'); return
    docs = os.path.join(ROOT, '..', '..', 'docs')
    os.makedirs(MROOT, exist_ok=True)
    others = lambda cur: ' ・ '.join(f'<a href="{m["slug"]}.html">{m["short"]}</a>' for m in MANUALS if m['slug'] != cur)
    for m in MANUALS:
        body = md_to_html(open(os.path.join(docs, m['src'])).read())
        page_html = mhead(m['title'], m['desc'], MBASE + m['slug'] + '.html') + f"""<main class="wrap manual">
<p class="crumb"><a href="../">Fuseo</a> › <a href="./">使い方</a> › {m['short']}</p>
<div class="eyebrow"><span class="bar"></span><span class="label">MANUAL</span></div>
<h1 class="mincho">{html.escape(m['title'])}</h1>
<p class="crumb" style="margin-top:0">ほかの端末: {others(m['slug'])} ／ 画面つきの紹介は <a href="../features.html">機能のご紹介</a></p>
{body}
<p class="crumb">最終更新: {UPDATED}</p>
</main>
{FOOTER}
</body></html>
"""
        open(os.path.join(MROOT, m['slug'] + '.html'), 'w').write(page_html)
    cards = ''.join(f'<a class="card" href="{m["slug"]}.html"><b>{m["short"]}</b><span>{m["sub"]}</span></a>' for m in MANUALS)
    idx = mhead('Fuseo の使い方（Mac・Windows・iPhone）', 'Fuseo の使い方ガイド。Mac 版・Windows 版・iPhone 版の操作を、端末ごとにまとめました。', MBASE) + f"""<main class="wrap">
<p class="crumb"><a href="../">Fuseo</a> › 使い方</p>
<div class="eyebrow"><span class="bar"></span><span class="label">MANUAL</span></div>
<h1 class="mincho">使い方</h1>
<p>お使いの端末を選んでください。どの端末でも、流れは「読み込む → 塗る場所を確かめる → 書き出す」です。</p>
<div class="pick">{cards}</div>
<p>端末ごとにできることの違いと画面は「<a href="../features.html">機能のご紹介</a>」に、書類ごとにどの欄を伏せるかは「<a href="../guide/">書類別の伏せ方ガイド</a>」にまとめています。</p>
{NOTE}
</main>
{FOOTER}
</body></html>
"""
    open(os.path.join(MROOT, 'index.html'), 'w').write(idx)
    print('manual pages:', 1 + len(MANUALS))

build_manuals()

TROOT = os.path.join(ROOT, '..', 'terms')
def build_terms():
    """利用条件（docs/legal/TERMS.md → site/terms/index.html）。"""
    try:
        import markdown  # noqa: F401
    except ImportError:
        print('terms: markdown が無いので飛ばした'); return
    src = open(os.path.join(ROOT, '..', '..', 'docs', 'legal', 'TERMS.md')).read()
    body = md_to_html(src)
    url = 'https://fuseo.ati-mirai.co.jp/terms/'
    head = mhead('Fuseo 利用条件', 'Fuseo（本人確認書類の黒塗りアプリ）の利用条件。料金、使ってよい範囲、黒塗りの結果、保証しないことと責任の範囲など。', url)
    head = head.replace('<a href="../manual/" aria-current="page">', '<a href="../manual/">')
    os.makedirs(TROOT, exist_ok=True)
    open(os.path.join(TROOT, 'index.html'), 'w').write(head + f"""<main class="wrap manual">
<p class="crumb"><a href="../">Fuseo</a> › 利用条件</p>
<div class="eyebrow"><span class="bar"></span><span class="label">TERMS</span></div>
<h1 class="mincho">利用条件</h1>
{body}
<p class="crumb">プライバシーの方針は <a href="https://github.com/supergodak/fuseo/blob/main/PRIVACY.md">プライバシーポリシー</a> をご覧ください。</p>
</main>
{FOOTER}
</body></html>
""")
    print('terms: 1 page')

build_terms()


# ---- 書類を集める方へ（site/collect/）----
CROOT = os.path.join(ROOT, '..', 'collect')
CBASE = 'https://fuseo.ati-mirai.co.jp/collect/'
TPL_FOOT = """
――――――――――
このひな形は、ATI株式会社が無償で公開しているものです（https://fuseo.ati-mirai.co.jp/collect/）。
自由に書き換えて、社内外に配布していただけます。この注記は消してかまいません。
一般的な整理であり、法的な助言ではありません。必要な欄は手続きごとに違いますので、貴社の手続きに合わせて直してください。"""

TEMPLATES = [
 dict(slug='nyusha', name='入社・社内手続き用（人事・総務）', file='fuseo-template-nyusha',
  lead='入社手続きや社内の届出で、書類の写しを出してもらうときの案内文です。',
  text="""【ご提出書類についてのお願い】

[会社名] [部署名]

[入社手続き／○○の届出]にあたり、次の書類の写しをご提出いただきます。
当社でお預かりする情報を必要な範囲にとどめるため、ご提出の前に、下の「伏せていただきたい欄」を隠してください。

■ ご提出いただく書類と、伏せていただきたい欄
（※貴社の手続きに合わせて、不要な行は消し、必要な行を足してください）

1. 身元確認の書類（運転免許証・マイナンバーカードの表面 など）
   ・伏せる: 運転免許証の裏面の「臓器提供意思表示欄」／マイナンバーカード表面の「臓器提供意思表示欄」
   ・伏せない: 氏名、住所、生年月日、顔写真、有効期限、免許証裏面の備考欄（住所変更の記載）
   ・マイナンバーカードの裏面（個人番号の面）は、この書類としては提出しないでください

2. 住所確認の書類（住民票の写し など）
   ・「個人番号」「本籍」「続柄」の記載がないものを取得してください
   ・すでに記載のある写しを出す場合は、その欄を伏せてください

3. 健康保険証・資格確認書の写し（身元確認として出す場合）
   ・伏せる: 保険者番号、記号・番号、QR コード
   ・伏せない: 氏名、生年月日

4. 給与振込口座の確認（通帳やキャッシュカードの写し）
   ・必要なのは、金融機関名・支店名・口座種別・口座番号・名義です
   ・残高や取引の履歴が写ったページは提出しないでください

■ マイナンバー（個人番号）のご提出について
・マイナンバーは、[当社指定の方法（例: 専用フォーム／封をした書面）]でのみご提出ください
・メールやチャットに、番号の写った画像を貼り付けないでください
・上の 1〜4 の書類に、マイナンバーが写り込まないようにしてください

■ 伏せ方
・紙の場合: 原本の該当箇所に付箋や紙片を重ねてからコピーしてください（原本に直接書き込まないでください）
・画像や PDF の場合: 上から図形を重ねるだけでは、あとで外せることがあります。画像そのものを黒く塗りつぶし、新しいファイルとして保存してください
  （例: 無料ソフト「Fuseo」 https://fuseo.ati-mirai.co.jp/ ／スマートフォンの写真編集でも、塗りつぶして別名で保存すれば可）

■ ご不明な点
どの欄が必要か迷う場合は、伏せる前に[担当: ○○／連絡先]までお問い合わせください。
"""),
 dict(slug='moushikomi', name='お申込み・ご契約用（本人確認書類のお願い）', file='fuseo-template-moushikomi',
  lead='お客様や会員に、本人確認書類の写しを出してもらうときの案内文です（入居申込、会員登録、各種の申込みなど）。',
  text="""【本人確認書類のご提出についてのお願い】

[事業者名]

[お申込み／ご契約]にあたり、ご本人であることの確認のため、次のいずれかの書類の写し（画像）をご提出ください。
お預かりする情報を必要な範囲にとどめるため、ご提出の前に、下の「伏せていただきたい欄」を隠してください。

■ ご提出いただける書類と、伏せていただきたい欄
（※貴社で確認に使う欄に合わせて、書き換えてください）

・運転免許証（表・裏）
   伏せる: 裏面の「臓器提供意思表示欄」[／免許証番号（当社では確認に使いません）]
   伏せない: 氏名、住所、生年月日、顔写真、有効期限、裏面の備考欄

・マイナンバーカード（表面のみ）
   伏せる: 「臓器提供意思表示欄」[／性別]
   裏面（個人番号の面）は提出しないでください。当社は個人番号をお預かりできません

・健康保険証・資格確認書
   伏せる: 保険者番号、記号・番号、QR コード
   伏せない: 氏名、生年月日、住所欄

・住民票の写し（補助書類として）
   「個人番号」「本籍」「続柄」の記載がないものをご用意ください

■ 伏せ方
・紙の場合: 原本の該当箇所に付箋や紙片を重ねてからコピーしてください
・画像や PDF の場合: 上から図形を重ねるだけでは、あとで外せることがあります。画像そのものを黒く塗りつぶし、新しいファイルとして保存してください
  （例: 無料ソフト「Fuseo」 https://fuseo.ati-mirai.co.jp/ ）

■ 伏せてはいけない欄を伏せた場合
確認ができないため、再提出をお願いすることがあります。迷う場合は、伏せる前に[窓口／連絡先]までお問い合わせください。
"""),
 dict(slug='tantousha', name='担当者用: 受け取った書類に余計な情報が写っていたとき', file='fuseo-template-tantousha',
  lead='届いた写しに、マイナンバーや保険証の記号・番号などが写っていたときの、社内の手順のたたき台です。',
  text="""【受け取った書類に、不要な情報が写っていたときの手順（担当者用）】

[会社名] [部署名]　制定: [日付]

■ 対象
本人確認などの目的で受け取った書類の写し（紙・画像・PDF）に、その手続きでは使わない次の情報が写っていた場合。
・個人番号（マイナンバー）　※番号を扱う手続き（税・社会保険）で、所定の方法により受け取ったものは除く
・健康保険証・資格確認書の保険者番号、記号・番号、QR コード
・[その他、当社で受け取らないと決めた欄: 本籍、続柄、臓器提供意思表示欄 など]

■ 手順
1. 気づいた時点で、そのファイル（紙）を、共有フォルダやチャットなどに転送・複製しない
2. 次のどちらかで対応する
   (a) 該当の欄を、復元できない形で塗りつぶした写しを作り、元のファイル（紙）は削除（裁断）する
   (b) 元のファイル（紙）を削除（裁断）し、提出者に、該当の欄を伏せた写しの再提出をお願いする
3. メールに添付されて届いた場合は、受信したメールの添付ファイルも削除する
4. 対応した日付と内容を、[台帳／管理表]に記録する

■ 塗りつぶすときの注意
・PDF や画像の上に、図形や注釈を重ねるだけにしない（あとで外せます）
・塗りつぶした結果を、新しいファイルとして保存する。元のファイルを上書きせず、確認してから元を削除する
・社外のサービスに書類をアップロードして加工しない
  （例: 端末内だけで処理する無料ソフト「Fuseo」 https://fuseo.ati-mirai.co.jp/ 。フォルダごとまとめて処理できます）

■ 再発を防ぐために
・提出のお願いに、伏せていただきたい欄を書いておく（ひな形: https://fuseo.ati-mirai.co.jp/collect/）
"""),
]

def write_docx(path, title, text):
    try:
        import docx
        from docx.shared import Pt
        from docx.oxml.ns import qn
    except ImportError:
        return False
    d = docx.Document()
    st = d.styles['Normal']; st.font.name = 'Yu Gothic'; st.font.size = Pt(10.5)
    st.element.rPr.rFonts.set(qn('w:eastAsia'), '游ゴシック')
    lines = text.strip().split('\n')
    for i, line in enumerate(lines):
        par = d.add_paragraph()
        run = par.add_run(line)
        par.paragraph_format.space_after = Pt(0)
        if i == 0:
            run.bold = True; run.font.size = Pt(14)
        elif line.startswith('■'):
            run.bold = True
    d.core_properties.author = ''; d.core_properties.title = title; d.core_properties.comments = ''
    d.save(path); return True

def build_collect():
    files = os.path.join(CROOT, 'files'); os.makedirs(files, exist_ok=True)
    blocks = ''
    for t in TEMPLATES:
        full = t['text'].strip() + '\n' + TPL_FOOT
        open(os.path.join(files, t['file'] + '.txt'), 'w', encoding='utf-8-sig').write(full.replace('\n', '\r\n'))
        has_docx = write_docx(os.path.join(files, t['file'] + '.docx'), t['name'], full)
        word = f'<a class="btn small" href="files/{t["file"]}.docx" download>Word 形式</a>' if has_docx else ''
        blocks += f"""<div class="tpl" id="{t['slug']}">
<h3>{html.escape(t['name'])}</h3>
<p>{html.escape(t['lead'])}</p>
<div class="dlrow">{word}<a class="btn small ghost" href="files/{t['file']}.txt" download>テキスト形式</a></div>
<details><summary>内容を見る</summary><pre>{html.escape(full)}</pre></details>
</div>
"""
    title = '書類を集める方へ — 提出前に伏せてもらう案内文のひな形（無料）'
    desc = '人事・総務や窓口のご担当者向け。本人確認書類の写しを集めるときに、マイナンバーや保険証の記号・番号など、受け取らなくてよい欄を提出前に伏せてもらうための案内文のひな形と、受け取った後の手順をまとめました。'
    head = mhead(title, desc, CBASE).replace('<a href="../manual/" aria-current="page">', '<a href="../manual/">').replace('<a href="../collect/">集める方へ</a>', '<a href="../collect/" aria-current="page">集める方へ</a>')
    page_html = head + f"""<main class="wrap">
<p class="crumb"><a href="../">Fuseo</a> › 書類を集める方へ</p>
<div class="eyebrow"><span class="bar"></span><span class="label">FOR COLLECTORS</span></div>
<h1 class="mincho">書類を集める方へ</h1>
<div class="answer"><b>受け取らなくてよい情報は、提出の前に伏せてもらう。</b>そのための案内文のひな形を、無料で公開しています。入社手続きや申込みで本人確認書類の写しを集める、人事・総務や窓口のご担当者向けです。自由に書き換えて配布できます。</div>

<h2>なぜ、出す前に伏せてもらうのか</h2>
<ul>
<li><b>受け取ってはいけない情報がある</b>: 個人番号（マイナンバー）は、番号法で、法令に定めのある場合以外の収集・保管が禁じられています。本人確認のために受け取った写しに番号が写っていると、受け取った側の問題になります。健康保険証の保険者番号と記号・番号も、保険の給付と関係のない場面で告知を求めることが制限されています</li>
<li><b>受け取った後の手間が減る</b>: 番号の写った写しが届くと、担当者が塗りつぶすか、出し直しをお願いすることになります。最初の案内に書いておけば、その往復が減ります</li>
<li><b>漏えいしたときの影響を小さくできる</b>: 預かる情報が少ないほど、万一のときに外へ出る情報も、説明の負担も小さくなります</li>
</ul>
<p>どの欄が必要かは、手続きごとに違います。ひな形は、<b>貴社で確認に使う欄に合わせて書き換える前提</b>で作っています。書類ごとの考え方は「<a href="../guide/">書類別の伏せ方ガイド</a>」をご覧ください。</p>

<h2>案内文のひな形</h2>
<p>Word 形式とテキスト形式があります。［　］の部分を書き換えてお使いください。Fuseo の名前や末尾の注記は、消してかまいません。</p>
{blocks}

<h2>受け取った後に、担当者が伏せる</h2>
<p>すでに届いた写しに不要な情報が写っている場合は、保管する前に担当者が塗りつぶす方法もあります。<b>Fuseo</b> は、そのための無料の道具としても使えます。</p>
<ul>
<li>フォルダごと読み込んで、一覧で確かめながら、まとめて処理できます（Mac。Windows 版は近日公開）</li>
<li>マイナンバー、保険証の記号・番号、QR コードなどを、塗る候補として示します。候補は外れることがあるので、保存の前に必ず担当者が確かめます</li>
<li>塗った部分は画素そのものを黒に置き換えて書き出します。元のファイルは上書きしません</li>
</ul>
<p>操作は「<a href="../manual/">使い方</a>」、画面は「<a href="../features.html">機能のご紹介</a>」にあります。</p>

<h2>社内で使う前に確かめたいこと</h2>
<div class="tablewrap"><table><tbody>
<tr><th>通信</th><td>書類の画像や内容を、外部に送信しません。Mac 版の通信は、新しい版の有無を確かめるアップデート確認だけです（自動で確かめるかは初回に尋ねます）。iPhone 版に独自の通信はありません</td></tr>
<tr><th>データの保存先</th><td>作業中の書類は、その端末の中だけに保存します。アプリの一覧から削除できます。書き出したファイルは、利用者が選んだ場所だけに保存します</td></tr>
<tr><th>費用・ライセンス</th><td>無料です。Mac 版はオープンソース（MIT License）で、業務でも使えます。アカウント登録や広告はありません</td></tr>
<tr><th>導入</th><td>Mac: 公証済みのアプリ（macOS 14 以降）。iPhone: App Store（iOS 17 以降）</td></tr>
<tr><th>Windows 版</th><td>近日公開です。電子署名（発行元の証明）を付けてから配布します。インストーラなしで、zip を展開して起動する形の予定です</td></tr>
<tr><th>ソースコード</th><td><a href="https://github.com/supergodak/fuseo">GitHub</a> で公開しています。プライバシーの方針は <a href="https://github.com/supergodak/fuseo/blob/main/PRIVACY.md">PRIVACY.md</a> にあります</td></tr>
</tbody></table></div>

<div class="note">このページとひな形は一般的な整理で、<b>法的な助言ではありません</b>。必要な欄や保存の義務は、手続きや業種によって違います。Fuseo の自動の検出は外れることがあり、漏えいを防ぐものでも、法令への対応を保証するものでもありません。ひな形は自由に改変・再配布できます（出典の表示は不要です）。</div>

<a class="btn" href="../#download">Fuseo を入手する（無料）</a><a class="btn ghost" href="../guide/">書類別の伏せ方ガイド</a>
<p class="crumb">最終更新: {UPDATED}</p>
</main>
{FOOTER}
</body></html>
"""
    open(os.path.join(CROOT, 'index.html'), 'w').write(page_html)
    print('collect: 1 page,', len(TEMPLATES), 'templates')

build_collect()
