using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ChServerM.Connections;
using ChServerM.Diagnostics;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting.Dispatch;
using ChServerM.Identity;

// CA2012 억제: 이 벤치의 모든 핸들러는 ValueTask.FromResult 로 완료된 작업만 돌려준다.
// 완료된 ValueTask 의 동기 소비가 곧 측정 대상(프레임당 디스패치 비용)이다 —
// await 로 바꾸면 상태 머신 비용이 라우팅 비용에 섞인다.
#pragma warning disable CA2012

namespace ChServerM.Bench.Dispatch;

/// <summary>
/// 디스패치 오버헤드 — 라우팅 전략 5종의 프레임당 비용 (ROADMAP Phase 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>무엇을 판단하는가.</b> ADR-0014 가 미룬 질문 — "제너레이터가 switch 문 디스패처를
/// 직접 생성할 가치가 있는가" — 를 수치로 닫는다. 프로덕션 경로(빌더가 만드는 배열
/// 인덱싱 + 파이프라인)와 이론적 대안들(순수 배열, 딕셔너리, FrozenDictionary,
/// switch 문, 리플렉션)을 같은 조건에서 잰다.
/// </para>
/// <para>
/// 핸들러 16개(ID 100~115 연속), 완료된 <c>ValueTask</c> 를 돌려주는 무동작 핸들러.
/// 매 연산이 16개 ID 를 순회해 분기 예측이 단일 ID 에 고착되는 것을 막는다
/// (<c>OperationsPerInvoke=16</c>, 표시는 디스패치 1회당).
/// </para>
/// <para>
/// 리플렉션 변형은 ROADMAP 의 "개발 편의 폴백 디스패처"가 지불할 비용의 견적이다 —
/// 프로덕션 후보가 아니다(AOT 에서 비활성).
/// </para>
/// </remarks>
[Config(typeof(BenchConfig))]
public class DispatchBenchmarks
{
    private const int HandlerCount = 16;
    private const ushort FirstId = 100;

    private MessageContext[] _contexts = null!;
    private MessageDispatcher _productionDispatcher = null!;
    private MessageDelegate[] _arrayTable = null!;
    private Dictionary<ushort, MessageDelegate> _dictionary = null!;
    private FrozenDictionary<ushort, MessageDelegate> _frozen = null!;
    private MessageDelegate _h0 = null!, _h1 = null!, _h2 = null!, _h3 = null!, _h4 = null!, _h5 = null!, _h6 = null!, _h7 = null!;
    private MessageDelegate _h8 = null!, _h9 = null!, _h10 = null!, _h11 = null!, _h12 = null!, _h13 = null!, _h14 = null!, _h15 = null!;
    private Dictionary<ushort, (object Target, MethodInfo Method)> _reflection = null!;
    private object[] _reflectionArgs = null!;

    [GlobalSetup]
    public void Setup()
    {
        // CA2000 억제: 무동작 커넥션의 수명은 벤치 프로세스 전체다. Dispose 할 자원도 없다.
#pragma warning disable CA2000
        NullConnection connection = new();
#pragma warning restore CA2000

        _contexts = new MessageContext[HandlerCount];
        for (int i = 0; i < HandlerCount; i++)
        {
            MessageContext context = new(connection);
            context.BeginFrame(
                new MessageEnvelope(new MessageId((ushort)(FirstId + i)), FrameFlags.None, 0),
                default,
                receivedAt: default,
                CancellationToken.None);
            _contexts[i] = context;
        }

        MessageDelegate[] handlers = new MessageDelegate[HandlerCount];
        MessageDispatcherBuilder builder = new();
        _dictionary = new Dictionary<ushort, MessageDelegate>(HandlerCount);
        _reflection = new Dictionary<ushort, (object, MethodInfo)>(HandlerCount);

        for (int i = 0; i < HandlerCount; i++)
        {
            handlers[i] = static _ => ValueTask.FromResult(DispatchStatus.Handled);

            ushort id = (ushort)(FirstId + i);
            builder.MapRaw(new MessageId(id), handlers[i]);
            _dictionary[id] = handlers[i];

            ReflectionHandler target = new();
            _reflection[id] = (target, typeof(ReflectionHandler).GetMethod(nameof(ReflectionHandler.Handle))!);
        }

        _productionDispatcher = builder.Build();
        _frozen = _dictionary.ToFrozenDictionary();

        // 배열 테이블 — 빌더가 만드는 것과 같은 구조를 파이프라인 없이.
        _arrayTable = new MessageDelegate[FirstId + HandlerCount];
        for (int i = 0; i < HandlerCount; i++)
        {
            _arrayTable[FirstId + i] = handlers[i];
        }

        _h0 = handlers[0]; _h1 = handlers[1]; _h2 = handlers[2]; _h3 = handlers[3];
        _h4 = handlers[4]; _h5 = handlers[5]; _h6 = handlers[6]; _h7 = handlers[7];
        _h8 = handlers[8]; _h9 = handlers[9]; _h10 = handlers[10]; _h11 = handlers[11];
        _h12 = handlers[12]; _h13 = handlers[13]; _h14 = handlers[14]; _h15 = handlers[15];

        _reflectionArgs = new object[1];
    }

