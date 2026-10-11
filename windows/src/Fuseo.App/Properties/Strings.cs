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

    /// <summary>表示言語（null なら OS の UI 言語）。英語の resx は W6 で追加。</summary>
    public static CultureInfo? Culture { get; set; }

    public static string Get(string key) =>
        Manager.GetString(key, Culture) ?? throw new MissingManifestResourceException($"文言が無い: {key}");

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
}
