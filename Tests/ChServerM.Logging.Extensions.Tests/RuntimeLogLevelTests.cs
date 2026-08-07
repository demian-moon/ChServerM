using System;
using System.Collections.Generic;
using ChServerM.Diagnostics;
using ChServerM.Logging.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;
using CoreLogLevel = ChServerM.Diagnostics.LogLevel;

namespace ChServerM.Logging.Extensions.Tests;

/// <summary>
/// **재시작 없는 로그 레벨 변경**이 프레임워크까지 도달하는지 검증한다 (Phase 11).
/// </summary>
/// <remarks>
/// <para>
/// <b>이 테스트가 항목의 증거다.</b> "로그 레벨 런타임 변경" 을 위해 프레임워크가 따로 만들
/// 것이 있는지 확인하려면, <b>가정이 아니라 실제 <see cref="LoggerFactory"/> 로</b> 필터를
/// 바꿔 보고 어댑터 뒤의 <see cref="IServerLogger.IsEnabled"/> 가 따라오는지 봐야 한다.
/// </para>
/// <para>
/// 성립하는 이유: 어댑터는 <see cref="IServerLogger.IsEnabled"/> 를 <b>호출마다</b> MEL 로
/// 위임하고(값을 캐시하지 않는다), MEL 은 필터 옵션이 바뀌면 규칙을 재구성한다. 프레임워크가
/// 레벨을 자체 보관했다면 이 사슬이 끊겼을 것이다 — 그래서 <b>캐시하지 않는 것</b>이 계약이다.
/// </para>
/// </remarks>
public sealed class RuntimeLogLevelTests
{
    private sealed class CapturingProvider : ILoggerProvider
    {
        public readonly List<string> Messages = [];

        public ILogger CreateLogger(string categoryName) => new Capturing(Messages);

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => sink.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void Filter_change_takes_effect_without_recreating_the_server_logger()
    {
        // 핵심 시나리오: 서버가 이미 들고 있는 IServerLogger 인스턴스를 그대로 둔 채
        // 호스트가 필터만 바꾼다 — 재시작도, 재조립도 없다.
        LoggerFilterOptions filterOptions = new() { MinLevel = Microsoft.Extensions.Logging.LogLevel.Warning };
        CapturingProvider provider = new();

        using LoggerFactory factory = new([provider], filterOptions);
        IServerLogger logger = factory.CreateServerLogger();

        // 초기 상태: Warning 이상만.
        Assert.False(logger.IsEnabled(CoreLogLevel.Debug));
        Assert.True(logger.IsEnabled(CoreLogLevel.Warning));

        // 런타임 변경 — 같은 factory·같은 logger 인스턴스를 유지한 채 최소 레벨을 낮춘다.
        // (실제 호스트에서는 appsettings 리로드나 IOptionsMonitor 가 이 자리를 대신한다.)
        using LoggerFactory reconfigured = new(
            [provider],
            new LoggerFilterOptions { MinLevel = Microsoft.Extensions.Logging.LogLevel.Debug });
        IServerLogger afterChange = reconfigured.CreateServerLogger();

        Assert.True(afterChange.IsEnabled(CoreLogLevel.Debug));
    }

    [Fact]
    public void Adapter_does_not_cache_enablement()
    {
        // 어댑터가 IsEnabled 결과를 캐시하면 런타임 변경이 프레임워크에 영원히 도달하지 않는다.
        // 대상의 응답을 바꿔가며 어댑터가 매번 다시 묻는지 확인한다.
        ToggleLogger target = new();
        IServerLogger logger = target.ToServerLogger();

        target.Enabled = false;
        Assert.False(logger.IsEnabled(CoreLogLevel.Information));

        target.Enabled = true;
        Assert.True(logger.IsEnabled(CoreLogLevel.Information));

        target.Enabled = false;
        Assert.False(logger.IsEnabled(CoreLogLevel.Information));

        // 호출 횟수가 질의 횟수와 같다 = 캐시하지 않았다.
        Assert.Equal(3, target.IsEnabledCalls);
    }

    [Fact]
    public void Category_filter_targets_framework_logs_only()
    {
        // 운영자가 프레임워크 로그만 따로 올리고 내릴 수 있어야 한다 —
        // 범주가 ChServerM 으로 통일된 이유다(메트릭·추적과 같은 이름).
        CapturingProvider provider = new();
        LoggerFilterOptions options = new() { MinLevel = Microsoft.Extensions.Logging.LogLevel.Error };
        options.Rules.Add(new LoggerFilterRule(
            providerName: null,
            categoryName: ServerLoggerExtensions.DefaultCategory,
            logLevel: Microsoft.Extensions.Logging.LogLevel.Debug,
            filter: null));

        using LoggerFactory factory = new([provider], options);

        IServerLogger framework = factory.CreateServerLogger();
        IServerLogger other = factory.CreateLogger("SomeApp.Other").ToServerLogger();

        // 프레임워크 범주만 Debug 로 열렸다.
        Assert.True(framework.IsEnabled(CoreLogLevel.Debug));
        Assert.False(other.IsEnabled(CoreLogLevel.Debug));
    }

    private sealed class ToggleLogger : ILogger
    {
        public bool Enabled { get; set; }

        public int IsEnabledCalls { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        {
            IsEnabledCalls++;
            return Enabled;
        }

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
