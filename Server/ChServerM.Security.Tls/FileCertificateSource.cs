using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using ChServerM.Diagnostics;

namespace ChServerM.Security.Tls;

/// <summary>
/// 파일(PFX 또는 PEM 쌍)에서 서버 인증서를 적재하고, 파일 교체를 감지해 회전하는 원천.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 인증서 갱신(cert-manager·Let's Encrypt·수동 배포)이 파일 교체로
/// 일어나는 운영 환경에서, 서버 재시작 없이 새 인증서를 집는 경로다.
/// </para>
/// <para>
/// <b>회전 감지 = 핸드셰이크 시점 지연 재확인.</b> <see cref="GetCertificate"/> 호출 시
/// 재확인 주기가 지났으면 파일 수정 시각을 비교하고, 바뀌었을 때만 재적재한다.
/// <c>FileSystemWatcher</c> 를 쓰지 않는 이유: k8s Secret 마운트(심볼릭 링크 원자 교체)
/// 에서 이벤트 누락으로 악명 높고, 전용 감시 핸들·스레드가 생긴다(9.5). 폴링은
/// 핸드셰이크가 있을 때만 파일 시각 조회 1회 — 유휴 커넥션 비용 0.
/// 운영 신호(SIGHUP 류)로 즉시 반영하려면 <see cref="Reload"/> 를 부른다.
/// </para>
/// <para>
/// <b>재적재 실패 = 기존 유지 + 경고.</b> 파일이 반쯤 쓰인 순간을 읽는 등의 실패로
/// 신규 접속을 거부하면 회전 실패가 곧 장애가 된다(가용성). 기존 인증서로 계속
/// 서비스하고 다음 주기에 재시도하며, 실패는 경고 로그로 관측된다(T-07 — 조용한
/// 실패 금지). 시작 시점 적재 실패는 예외다 — 잘못 조립된 서버는 뜨지 않는 편이 낫다.
/// </para>
/// <para>
/// <b>구세대 보관 — 직전 세대 1개.</b> 교체 직후 옛 인증서를 폐기하면 진행 중
/// 핸드셰이크가 해제된 키 핸들을 만진다(use-after-dispose). 직전 세대를 보관하고
/// 그 이전 세대만 폐기한다 — 핸드셰이크가 회전 주기(통상 수십 일) 두 번을 넘길 수
/// 없으므로 안전하다.
/// </para>
/// <para>
/// <b>Windows PEM 함정 내장.</b> <c>CreateFromPemFile</c> 이 만드는 ephemeral 개인키를
/// Schannel(<c>SslStream</c> Windows 백엔드)이 거부한다 — PFX 왕복 재적재로 흡수한다.
/// 플랫폼 무관 동일 동작을 위해 항상 왕복한다(적재는 드문 일이라 비용이 무의미하다).
/// </para>
/// <para>
/// <b>스레드 규약.</b> 스레드 안전하다. 재적재는 <see cref="Interlocked"/> 게이트로 한
/// 번에 하나만 돌고(해제는 <c>finally</c> — 9.2), 현재 인증서 참조는 <see cref="Volatile"/>
/// 로 원자 교체된다. 게이트를 얻지 못한 스레드는 기다리지 않고 현재 인증서를 쓴다.
/// </para>
/// </remarks>
public sealed class FileCertificateSource : IServerCertificateSource
{
    private static readonly EventId CertificateLoadedEvent = new(6006, "CertificateLoaded");
    private static readonly EventId CertificateReloadFailedEvent = new(6007, "CertificateReloadFailed");

    /// <summary>만료 임박 경고 창. 적재 시점에 만료까지 이보다 적게 남으면 경고한다.</summary>
    private static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(7);

    private readonly string? _pfxPath;
    private readonly string? _pfxPassword;
    private readonly string? _certificatePemPath;
    private readonly string? _privateKeyPemPath;
    private readonly TimeSpan _reloadCheckInterval;
    private readonly TimeProvider _timeProvider;
    private readonly IServerLogger _logger;

    private X509Certificate2 _current;

    /// <summary>직전 세대 — 진행 중 핸드셰이크 보호용. 게이트 안에서만 쓴다.</summary>
    private X509Certificate2? _previous;

    /// <summary>파일이 이 시각과 같으면 재적재하지 않는다. 게이트 안에서만 쓴다.</summary>
    private DateTime _loadedWriteTimeUtc;

    private long _nextCheckTimestamp;

    // 0 = 유휴, 1 = 재적재 중. 한 스레드만 파일을 만진다 — 나머지는 현재 인증서를 쓴다.
    private int _reloading;
    private int _disposed;

    /// <summary>설정을 검증하고 인증서를 즉시 적재한다.</summary>
    /// <param name="options">파일 경로·재확인 주기. 생성 이후의 옵션 변경은 반영되지 않는다.</param>
    /// <param name="timeProvider">시간 원본. 테스트에서 대체할 수 있다. 생략하면 시스템 시계.</param>
    /// <param name="logger">진단 로거. 생략하면 기록하지 않는다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>가 <see langword="null"/>일 때.</exception>
    /// <exception cref="InvalidOperationException">설정이 유효하지 않을 때.</exception>
    /// <exception cref="IOException">시작 시점 적재가 실패했을 때 — 잘못 조립된 서버는 뜨지 않는다.</exception>
    /// <exception cref="CryptographicException">인증서 파일을 해석할 수 없을 때.</exception>
    public FileCertificateSource(
        FileCertificateOptions options,
        TimeProvider? timeProvider = null,
        IServerLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _pfxPath = options.PfxPath;
        _pfxPassword = options.PfxPassword;
        _certificatePemPath = options.CertificatePemPath;
        _privateKeyPemPath = options.PrivateKeyPemPath;
        _reloadCheckInterval = options.ReloadCheckInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullServerLogger.Instance;

        // 시작 시점 적재 실패는 던진다 — 첫 커넥션이 아니라 조립 시점에 드러나야 한다.
        _loadedWriteTimeUtc = ReadLatestWriteTimeUtc();
        _current = LoadFromFiles();
        ScheduleNextCheck();
        LogLoaded(_current, rotated: false);
    }

