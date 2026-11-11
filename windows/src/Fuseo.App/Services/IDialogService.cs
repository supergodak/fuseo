using Fuseo.App.ViewModels;
using Fuseo.Core.Contracts;
using Fuseo.Core.Geometry;
using Fuseo.Core.Imaging;

namespace Fuseo.App.Services;

/// <summary>
/// ダイアログの抽象（ViewModel をウィンドウ無しで単体テストできるように）。
/// 本物は <see cref="WpfDialogService"/>。
/// </summary>
/// <summary>切り抜き画面に渡すもの: 切り抜く前の画像（いまの向きに回したもの）と、その上の現在の四隅。</summary>
public sealed record CropRequest(PageBitmap Image, Quad Initial);

public interface IDialogService
{
    /// <summary>「ファイルを開く」（複数選択・画像と PDF）。取り消しなら null。</summary>
    IReadOnlyList<string>? AskOpenFiles();

    /// <summary>
    /// パスワード入力。<see cref="IPageSource.LoadAsync"/> の askPassword にそのまま渡す。
    /// 入力値は戻り値でだけ返し、どこにも保持しない。取り消しなら null。
    /// </summary>
    Task<string?> AskPasswordAsync(PasswordRequest request);

    /// <summary>保存ダイアログ（上書き確認は標準のもの）。取り消しなら null。</summary>
    string? AskSavePath(ExportFormat format, string defaultFileName);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    /// <summary>書き出しシートをモーダルで開く（閉じるまで戻らない）。</summary>
    void ShowExport(ExportViewModel viewModel);

    /// <summary>設定ウィンドウをモーダルで開く（W6）。</summary>
    void ShowSettings(SettingsViewModel viewModel);

    /// <summary>バージョン情報をモーダルで開く（W6）。</summary>
    void ShowAbout();

    /// <summary>
    /// 4 隅切り抜きの画面をモーダルで開く（W4.5）。<see cref="CropRequest.Image"/> の上の四隅を返す
    /// （凸四角形のときだけ返る。全体 = <see cref="Quad.FullImage"/>）。取り消しなら null。
    /// </summary>
    Quad? AskCrop(CropRequest request);

    /// <summary>
    /// フォルダを選ぶ（W9-4: 「フォルダを開く」と「書類ごとに分ける」の保存先。<c>Microsoft.Win32.OpenFolderDialog</c>）。取り消しなら null。
    /// </summary>
    string? AskFolder(string title);

    /// <summary>ローカルのフォルダをエクスプローラーで開く（URL は渡さない・<see cref="FolderLauncher"/>）。</summary>
    void OpenFolder(string path);

    /// <summary>
    /// 文字の「その他の色」（v0.3）。Windows 標準の色の設定（ChooseColor）を開く。選んだ色・取り消しなら null。
    /// </summary>
    Fuseo.Core.Model.TextColor? AskColor(Fuseo.Core.Model.TextColor initial);
}
