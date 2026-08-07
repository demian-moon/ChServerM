using System;
using System.Collections.Generic;
using ChServerM.Diagnostics;
using ChServerM.Logging.Extensions;
using Xunit;
using MelEventId = Microsoft.Extensions.Logging.EventId;
using MelILogger = Microsoft.Extensions.Logging.ILogger;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ChServerM.Logging.Extensions.Tests;

/// <summary>
/// MEL 로깅 어댑터(ADR-0030)를 검증한다 — 심각도·이벤트 ID·상태·예외·포매터가 손실 없이
/// 전달되고, 구조체 상태가 <b>박싱되지 않는지</b>.
/// </summary>
/// <remarks>
/// 상태를 재포장하지 않는 것이 이 어댑터의 핵심이다 — 재포장하면 구조체 상태가 박싱되어
/// 프레임워크가 지켜온 무할당 규약이 어댑터에서 깨진다.
/// </remarks>
public sealed class MicrosoftServerLoggerTests
{
    /// <summary>기록된 항목을 그대로 담는 MEL 로거.</summary>
    private sealed class RecordingLogger : MelILogger
    {
        public readonly List<(MelLogLevel Level, MelEventId EventId, object? State, Exception? Exception, string Message)> Entries = [];

        public MelLogLevel MinimumLevel { get; set; } = MelLogLevel.Trace;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(MelLogLevel logLevel) => logLevel >= MinimumLevel;

        public void Log<TState>(
            MelLogLevel logLevel,
            MelEventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, state, exception, formatter(state, exception)));
    }

    /// <summary>구조체 상태 — 박싱 여부를 확인하기 위한 값 타입.</summary>
    private readonly struct FrameState(int messageId, long sequence)
    {
        public int MessageId { get; } = messageId;

        public long Sequence { get; } = sequence;
    }

    [Fact]
    public void Log_forwards_level_eventid_state_exception_and_message()
    {
        RecordingLogger target = new();
        IServerLogger logger = target.ToServerLogger();
        InvalidOperationException error = new("boom");

        logger.Log(
            LogLevel.Error,
            new EventId(4242, "TestEvent"),
            new FrameState(100, 7),
            error,
            static (state, ex) => $"msg={state.MessageId} seq={state.Sequence} err={ex?.Message}");

        (MelLogLevel level, MelEventId eventId, object? state, Exception? exception, string message) =
            Assert.Single(target.Entries);

        Assert.Equal(MelLogLevel.Error, level);
        Assert.Equal(4242, eventId.Id);
        Assert.Equal("TestEvent", eventId.Name);
        Assert.Same(error, exception);
        Assert.Equal("msg=100 seq=7 err=boom", message);

        // 상태가 재포장되지 않고 원본 타입 그대로 전달됐다.
        FrameState forwarded = Assert.IsType<FrameState>(state);
        Assert.Equal(100, forwarded.MessageId);
        Assert.Equal(7, forwarded.Sequence);
    }

    [Theory]
    [InlineData(LogLevel.Trace, MelLogLevel.Trace)]
    [InlineData(LogLevel.Debug, MelLogLevel.Debug)]
    [InlineData(LogLevel.Information, MelLogLevel.Information)]
    [InlineData(LogLevel.Warning, MelLogLevel.Warning)]
    [InlineData(LogLevel.Error, MelLogLevel.Error)]
    [InlineData(LogLevel.Critical, MelLogLevel.Critical)]
    public void Severity_mapping_is_exact(LogLevel core, MelLogLevel mel)
    {
        // 값이 어긋나면 운영자가 건 필터가 엉뚱한 심각도를 자른다 — 조용한 로그 유실이다.
        RecordingLogger target = new();
        IServerLogger logger = target.ToServerLogger();

        logger.Log(core, new EventId(1), 0, null, static (_, _) => "x");

        Assert.Equal(mel, Assert.Single(target.Entries).Level);
    }

    [Fact]
    public void IsEnabled_delegates_to_the_target()
    {
        // 게이트가 위임되지 않으면 프레임워크가 꺼진 로그의 인자를 계속 계산한다
        // (IServerLogger 의 핫패스 규약이 무너진다).
        RecordingLogger target = new() { MinimumLevel = MelLogLevel.Warning };
        IServerLogger logger = target.ToServerLogger();

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void Factory_extension_uses_the_framework_category()
    {
        // 범주가 프레임워크 이름이어야 프로바이더 필터로 프레임워크 로그만 조절할 수 있다.
        RecordingFactory factory = new();

        IServerLogger logger = factory.CreateServerLogger();

        Assert.Equal("ChServerM", factory.LastCategory);
        Assert.NotNull(logger);
    }

    [Fact]
    public void Factory_extension_accepts_a_custom_category()
    {
        RecordingFactory factory = new();

        factory.CreateServerLogger("MyApp.Net");

        Assert.Equal("MyApp.Net", factory.LastCategory);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new MicrosoftServerLogger(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ((Microsoft.Extensions.Logging.ILoggerFactory)null!).CreateServerLogger());
    }

    private sealed class RecordingFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public string? LastCategory { get; private set; }

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
        {
        }

        public MelILogger CreateLogger(string categoryName)
        {
            LastCategory = categoryName;
            return new RecordingLogger();
        }

        public void Dispose()
        {
        }
    }
}
