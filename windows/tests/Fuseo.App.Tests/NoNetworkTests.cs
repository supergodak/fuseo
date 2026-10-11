using System.Reflection;
using Xunit;

namespace Fuseo.App.Tests;

/// <summary>
/// 通信機能を持たないこと（製品の生命線）を静的に確認する。
/// アプリ・Core・Input（と同梱の PDFiumCore）のアセンブリが通信系の名前空間（System.Net.Http / Sockets / WebSockets 等）を参照していないこと。
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
    public void Assembly_DoesNotReferenceNetworkAssemblies(string name)
    {
        var asm = Assembly.Load(name);
        var refs = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        var hit = refs.Where(r => Forbidden.Any(f => r.StartsWith(f, StringComparison.Ordinal))).ToList();
        Assert.True(hit.Count == 0, $"{name} が通信系アセンブリを参照: {string.Join(", ", hit)}");
    }
}
