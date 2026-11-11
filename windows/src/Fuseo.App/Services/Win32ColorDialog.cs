using System.Runtime.InteropServices;
using Fuseo.Core.Model;

namespace Fuseo.App.Services;

/// <summary>
/// Windows 標準の「色の設定」ダイアログ（comdlg32 の ChooseColor・v0.3 の文字の「その他の色」）。
/// WPF には色選択の部品が無いので、OS の共通ダイアログを P/Invoke で開く（通信なし・追加の依存なし）。
/// 「色の作成」を最初から開き、作成した色（カスタム色 16 個）はアプリの実行中だけ覚える（保存しない）。
/// </summary>
internal static class Win32ColorDialog
{
    private const int CC_RGBINIT = 0x1;
    private const int CC_FULLOPEN = 0x2;
    private const int CC_ANYCOLOR = 0x100;

    private static readonly int[] CustomColors = new int[16];

    [StructLayout(LayoutKind.Sequential)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW", ExactSpelling = true, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref CHOOSECOLOR cc);

    /// <summary>色を選ばせる。取り消し・失敗なら null。</summary>
    public static TextColor? Ask(IntPtr owner, TextColor initial)
    {
        var custom = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
        try
        {
            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = owner,
                rgbResult = initial.R8 | (initial.G8 << 8) | (initial.B8 << 16),   // COLORREF = 0x00BBGGRR
                lpCustColors = custom.AddrOfPinnedObject(),
                Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
            };
            if (!ChooseColor(ref cc)) return null;
            var c = cc.rgbResult;
            return TextColor.FromBytes((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally
        {
            custom.Free();
        }
    }
}
