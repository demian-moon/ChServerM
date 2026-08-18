using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ChServerM.Connections;
using ChServerM.Dispatch;
using ChServerM.Features;
using ChServerM.Framing;
using ChServerM.Hosting.Sessions;
using ChServerM.Identity;
using ChServerM.Persistence.InMemory;
using ChServerM.Sessions;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 세션 재개의 <b>와이어 프로토콜</b>과 서버 측 배선을 검증한다 (ADR-0036 후속).
/// </summary>
/// <remarks>
/// 코덱은 <b>영구 동결</b>이므로 크기와 오프셋을 테스트가 못 박는다 — 클라이언트와 서버가
/// 서로 다른 버전으로 배포될 수 있어, 레이아웃이 조용히 바뀌면 배포 중에 드러난다.
/// </remarks>
public sealed class SessionResumeProtocolTests
{
    private static readonly FramingOptions Framing = new() { MaxPayloadLength = 4096 };

    private static SessionId Id(int seed) => new(new ObjectId(seed));

    // ── 동결된 레이아웃 ─────────────────────────────────────────────────────

    [Fact]
    public void Frozen_sizes_do_not_change()
    {
        // 이 상수들이 바뀌면 배포된 클라이언트와 말이 통하지 않는다.
        Assert.Equal(32, SessionHandshakeCodec.TokenLength);
        Assert.Equal(8, SessionHandshakeCodec.SessionIdLength);
        Assert.Equal(40, SessionHandshakeCodec.ResumeRequestSize);
        Assert.Equal(33, SessionHandshakeCodec.ResumeResponseSize);
        Assert.Equal(40, SessionHandshakeCodec.EstablishedSize);
    }

    [Fact]
    public void Reserved_message_ids_do_not_change()
    {
        Assert.Equal(40007, FrameworkMessageIds.SessionResume.Value);
        Assert.Equal(40008, FrameworkMessageIds.SessionResumed.Value);
        Assert.Equal(40009, FrameworkMessageIds.SessionEstablished.Value);
    }

    [Fact]
    public void Resume_request_round_trips()
    {
        byte[] token = new byte[SessionHandshakeCodec.TokenLength];
        Random.Shared.NextBytes(token);

        byte[] payload = new byte[SessionHandshakeCodec.ResumeRequestSize];
        SessionHandshakeCodec.WriteResumeRequest(payload, 0x0123_4567_89AB_CDEF, token);

        byte[] readToken = new byte[SessionHandshakeCodec.TokenLength];
        Assert.True(SessionHandshakeCodec.TryReadResumeRequest(payload, out long sessionId, readToken));

        Assert.Equal(0x0123_4567_89AB_CDEF, sessionId);
        Assert.Equal(token, readToken);
    }

    [Fact]
    public void Resume_response_round_trips()
    {
        byte[] token = new byte[SessionHandshakeCodec.TokenLength];
        Random.Shared.NextBytes(token);

        byte[] payload = new byte[SessionHandshakeCodec.ResumeResponseSize];
        SessionHandshakeCodec.WriteResumeResponse(payload, SessionResumeStatus.Resumed, token);

        byte[] readToken = new byte[SessionHandshakeCodec.TokenLength];
        Assert.True(SessionHandshakeCodec.TryReadResumeResponse(payload, out SessionResumeStatus status, readToken));

        Assert.Equal(SessionResumeStatus.Resumed, status);
        Assert.Equal(token, readToken);
    }

