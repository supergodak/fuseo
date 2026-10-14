using System.Globalization;
using System.Resources;

namespace Fuseo.App.Properties;

/// <summary>
/// UI 文言の入口（Properties/Strings.resx）。XAML からは {x:Static p:Strings.Key} で参照する。
/// Visual Studio の自動生成に頼らず手で持つ（dotnet CLI だけでビルドするため）。キーは resx と 1 対 1（StringsTests で検査）。
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("Fuseo.App.Properties.Strings", typeof(Strings).Assembly);

    /// <summary>
    /// 表示言語のカルチャ（起動時に Configuration.UiLanguage.Resolve の結果を入れる）。
    /// null なら日本語（ニュートラル）。OS の UI 言語に暗黙に引きずられないよう、null のときは InvariantCulture で引く
    /// （英語 OS でテストしても日本語の文言・<see cref="IsEnglish"/>=false で一致する）。
    /// </summary>
    public static CultureInfo? Culture { get; set; }

    /// <summary>英語 UI か（プリセットの *En 項目を使うか）。<see cref="Culture"/> と必ず一致させる。</summary>
    public static bool IsEnglish => IsEnglishCulture(Culture);

    public static bool IsEnglishCulture(CultureInfo? culture) => culture?.TwoLetterISOLanguageName == "en";

    /// <summary>日付・数値の書式に使うカルチャ（表示言語と同じ）。</summary>
    public static CultureInfo FormatCulture => Culture ?? CultureInfo.GetCultureInfo("ja-JP");

    public static string Get(string key) => Get(key, Culture);

    /// <summary>カルチャを指定して引く（テスト・言語を明示したい箇所用。グローバルの <see cref="Culture"/> を変えない）。</summary>
    public static string Get(string key, CultureInfo? culture) =>
        Manager.GetString(key, culture ?? CultureInfo.InvariantCulture)
        ?? throw new MissingManifestResourceException($"文言が無い: {key}");

    public static string AppTitle => Get(nameof(AppTitle));
    public static string Ok => Get(nameof(Ok));
    public static string Cancel => Get(nameof(Cancel));
    public static string UnexpectedError => Get(nameof(UnexpectedError));
    public static string OpenFiles => Get(nameof(OpenFiles));
    public static string OpenFilesTip => Get(nameof(OpenFilesTip));
    public static string ToolLabel => Get(nameof(ToolLabel));
    public static string ToolSelect => Get(nameof(ToolSelect));
    public static string ToolSelectTip => Get(nameof(ToolSelectTip));
    public static string ToolRect => Get(nameof(ToolRect));
    public static string ToolRectTip => Get(nameof(ToolRectTip));
    public static string ToolBrush => Get(nameof(ToolBrush));
    public static string ToolBrushTip => Get(nameof(ToolBrushTip));
    public static string BrushSizeLabel => Get(nameof(BrushSizeLabel));
    public static string BrushThin => Get(nameof(BrushThin));
    public static string BrushMedium => Get(nameof(BrushMedium));
    public static string BrushThick => Get(nameof(BrushThick));
    public static string Undo => Get(nameof(Undo));
    public static string UndoTip => Get(nameof(UndoTip));
    public static string Redo => Get(nameof(Redo));
    public static string RedoTip => Get(nameof(RedoTip));
    public static string DeleteSelection => Get(nameof(DeleteSelection));
    public static string DeleteSelectionTip => Get(nameof(DeleteSelectionTip));
    public static string RotateLeft => Get(nameof(RotateLeft));
    public static string RotateLeftTip => Get(nameof(RotateLeftTip));
    public static string RotateRight => Get(nameof(RotateRight));
    public static string RotateRightTip => Get(nameof(RotateRightTip));
    public static string ZoomOut => Get(nameof(ZoomOut));
    public static string ZoomIn => Get(nameof(ZoomIn));
    public static string ZoomFit => Get(nameof(ZoomFit));
    public static string ZoomTip => Get(nameof(ZoomTip));
    public static string ExportButton => Get(nameof(ExportButton));
    public static string EmptyTitle => Get(nameof(EmptyTitle));
    public static string EmptySubtitle => Get(nameof(EmptySubtitle));
    public static string PagesHeading => Get(nameof(PagesHeading));
    public static string MoveUp => Get(nameof(MoveUp));
    public static string MoveDown => Get(nameof(MoveDown));
    public static string RemovePage => Get(nameof(RemovePage));
    public static string PageTitleImage => Get(nameof(PageTitleImage));
    public static string PageTitlePdf => Get(nameof(PageTitlePdf));
    public static string PageNoMask => Get(nameof(PageNoMask));
    public static string PageMaskCount => Get(nameof(PageMaskCount));
    public static string DocumentTypeLabel => Get(nameof(DocumentTypeLabel));
    public static string ChecklistHeading => Get(nameof(ChecklistHeading));
    public static string ChecklistNote => Get(nameof(ChecklistNote));
    public static string ChecklistManualGuide => Get(nameof(ChecklistManualGuide));
    public static string ChecklistQrNote => Get(nameof(ChecklistQrNote));
    public static string CandidatesHeading => Get(nameof(CandidatesHeading));
    public static string CandidatesEmpty => Get(nameof(CandidatesEmpty));
    public static string BannerClose => Get(nameof(BannerClose));
    public static string StatusImporting => Get(nameof(StatusImporting));
    public static string StatusImported => Get(nameof(StatusImported));
    public static string StatusDetecting => Get(nameof(StatusDetecting));
    public static string StatusDetectFailed => Get(nameof(StatusDetectFailed));
    public static string StatusExportedOne => Get(nameof(StatusExportedOne));
    public static string StatusExportedMany => Get(nameof(StatusExportedMany));
    public static string OpenFilesTitle => Get(nameof(OpenFilesTitle));
    public static string OpenFilesFilter => Get(nameof(OpenFilesFilter));
    public static string ImportErrorTitle => Get(nameof(ImportErrorTitle));
    public static string ImportErrorNotSupported => Get(nameof(ImportErrorNotSupported));
    public static string ImportErrorNotFound => Get(nameof(ImportErrorNotFound));
    public static string ImportErrorUnreadable => Get(nameof(ImportErrorUnreadable));
    public static string ImportErrorGeneric => Get(nameof(ImportErrorGeneric));
    public static string ImportErrorPageLimit => Get(nameof(ImportErrorPageLimit));
    public static string TempSourcePdfUnsupported => Get(nameof(TempSourcePdfUnsupported));
    public static string PasswordTitle => Get(nameof(PasswordTitle));
    public static string PasswordMessage => Get(nameof(PasswordMessage));
    public static string PasswordRetryMessage => Get(nameof(PasswordRetryMessage));
    public static string PasswordNote => Get(nameof(PasswordNote));
    public static string ExportTitle => Get(nameof(ExportTitle));
    public static string ExportFormatLabel => Get(nameof(ExportFormatLabel));
    public static string ExportPdfNote => Get(nameof(ExportPdfNote));
    public static string ExportSeparateFilesNote => Get(nameof(ExportSeparateFilesNote));
    public static string ExportPreviewHeading => Get(nameof(ExportPreviewHeading));
    public static string ExportPreviewNote => Get(nameof(ExportPreviewNote));
    public static string ExportSummary => Get(nameof(ExportSummary));
    public static string PreviewPageTitle => Get(nameof(PreviewPageTitle));
    public static string ExportPreparing => Get(nameof(ExportPreparing));
    public static string ExportZeroMaskWarning => Get(nameof(ExportZeroMaskWarning));
    public static string ExportSave => Get(nameof(ExportSave));
    public static string ExportSaveWithoutMask => Get(nameof(ExportSaveWithoutMask));
    public static string ExportSaving => Get(nameof(ExportSaving));
    public static string ExportErrorTitle => Get(nameof(ExportErrorTitle));
    public static string ExportPrepareFailed => Get(nameof(ExportPrepareFailed));
    public static string ExportFailed => Get(nameof(ExportFailed));
    public static string ExportOverwriteTitle => Get(nameof(ExportOverwriteTitle));
    public static string ExportOverwriteMessage => Get(nameof(ExportOverwriteMessage));
    public static string SaveDialogTitle => Get(nameof(SaveDialogTitle));
    public static string SaveFilterPdf => Get(nameof(SaveFilterPdf));
    public static string SaveFilterJpeg => Get(nameof(SaveFilterJpeg));
    public static string SaveFilterPng => Get(nameof(SaveFilterPng));
    public static string DefaultExportBaseName => Get(nameof(DefaultExportBaseName));
    public static string NewDocument => Get(nameof(NewDocument));
    public static string NewDocumentTip => Get(nameof(NewDocumentTip));
    public static string WorkTitleMany => Get(nameof(WorkTitleMany));
    public static string LibraryHeading => Get(nameof(LibraryHeading));
    public static string LibraryStorageNote => Get(nameof(LibraryStorageNote));
    public static string LibraryOpen => Get(nameof(LibraryOpen));
    public static string LibraryOpenTip => Get(nameof(LibraryOpenTip));
    public static string LibraryDelete => Get(nameof(LibraryDelete));
    public static string LibraryDeleteAll => Get(nameof(LibraryDeleteAll));
    public static string LibraryUpdated => Get(nameof(LibraryUpdated));
    public static string LibraryPageCount => Get(nameof(LibraryPageCount));
    public static string LibraryStatusInProgress => Get(nameof(LibraryStatusInProgress));
    public static string LibraryStatusExported => Get(nameof(LibraryStatusExported));
    public static string LibraryUnreadable => Get(nameof(LibraryUnreadable));
    public static string LibraryUnreadableNote => Get(nameof(LibraryUnreadableNote));
    public static string LibraryDeleteTitle => Get(nameof(LibraryDeleteTitle));
    public static string LibraryDeleteMessage => Get(nameof(LibraryDeleteMessage));
    public static string LibraryDeleteAllTitle => Get(nameof(LibraryDeleteAllTitle));
    public static string LibraryDeleteAllMessage => Get(nameof(LibraryDeleteAllMessage));
    public static string LibraryDeleteFailedTitle => Get(nameof(LibraryDeleteFailedTitle));
    public static string LibraryDeleteFailed => Get(nameof(LibraryDeleteFailed));
    public static string LibraryOpenFailedTitle => Get(nameof(LibraryOpenFailedTitle));
    public static string LibraryOpenFailed => Get(nameof(LibraryOpenFailed));
    public static string StatusWorkOpened => Get(nameof(StatusWorkOpened));
    public static string StatusSaveFailed => Get(nameof(StatusSaveFailed));
    public static string StatusLibraryFailed => Get(nameof(StatusLibraryFailed));
    public static string Settings => Get(nameof(Settings));
    public static string SettingsTip => Get(nameof(SettingsTip));
    public static string About => Get(nameof(About));
    public static string AboutTip => Get(nameof(AboutTip));
    public static string Close => Get(nameof(Close));
    public static string SettingsTitle => Get(nameof(SettingsTitle));
    public static string SettingsLanguageLabel => Get(nameof(SettingsLanguageLabel));
    public static string SettingsLanguageAuto => Get(nameof(SettingsLanguageAuto));
    public static string SettingsLanguageJapanese => Get(nameof(SettingsLanguageJapanese));
    public static string SettingsLanguageEnglish => Get(nameof(SettingsLanguageEnglish));
    public static string SettingsRestartNote => Get(nameof(SettingsRestartNote));
    public static string SettingsSaveFailed => Get(nameof(SettingsSaveFailed));
    public static string SettingsStorageHeading => Get(nameof(SettingsStorageHeading));
    public static string SettingsStorageNote => Get(nameof(SettingsStorageNote));
    public static string SettingsOpenFolder => Get(nameof(SettingsOpenFolder));
    public static string SettingsOpenFolderFailed => Get(nameof(SettingsOpenFolderFailed));
    public static string SettingsDeleteHeading => Get(nameof(SettingsDeleteHeading));
    public static string SettingsDeleteAll => Get(nameof(SettingsDeleteAll));
    public static string SettingsDeleteAllNote => Get(nameof(SettingsDeleteAllNote));
    public static string SettingsDeleteAllBlocked => Get(nameof(SettingsDeleteAllBlocked));
    public static string SettingsNoWorks => Get(nameof(SettingsNoWorks));
    public static string SettingsWorkCount => Get(nameof(SettingsWorkCount));
    public static string AboutTitle => Get(nameof(AboutTitle));
    public static string AboutVersion => Get(nameof(AboutVersion));
    public static string AboutCopyright => Get(nameof(AboutCopyright));
    public static string AboutNoNetwork => Get(nameof(AboutNoNetwork));
    public static string AboutWebsite => Get(nameof(AboutWebsite));
    public static string AboutSourceCode => Get(nameof(AboutSourceCode));
    public static string AboutCopy => Get(nameof(AboutCopy));
    public static string AboutCopied => Get(nameof(AboutCopied));
    public static string AboutUrlNote => Get(nameof(AboutUrlNote));
    public static string AboutLicenseHeading => Get(nameof(AboutLicenseHeading));
    public static string AboutThirdPartyHeading => Get(nameof(AboutThirdPartyHeading));
    public static string ClosingSaving => Get(nameof(ClosingSaving));
    public static string ImportErrorPageLimitFile => Get(nameof(ImportErrorPageLimitFile));
    public static string ImportErrorHeic => Get(nameof(ImportErrorHeic));
    public static string ImportErrorUnreadableFile => Get(nameof(ImportErrorUnreadableFile));
    public static string ImportErrorUnsupportedFile => Get(nameof(ImportErrorUnsupportedFile));
    public static string ImportErrorFileGeneric => Get(nameof(ImportErrorFileGeneric));
}
