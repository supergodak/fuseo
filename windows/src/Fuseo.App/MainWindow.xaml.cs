using System.Windows;
using Fuseo.Core.Model;

namespace Fuseo.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = $"プリセット {PresetStore.LoadAll().Count} 種別を読み込みました（骨組み）";
    }
}
