using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChServerM.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace ChServerM.Analyzers.Tests;

/// <summary>
/// 사용 규약 분석기(CHSM3xxx)의 판정 테스트 — 위반은 잡히고, 합법 패턴은 조용해야 한다.
/// </summary>
/// <remarks>
/// 오탐(합법 코드에 경고)이 이 대역의 최대 리스크다 — 오탐이 생기면 사용자는 진단을 끈다.
/// 그래서 모든 규칙에 "잡아야 한다" 테스트와 같은 수의 "조용해야 한다" 테스트를 둔다.
/// </remarks>
public sealed class UsageAnalyzerTests
{
    // ── CHSM3001: async void ─────────────────────────────────────────

    [Fact]
    public async Task AsyncVoidMethod_Reports3001()
    {
        const string Source = """
            using System.Threading.Tasks;

            public class C
            {
                public async void Fire() => await Task.Yield();
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new AsyncVoidAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3001");
    }

    [Fact]
    public async Task AsyncTaskMethod_IsQuiet()
    {
        const string Source = """
            using System.Threading.Tasks;

            public class C
            {
                public async Task FireAsync() => await Task.Yield();
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new AsyncVoidAnalyzer());

        Assert.DoesNotContain(diagnostics, d => d.Id == "CHSM3001");
    }