    [Fact]
    public void Rejection_has_the_same_length_as_success()
    {
        // ★ 길이가 다르면 상태 바이트를 읽지 않고도 결과를 알 수 있어 부수 채널이 된다.
        byte[] ok = new byte[SessionHandshakeCodec.ResumeResponseSize];
        byte[] rejected = new byte[SessionHandshakeCodec.ResumeResponseSize];

        byte[] token = new byte[SessionHandshakeCodec.TokenLength];
        Random.Shared.NextBytes(token);

        SessionHandshakeCodec.WriteResumeResponse(ok, SessionResumeStatus.Resumed, token);
        SessionHandshakeCodec.WriteResumeResponse(rejected, SessionResumeStatus.Rejected, ReadOnlySpan<byte>.Empty);

        Assert.Equal(ok.Length, rejected.Length);

        // 거부 시 토큰 자리는 0 이다 — 남은 값이 새지 않는다.
        byte[] readToken = new byte[SessionHandshakeCodec.TokenLength];
        SessionHandshakeCodec.TryReadResumeResponse(rejected, out _, readToken);
        Assert.All(readToken, b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(41)]
    public void Wrong_length_payloads_are_rejected(int length)
    {
        // ⚠ 더 긴 페이로드를 관대하게 받으면 검증되지 않은 바이트가 흘러 들어온다.
        byte[] token = new byte[SessionHandshakeCodec.TokenLength];
        Assert.False(SessionHandshakeCodec.TryReadResumeRequest(new byte[length], out _, token));
    }

    [Fact]
    public void Undefined_status_bytes_are_rejected_as_malformed()
    {
        // 감사 2026-08-18 C-5: 정의되지 않은 상태 바이트(0=Unspecified 포함)를 "형식이 맞다"로
        // 통과시키지 않는다 — VersionHandshakeCodec 의 "부트스트랩에 관대한 수신은 없다"와
        // 같은 원칙. 빈 버퍼(전부 0)는 이제 파싱 자체가 거부된다.
        byte[] token = new byte[SessionHandshakeCodec.TokenLength];

        byte[] empty = new byte[SessionHandshakeCodec.ResumeResponseSize];
        Assert.False(SessionHandshakeCodec.TryReadResumeResponse(empty, out SessionResumeStatus status, token));
        Assert.Equal(SessionResumeStatus.Unspecified, status);

        byte[] garbage = new byte[SessionHandshakeCodec.ResumeResponseSize];
        garbage[0] = 200; // 정의되지 않은 상태 값
        Assert.False(SessionHandshakeCodec.TryReadResumeResponse(garbage, out _, token));
    }

    // ── 서버 측 배선 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_resume_binds_the_connection_and_returns_a_rotated_token()
    {
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService service = new(store);
        SessionResumeDispatch dispatch = new(service, new FixedHeaderFrameEncoder(Framing));

        SessionBinding created = (await service.TryCreateAsync(Id(1), new byte[] { 7, 7 }))!.Value;

        StubConnection connection = new();
        MessageContext context = NewContext(connection, ResumeRequest(1, created.ResumeToken));

        DispatchStatus status = await dispatch.HandleResumeAsync(context);
        Assert.Equal(DispatchStatus.Handled, status);

        // 커넥션이 세션에 바인딩됐다.
        ISessionFeature? feature = connection.Features.Get<ISessionFeature>();
        Assert.NotNull(feature);
        Assert.Equal(Id(1), feature.SessionId);
        Assert.NotEqual(SessionVersion.None, feature.Version);

        // 응답은 성공이고 토큰이 회전됐다.
        (SessionResumeStatus responseStatus, byte[] rotated) = await ReadResponseAsync(connection);
        Assert.Equal(SessionResumeStatus.Resumed, responseStatus);

        byte[] original = new byte[SessionHandshakeCodec.TokenLength];
        created.ResumeToken.CopyTo(original);
        Assert.NotEqual(original, rotated);
    }

    [Fact]
    public async Task Invalid_token_is_rejected_without_binding()
    {
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService service = new(store);
        SessionResumeDispatch dispatch = new(service, new FixedHeaderFrameEncoder(Framing));

        await service.TryCreateAsync(Id(1), new byte[] { 1 });

        StubConnection connection = new();
        MessageContext context = NewContext(connection, ResumeRequest(1, SessionResumeToken.Create()));

        await dispatch.HandleResumeAsync(context);

        // ★ 실패했는데 바인딩이 생기면 그 커넥션이 남의 세션을 쓰게 된다.
        Assert.Null(connection.Features.Get<ISessionFeature>());

        (SessionResumeStatus status, _) = await ReadResponseAsync(connection);
        Assert.Equal(SessionResumeStatus.Rejected, status);
    }

    [Fact]
    public async Task Unknown_session_and_bad_token_look_identical_on_the_wire()
    {
        // ★★ 응답 바이트가 완전히 같아야 한다 — 다르면 실재하는 세션을 열거할 수 있다.
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService service = new(store);
        SessionResumeDispatch dispatch = new(service, new FixedHeaderFrameEncoder(Framing));

        await service.TryCreateAsync(Id(1), new byte[] { 1 });
        SessionResumeToken bogus = SessionResumeToken.Create();

        StubConnection existing = new();
        await dispatch.HandleResumeAsync(NewContext(existing, ResumeRequest(1, bogus)));

        StubConnection missing = new();
        await dispatch.HandleResumeAsync(NewContext(missing, ResumeRequest(999, bogus)));

        byte[] a = await ReadRawFrameAsync(existing);
        byte[] b = await ReadRawFrameAsync(missing);

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Malformed_payload_is_answered_not_ignored()
    {
        // 응답 없이 끊으면 클라이언트가 "거부됐다" 와 "네트워크가 끊겼다" 를 구분할 수 없다.
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeDispatch dispatch = new(new SessionResumeService(store), new FixedHeaderFrameEncoder(Framing));

        StubConnection connection = new();
        await dispatch.HandleResumeAsync(NewContext(connection, new byte[] { 1, 2, 3 }));

        (SessionResumeStatus status, _) = await ReadResponseAsync(connection);
        Assert.Equal(SessionResumeStatus.Rejected, status);
    }

    [Fact]
    public async Task Established_notification_binds_and_carries_the_token()
    {
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService service = new(store);
        SessionResumeDispatch dispatch = new(service, new FixedHeaderFrameEncoder(Framing));

        SessionBinding created = (await service.TryCreateAsync(Id(42), new byte[] { 5 }))!.Value;

        StubConnection connection = new();
        MessageContext context = NewContext(connection, Array.Empty<byte>());

        await dispatch.WriteEstablishedAsync(context, Id(42), created);

        ISessionFeature? feature = connection.Features.Get<ISessionFeature>();
        Assert.NotNull(feature);
        Assert.Equal(Id(42), feature.SessionId);
        Assert.Equal(created.Version, feature.Version);

        byte[] frame = await ReadRawFrameAsync(connection);
        byte[] payload = frame.AsSpan(FrameHeader.Size).ToArray();

        byte[] token = new byte[SessionHandshakeCodec.TokenLength];
        Assert.True(SessionHandshakeCodec.TryReadEstablished(payload, out long sessionId, token));
        Assert.Equal(42, sessionId);

        byte[] expected = new byte[SessionHandshakeCodec.TokenLength];
        created.ResumeToken.CopyTo(expected);
        Assert.Equal(expected, token);
    }

    [Fact]
    public async Task Resume_fences_the_previous_connection_through_the_feature_version()
    {
        // ★ 프로토콜 계층에서도 좀비 차단이 성립하는지 — 옛 커넥션의 feature 버전은
        // 재개 후 무효가 되어 쓰기가 Conflict 로 실패한다.
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });
        SessionResumeService service = new(store);
        SessionResumeDispatch dispatch = new(service, new FixedHeaderFrameEncoder(Framing));

        SessionBinding created = (await service.TryCreateAsync(Id(1), new byte[] { 1 }))!.Value;

        // 옛 커넥션이 최초 바인딩을 갖는다.
        StubConnection oldConnection = new();
        await dispatch.WriteEstablishedAsync(NewContext(oldConnection, Array.Empty<byte>()), Id(1), created);
        ISessionFeature oldFeature = oldConnection.Features.Get<ISessionFeature>()!;

        // 새 커넥션이 재개한다.
        StubConnection newConnection = new();
        await dispatch.HandleResumeAsync(NewContext(newConnection, ResumeRequest(1, created.ResumeToken)));
        ISessionFeature newFeature = newConnection.Features.Get<ISessionFeature>()!;

        // 옛 커넥션의 버전은 밀려났다.
        Assert.NotEqual(oldFeature.Version, newFeature.Version);
        Assert.False((await service.TryWriteStateAsync(Id(1), new byte[] { 0xDE }, oldFeature.Version)).Succeeded);
        Assert.True((await service.TryWriteStateAsync(Id(1), new byte[] { 0xAD }, newFeature.Version)).Succeeded);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        using InMemorySessionStore store = new(new InMemorySessionStoreOptions { SweepInterval = null });

        Assert.Throws<ArgumentNullException>(() =>
            new SessionResumeDispatch(null!, new FixedHeaderFrameEncoder(Framing)));
        Assert.Throws<ArgumentNullException>(() =>
            new SessionResumeDispatch(new SessionResumeService(store), null!));
    }