    /// <inheritdoc />
    public X509Certificate2 GetCertificate()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (_reloadCheckInterval > TimeSpan.Zero
            && _timeProvider.GetTimestamp() >= Volatile.Read(ref _nextCheckTimestamp))
        {
            TryReload(force: false);
        }

        return Volatile.Read(ref _current);
    }

    /// <summary>파일 수정 시각과 무관하게 지금 재적재한다 — 운영 신호(SIGHUP 류)용.</summary>
    /// <remarks>실패해도 던지지 않는다 — 기존 인증서 유지 + 경고 로그(가동 중 실패 정책).</remarks>
    public void Reload()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        TryReload(force: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _current.Dispose();
        _previous?.Dispose();
    }

    private void TryReload(bool force)
    {
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) != 0)
        {
            // 다른 스레드가 재적재 중이다 — 기다리지 않고 현재 인증서를 쓴다.
            return;
        }

        try
        {
            // 성패와 무관하게 다음 확인 시점부터 갱신한다 — 실패가 핸드셰이크마다
            // 파일 IO 를 반복하게 두면 그 자체가 부하 증폭이다.
            ScheduleNextCheck();

            DateTime writeTimeUtc = ReadLatestWriteTimeUtc();
            if (!force && writeTimeUtc == _loadedWriteTimeUtc)
            {
                return;
            }

            X509Certificate2 loaded = LoadFromFiles();
            _loadedWriteTimeUtc = writeTimeUtc;

            X509Certificate2? retired = _previous;
            _previous = Volatile.Read(ref _current);
            Volatile.Write(ref _current, loaded);

            // 두 세대 전만 폐기한다 — 직전 세대는 진행 중 핸드셰이크가 참조할 수 있다.
            retired?.Dispose();

            LogLoaded(loaded, rotated: true);
        }
        catch (Exception exception)
            when (exception is IOException or CryptographicException or ArgumentException or UnauthorizedAccessException)
        {
            // 기존 유지 — 회전 실패가 장애가 되면 안 된다. 다음 주기(또는 다음 Reload)에 재시도.
            LogReloadFailed(exception);
        }
        finally
        {
            // 9.2 — 해제를 finally 에 두지 않으면 예외 하나가 회전을 영구 정지시킨다.
            Volatile.Write(ref _reloading, 0);
        }
    }

    private void ScheduleNextCheck()
    {
        if (_reloadCheckInterval <= TimeSpan.Zero)
        {
            return;
        }

        long intervalTimestampTicks = (long)(_reloadCheckInterval.TotalSeconds * _timeProvider.TimestampFrequency);
        Volatile.Write(ref _nextCheckTimestamp, _timeProvider.GetTimestamp() + intervalTimestampTicks);
    }

    private DateTime ReadLatestWriteTimeUtc()
    {
        if (_pfxPath is not null)
        {
            return File.GetLastWriteTimeUtc(_pfxPath);
        }

        // PEM 쌍은 두 파일 중 늦게 바뀐 쪽 — 교체 도구가 두 파일을 순차로 쓴다.
        DateTime certificateTime = File.GetLastWriteTimeUtc(_certificatePemPath!);
        DateTime keyTime = File.GetLastWriteTimeUtc(_privateKeyPemPath!);
        return certificateTime > keyTime ? certificateTime : keyTime;
    }

    private X509Certificate2 LoadFromFiles()
    {
        if (_pfxPath is not null)
        {
            return X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, _pfxPassword);
        }

        // Windows Schannel 은 CreateFromPemFile 의 ephemeral 개인키를 거부한다 —
        // PFX 왕복으로 흡수한다. 플랫폼 무관 동일 동작(적재는 드물어 비용 무의미).
        using X509Certificate2 ephemeral = X509Certificate2.CreateFromPemFile(_certificatePemPath!, _privateKeyPemPath!);
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
    }

    private void LogLoaded(X509Certificate2 certificate, bool rotated)
    {
        TimeSpan untilExpiry = certificate.NotAfter.ToUniversalTime() - _timeProvider.GetUtcNow().UtcDateTime;
        bool expiryImminent = untilExpiry < ExpiryWarningWindow;

        // 만료 임박은 경고로 승격 — 회전 파이프라인이 죽어 있다는 신호일 수 있다.
        LogLevel level = expiryImminent ? LogLevel.Warning : LogLevel.Information;
        if (_logger.IsEnabled(level))
        {
            _logger.Log(
                level,
                CertificateLoadedEvent,
                (Thumbprint: certificate.Thumbprint, certificate.NotAfter, rotated, expiryImminent),
                null,
                static (state, _) =>
                    $"서버 인증서 {(state.rotated ? "회전" : "적재")}: {state.Thumbprint}, " +
                    $"만료 {state.NotAfter:u}{(state.expiryImminent ? " — ⚠ 만료 임박, 회전 경로를 점검하라" : "")}");
        }
    }

    private void LogReloadFailed(Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(
                LogLevel.Warning,
                CertificateReloadFailedEvent,
                0,
                exception,
                static (_, error) =>
                    $"인증서 재적재 실패 — 기존 인증서로 계속 서비스하고 다음 주기에 재시도한다: {error?.Message}");
        }
    }
}