    [Benchmark(Baseline = true, Description = "프로덕션 (빌더 배열 + 파이프라인)", OperationsPerInvoke = HandlerCount)]
    public int ProductionDispatcher()
    {
        int sum = 0;
        foreach (MessageContext context in _contexts)
        {
            sum += (int)_productionDispatcher.DispatchAsync(context).GetAwaiter().GetResult();
        }

        return sum;
    }

    [Benchmark(Description = "배열 인덱싱 (라우팅만)", OperationsPerInvoke = HandlerCount)]
    public int ArrayTable()
    {
        int sum = 0;
        foreach (MessageContext context in _contexts)
        {
            ushort id = context.Envelope.MessageId.Value;
            MessageDelegate handler = _arrayTable[id];
            sum += (int)handler(context).GetAwaiter().GetResult();
        }

        return sum;
    }

    [Benchmark(Description = "Dictionary 조회", OperationsPerInvoke = HandlerCount)]
    public int DictionaryLookup()
    {
        int sum = 0;
        foreach (MessageContext context in _contexts)
        {
            if (_dictionary.TryGetValue(context.Envelope.MessageId.Value, out MessageDelegate? handler))
            {
                sum += (int)handler(context).GetAwaiter().GetResult();
            }
        }

        return sum;
    }

    [Benchmark(Description = "FrozenDictionary 조회", OperationsPerInvoke = HandlerCount)]
    public int FrozenDictionaryLookup()
    {
        int sum = 0;
        foreach (MessageContext context in _contexts)
        {
            if (_frozen.TryGetValue(context.Envelope.MessageId.Value, out MessageDelegate? handler))
            {
                sum += (int)handler(context).GetAwaiter().GetResult();
            }
        }

        return sum;
    }

    [Benchmark(Description = "switch 문 (직생성 시안)", OperationsPerInvoke = HandlerCount)]
    public int SwitchStatement()
    {
        int sum = 0;
        foreach (MessageContext context in _contexts)
        {
            ValueTask<DispatchStatus> result = context.Envelope.MessageId.Value switch
            {
                100 => _h0(context), 101 => _h1(context), 102 => _h2(context), 103 => _h3(context),
                104 => _h4(context), 105 => _h5(context), 106 => _h6(context), 107 => _h7(context),
                108 => _h8(context), 109 => _h9(context), 110 => _h10(context), 111 => _h11(context),
                112 => _h12(context), 113 => _h13(context), 114 => _h14(context), 115 => _h15(context),
                _ => ValueTask.FromResult(DispatchStatus.HandlerNotFound),
            };
            sum += (int)result.GetAwaiter().GetResult();
        }

        return sum;
    }

    [Benchmark(Description = "리플렉션 Invoke (폴백 견적)", OperationsPerInvoke = HandlerCount)]
    public int ReflectionInvoke()
    {
        int sum = 0;
        foreach (MessageContext context in _contexts)
        {
            (object target, MethodInfo method) = _reflection[context.Envelope.MessageId.Value];
            _reflectionArgs[0] = context;
            ValueTask<DispatchStatus> result = (ValueTask<DispatchStatus>)method.Invoke(target, _reflectionArgs)!;
            sum += (int)result.GetAwaiter().GetResult();
        }

        return sum;
    }

    /// <summary>디스패치 측정에는 커넥션이 쓰이지 않는다 — 계약 충족용 무동작 구현.</summary>
    private sealed class NullConnection : IConnection
    {
        private static readonly Pipe DummyPipe = new();

        public ConnectionId Id => new(1, 0);

        public PipeReader Input => DummyPipe.Reader;

        public PipeWriter Output => DummyPipe.Writer;

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 0);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}

/// <summary>리플렉션 변형의 대상 — 인스턴스 메서드 Invoke 비용을 재기 위한 껍데기.</summary>
internal sealed class ReflectionHandler
{
    public ValueTask<DispatchStatus> Handle(MessageContext context) =>
        ValueTask.FromResult(DispatchStatus.Handled);
}