    [Fact]
    public async Task AsyncVoidEventHandlerShape_IsQuiet()
    {
        // UI 이벤트 핸들러는 델리게이트 계약이 void 를 강제한다 — 유일한 면제다.
        const string Source = """
            using System;
            using System.Threading.Tasks;

            public class C
            {
                public async void OnClick(object sender, EventArgs e) => await Task.Yield();
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new AsyncVoidAnalyzer());

        Assert.DoesNotContain(diagnostics, d => d.Id == "CHSM3001");
    }

    [Fact]
    public async Task AsyncVoidLambda_Reports3001()
    {
        const string Source = """
            using System;
            using System.Threading.Tasks;

            public class C
            {
                public void Register()
                {
                    Action fire = async () => await Task.Yield();
                    fire();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new AsyncVoidAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3001");
    }

    [Fact]
    public async Task AsyncVoidLocalFunction_Reports3001()
    {
        const string Source = """
            using System.Threading.Tasks;

            public class C
            {
                public void Run()
                {
                    async void Fire() => await Task.Yield();
                    Fire();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new AsyncVoidAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3001");
    }

    // ── CHSM3002: async 경로의 블로킹 호출 ───────────────────────────

    [Fact]
    public async Task TaskResultInAsyncMethod_Reports3002()
    {
        const string Source = """
            using System.Threading.Tasks;

            public class C
            {
                public async Task<int> ReadAsync(Task<int> pending)
                {
                    await Task.Yield();
                    return pending.Result;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new BlockingCallInAsyncAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3002");
    }

    [Fact]
    public async Task TaskWaitInAsyncMethod_Reports3002()
    {
        const string Source = """
            using System.Threading.Tasks;

            public class C
            {
                public async Task RunAsync(Task pending)
                {
                    pending.Wait();
                    await Task.Yield();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new BlockingCallInAsyncAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3002");
    }

    [Fact]
    public async Task ThreadSleepInAsyncMethod_Reports3002()
    {
        const string Source = """
            using System.Threading;
            using System.Threading.Tasks;

            public class C
            {
                public async Task RunAsync()
                {
                    Thread.Sleep(1000);
                    await Task.Yield();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new BlockingCallInAsyncAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3002");
    }

    [Fact]
    public async Task GetAwaiterGetResultInAsyncMethod_Reports3002()
    {
        const string Source = """
            using System.Threading.Tasks;

            public class C
            {
                public async Task<int> ReadAsync(Task<int> pending)
                {
                    await Task.Yield();
                    return pending.GetAwaiter().GetResult();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new BlockingCallInAsyncAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3002");
    }

    [Fact]
    public async Task TaskResultInSyncMethod_IsQuiet()
    {
        // 동기 메서드의 sync-over-async 는 1차 판정 범위 밖이다(콘솔 Main 등 정당한 경우와
        // 구분 불가). 이 테스트가 그 계약을 고정한다 — 범위를 넓히면 함께 바꾼다.
        const string Source = """
            using System.Threading.Tasks;

            public class C
            {
                public int Read(Task<int> pending) => pending.Result;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new BlockingCallInAsyncAnalyzer());

        Assert.DoesNotContain(diagnostics, d => d.Id == "CHSM3002");
    }

    [Fact]
    public async Task SyncLambdaInsideAsyncMethod_IsQuiet()
    {
        // 함수 경계는 가장 가까운 것이 이긴다 — async 메서드 "안"이라도 동기 람다 안의
        // 블로킹은 그 람다를 실행할 스레드의 문제다(스레드풀 작업으로 넘기는 표준 패턴).
        const string Source = """
            using System;
            using System.Threading.Tasks;

            public class C
            {
                public async Task RunAsync(Task<int> pending)
                {
                    Func<int> read = () => pending.Result;
                    await Task.Run(read);
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new BlockingCallInAsyncAnalyzer());

        Assert.DoesNotContain(diagnostics, d => d.Id == "CHSM3002");
    }

    [Fact]
    public async Task AsyncLambdaInsideSyncMethod_Reports3002()
    {
        const string Source = """
            using System;
            using System.Threading.Tasks;

            public class C
            {
                public Func<Task> Build(Task<int> pending) =>
                    async () => { _ = pending.Result; await Task.Yield(); };
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new BlockingCallInAsyncAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3002");
    }

    // ── CHSM3003: Payload 수명 위반 ──────────────────────────────────

    [Fact]
    public async Task PayloadAssignedToField_Reports3003()
    {
        const string Source = """
            using System.Buffers;
            using ChServerM.Dispatch;

            public class C
            {
                private ReadOnlySequence<byte> _stash;

                public void Handle(MessageContext context)
                {
                    _stash = context.Payload;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new PayloadEscapeAnalyzer());

        Assert.Contains(diagnostics, d => d.Id == "CHSM3003");
    }

    [Fact]
    public async Task PayloadAssignedToLocal_IsQuiet()
    {
        // 지역 변수는 핸들러 수명 안이다 — 합법.
        const string Source = """
            using System.Buffers;
            using ChServerM.Dispatch;

            public class C
            {
                public long Handle(MessageContext context)
                {
                    ReadOnlySequence<byte> payload = context.Payload;
                    return payload.Length;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new PayloadEscapeAnalyzer());

        Assert.DoesNotContain(diagnostics, d => d.Id == "CHSM3003");
    }

    [Fact]
    public async Task PayloadCopyAssignedToField_IsQuiet()
    {
        // 복사(ToArray)의 저장은 합법 — 버퍼가 아니라 사본을 붙드는 것이다.
        const string Source = """
            using System.Buffers;
            using ChServerM.Dispatch;

            public class C
            {
                private byte[] _copy = [];

                public void Handle(MessageContext context)
                {
                    _copy = context.Payload.ToArray();
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source, new PayloadEscapeAnalyzer());

        Assert.DoesNotContain(diagnostics, d => d.Id == "CHSM3003");
    }

    // ── 드라이버 도우미 ──────────────────────────────────────────────

    /// <summary>소스를 컴파일해 분석기 하나를 돌리고 그 진단만 돌려준다.</summary>
    /// <remarks>테스트 소스의 컴파일 오류는 판정을 무의미하게 하므로 먼저 실패시킨다.</remarks>
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, DiagnosticAnalyzer analyzer)
    {
        List<MetadataReference> references = [];

        // 테스트 러너 런타임의 참조 어셈블리 — BCL 해석용 표준 트릭(SourceGen.Tests 와 동일).
        string platformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in platformAssemblies.Split(Path.PathSeparator))
        {
            if (path.Length > 0
                && !Path.GetFileName(path).StartsWith("ChServerM.", StringComparison.Ordinal))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        references.Add(MetadataReference.CreateFromFile(typeof(ChServerM.Dispatch.MessageContext).Assembly.Location));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Diagnostic[] compileErrors =
            [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.Empty(compileErrors);

        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers([analyzer]);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }
}
