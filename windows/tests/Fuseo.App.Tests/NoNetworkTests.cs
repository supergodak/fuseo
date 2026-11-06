using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>
/// 通信機能を持たないこと（製品の生命線）を静的に確認する。
/// アプリ・Core・Input（と同梱の PDFiumCore・ZXing.Net）のアセンブリが通信系の名前空間（System.Net.Http / Sockets / WebSockets 等）を参照していないこと。
/// v0.2（W9-2）で WinRT 投影（Microsoft.Windows.SDK.NET）が入ったので、自前のアセンブリが WinRT の通信系
/// （Windows.Web.* / Windows.Networking.*）の型を使っていないことも見る（投影アセンブリ自体はそれらの型を含むので、使う側を見る）。
/// </summary>
public class NoNetworkTests
{
    private static readonly string[] Forbidden =
        ["System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "System.Net.Requests", "System.Net.Mail"];

    [Theory]
    [InlineData("Fuseo")]
    [InlineData("Fuseo.Core")]
    [InlineData("Fuseo.Input")]
    [InlineData("PDFiumCore")]
    [InlineData("zxing")]
    public void Assembly_DoesNotReferenceNetworkAssemblies(string name)
    {
        var asm = Assembly.Load(name);
        var refs = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        var hit = refs.Where(r => Forbidden.Any(f => r.StartsWith(f, StringComparison.Ordinal))).ToList();
        Assert.True(hit.Count == 0, $"{name} が通信系アセンブリを参照: {string.Join(", ", hit)}");
    }

    private static readonly string[] ForbiddenWinRtNamespaces = ["Windows.Web", "Windows.Networking"];

    [Theory]
    [InlineData("Fuseo")]
    [InlineData("Fuseo.Core")]
    [InlineData("Fuseo.Input")]
    public void Assembly_DoesNotUseWinRtNetworkTypes(string name)
    {
        var path = Assembly.Load(name).Location;
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var namespaces = md.TypeReferences
            .Select(h => md.GetTypeReference(h))
            .Select(t => md.GetString(t.Namespace))
            .ToHashSet(StringComparer.Ordinal);
        // 走査が WinRT の型参照を拾えていることの確認（Input は OCR を使う）。
        if (name == "Fuseo.Input") Assert.Contains("Windows.Media.Ocr", namespaces);
        var hit = namespaces
            .Where(ns => ForbiddenWinRtNamespaces.Any(f => ns == f || ns.StartsWith(f + ".", StringComparison.Ordinal)))
            .ToList();
        Assert.True(hit.Count == 0, $"{name} が WinRT の通信系の型を参照: {string.Join(", ", hit)}");
    }
}
