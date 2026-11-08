using System.IO;
using Fuseo.Core.Contracts;

namespace Fuseo.App.State;

/// <summary>
/// <see cref="IExporter"/> を実ファイルにつなぐ。書き込みは一時ファイル（同じフォルダ）に行い、
/// 全ページ書けてから本来の名前へ置き換える＝失敗時に書きかけのファイルを残さない。
/// 受け取るのは焼き込み済みの <see cref="BurnedPage"/> だけ（焼き込み前の画像を書く経路は無い）。
/// </summary>
public static class ExportWriter
{
    public const string TempSuffix = ".fuseo-tmp";

    /// <param name="overwrite">
    /// false = 上書きしないモード（W9-4「書類ごとに分ける」）: 一時ファイルは新規作成のみ・本来の名前へは <c>File.Move(overwrite: false)</c>。
    /// 既にあれば失敗し、この呼び出しで作ったファイルだけを消して戻す（既存のファイルには触れない）。
    /// </param>
    public static void Write(IExporter exporter, IReadOnlyList<BurnedPage> pages, ExportOptions options,
                             IReadOnlyList<string> targets, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        if (targets.Count == 0) throw new ArgumentException("書き出し先が無い", nameof(targets));
        var temps = new string?[targets.Count];
        var streams = new List<FileStream>();
        var moved = new List<string>();
        try
        {
            exporter.Export(pages, options, index =>
            {
                if (index < 0 || index >= targets.Count)
                    throw new InvalidOperationException($"想定外の書き出し先の番号: {index}");
                if (temps[index] is not null)
                    throw new InvalidOperationException($"同じ書き出し先が二度要求された: {index}");
                var tmp = targets[index] + TempSuffix;
                // 作れてから記録する（作れなかった＝既にあった一時ファイルを後片付けで消さないように）。
                var fs = new FileStream(tmp, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
                temps[index] = tmp;
                streams.Add(fs);
                return fs;
            });
            foreach (var s in streams) s.Dispose();
            streams.Clear();
            if (temps.Any(t => t is null))
                throw new InvalidOperationException("一部のページが書き出されなかった");
            for (var i = 0; i < targets.Count; i++)
            {
                File.Move(temps[i]!, targets[i], overwrite);
                moved.Add(targets[i]);
            }
        }
        catch
        {
            foreach (var s in streams) s.Dispose();
            foreach (var t in temps)
            {
                if (t is null) continue;
                try { File.Delete(t); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // 上書きしないモードで途中まで置けた分は、この呼び出しが新しく作ったファイルなので消す（書類単位で全部か無しか）。
            if (!overwrite)
                foreach (var m in moved)
                {
                    try { File.Delete(m); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            throw;
        }
    }
}