    // ── 도우미 ──────────────────────────────────────────────────────────────

    private static byte[] ResumeRequest(long sessionId, SessionResumeToken token)
    {
        byte[] payload = new byte[SessionHandshakeCodec.ResumeRequestSize];
        Span<byte> raw = stackalloc byte[SessionHandshakeCodec.TokenLength];
        token.CopyTo(raw);
        SessionHandshakeCodec.WriteResumeRequest(payload, sessionId, raw);
        return payload;
    }

    private static MessageContext NewContext(StubConnection connection, byte[] payload)
    {
        MessageContext context = new(connection);
        context.BeginFrame(
            new MessageEnvelope(FrameworkMessageIds.SessionResume, FrameFlags.None, 0),
            new System.Buffers.ReadOnlySequence<byte>(payload),
            receivedAt: default,
            CancellationToken.None);

        return context;
    }

    private static async Task<byte[]> ReadRawFrameAsync(StubConnection connection)
    {
        await connection.Output.FlushAsync();
        ReadResult read = await connection.Reader.ReadAsync();
        byte[] bytes = read.Buffer.ToArray();
        connection.Reader.AdvanceTo(read.Buffer.End);
        return bytes;
    }

    private static async Task<(SessionResumeStatus Status, byte[] Token)> ReadResponseAsync(StubConnection connection)
    {
        byte[] frame = await ReadRawFrameAsync(connection);
        byte[] payload = frame.AsSpan(FrameHeader.Size).ToArray();

        byte[] token = new byte[SessionHandshakeCodec.TokenLength];
        Assert.True(SessionHandshakeCodec.TryReadResumeResponse(payload, out SessionResumeStatus status, token));
        return (status, token);
    }

    /// <summary>파이프 하나로 서버가 쓴 프레임을 되읽는 최소 커넥션.</summary>
    private sealed class StubConnection : IConnection
    {
        private readonly Pipe _outbound = new();

        public ConnectionId Id => new(1, 0);

        public PipeReader Input => _outbound.Reader;

        public PipeWriter Output => _outbound.Writer;

        /// <summary>서버가 쓴 것을 테스트가 읽는 쪽.</summary>
        public PipeReader Reader => _outbound.Reader;

        public IFeatureCollection Features { get; } = new FeatureCollection(capacity: 2);

        public CancellationToken ConnectionClosed => CancellationToken.None;

        public void Abort(in ConnectionCloseInfo info)
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
