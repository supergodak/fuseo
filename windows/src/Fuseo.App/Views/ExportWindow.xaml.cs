using System.ComponentModel;
using System.Windows;
using Fuseo.App.ViewModels;

namespace Fuseo.App.Views;

/// <summary>書き出しシートのウィンドウ。開いたら焼き込みを始め、ViewModel の閉じる要求で閉じる。</summary>
public partial class ExportWindow : Window
{
    public ExportWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is not ExportViewModel vm) return;
            vm.CloseRequested += (_, _) => Close();
            await vm.PrepareAsync();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 保存中に閉じると書き出しの完了を利用者が確認できないので、終わるまで閉じさせない。
        if (DataContext is ExportViewModel { IsSaving: true }) e.Cancel = true;
        base.OnClosing(e);
    }
}
