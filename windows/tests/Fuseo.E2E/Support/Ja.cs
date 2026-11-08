namespace Fuseo.E2E;

/// <summary>
/// 画面の日本語の文言（ウィンドウ・ダイアログのタイトルなど、AutomationId を付けられない所を探すのに使う）。
/// 元は src/Fuseo.App/Properties/Strings.resx。文言を変えたらここも直す。
/// </summary>
internal static class Ja
{
    public const string OpenDialog = "画像か PDF を開く";
    public const string SaveDialog = "書き出し先";
    public const string ExportWindow = "書き出し";
    public const string CropWindow = "切り抜き";
    public const string SettingsWindow = "設定";
    public const string AboutWindow = "バージョン情報";
    public const string PasswordWindow = "パスワード";

    public const string ImportErrorTitle = "読み込めませんでした";
    public const string CropConfirmTitle = "切り抜きを変更";
    public const string LibraryDeleteTitle = "作業の削除";
    public const string LibraryDeleteAllTitle = "すべての作業の削除";
    public const string ExportOverwriteTitle = "上書きの確認";

    public const string NoMask = "マスクなし";
    public static string MaskCount(int n) => n == 0 ? NoMask : $"マスク {n}";
    public static string Imported(int n) => $"{n} ページを読み込みました";
    public static string ExportedOne(string file) => $"書き出しました: {file}";

    public const string ExportSave = "保存…";
    public const string ExportSaveWithoutMask = "マスクなしのページも含めて保存…";

    public const string LibraryInProgress = "作業中";
    public const string LibraryExported = "書き出し済み";
    public static string LibraryPages(int n) => $"{n} ページ";

    public const string SettingsBlocked = "書類を開いている間は削除できません。「新しい書類」で閉じてから削除してください。";
    public const string SettingsNoWorks = "保存されている作業はありません。";
    public static string SettingsWorkCount(int n) => $"保存されている作業: {n} 件";
    public const string LanguageEnglish = "English";

    public const string TypeMyNumberBack = "マイナンバーカード（裏）";
    public const string TypeLicenseBack = "運転免許証（裏）";
    public const string TypeGeneric = "その他の書類";
    public const string TypeMyNumberFront = "マイナンバーカード（表）";
    public const string TypeLicenseFront = "運転免許証（表）";

    // ---- 文字認識・フォルダ・一覧・書類ごとの書き出し（W9-3〜W9-5） ----
    public const string RuleMyNumber = "個人番号（マイナンバー）";
    public const string RuleLicenseNumber = "免許証番号";
    public const string RuleGender = "性別";
    public const string RuleOrganDonation = "臓器提供意思欄";
    public const string StatusReread = "文字を読み直し、候補を作り直しました。";
    public const string NoteAuto = "自動で探します";
    public const string NotePositioned = "位置で候補にしています";
    public const string NoteCropForPosition = "切り抜くと位置で候補になります";
    public const string NoteTextThenPosition = "自動で探します（見つからなければ位置で候補にします）";
    public const string OpenFolderDialog = "開くフォルダを選んでください";
    public const string ExportFolderDialog = "保存先のフォルダを選んでください";
    public const string OpenedAsNewWork = "新しい作業として開きました（前の作業は一覧に残っています）。";
    public static string SkippedUnsupported(int n) => $"対応していないファイル {n} 件は飛ばしました。";
    public static string GridTitle(string file, int p, int n) => $"{file}（{p}/{n}）";
    public static string SeparateCompleted(int n) => $"{n} 件を保存しました。";
    public static string ExportedSeparate(int n, string folder) => $"{n} 件を保存しました（保存先: {folder}）";
}
