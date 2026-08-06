using System;
using System.Collections.Generic;
using ChServerM.Identity;

namespace ChServerM.Hosting;

/// <summary>
/// 송신 압축 정책 — 무엇을 압축하지 않을 것인가 (T-11).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 압축 자체는 코덱(기계)의 몫이고, "이 페이로드를 압축할 것인가"는
/// 정책이다. 정책의 세 갈래: (1) 작은 페이로드 — 압축 헤더·CPU 비용이 이득을 넘는다
/// (레거시의 1024B 문턱 승계), (2) 비밀이 실리는 메시지 — 비밀과 공격자 제어 데이터가
/// 같은 압축 문맥에 섞이면 압축률 관찰로 비밀이 새는 CRIME 류 표면이 된다(T-11),
/// (3) 압축해도 줄지 않는 데이터 — 이 판정은 인코딩 결과로만 알 수 있으므로
/// <see cref="FrameWriter"/> 의 <c>WriteCompressedFrameAsync</c> 가 수행한다.
/// </para>
/// <para><b>스레드 규약.</b> 조립 시점에 구성하고 이후 읽기만 한다. 송신 경로가
/// <see cref="IsExcluded"/> 를 프레임마다 읽으므로, 동작 중 변경은 지원하지 않는다.</para>
/// </remarks>
public sealed class PayloadCompressionOptions
{
    /// <summary>기본 압축 문턱. 이 미만은 압축하지 않는다. 레거시 정책(1024B) 승계.</summary>
    public const int DefaultMinCompressLength = 1024;

    private readonly HashSet<ushort> _excluded = [];

    /// <summary>압축을 시도할 최소 페이로드 크기(바이트).</summary>
    /// <remarks>작은 페이로드는 압축 접두(4B)와 CPU 비용이 이득을 넘는다.
    /// 근거 수치는 <c>docs/BENCHMARKS.md</c> 압축 절.</remarks>
    public int MinCompressLength { get; set; } = DefaultMinCompressLength;

    /// <summary>메시지를 압축 제외 목록에 넣는다.</summary>
    /// <param name="messageId">압축하지 않을 메시지 식별자.</param>
    /// <returns>메서드 체이닝을 위한 자기 자신.</returns>
    /// <exception cref="ArgumentException">센티넬(0)이거나 이미 제외된 식별자일 때.</exception>
    /// <remarks>
    /// <b>토큰·자격 같은 비밀이 실리는 메시지는 반드시 제외한다</b> — 비밀과 공격자
    /// 제어 데이터가 같은 압축 문맥에 섞이면 압축률이 비밀의 관찰 통로가 된다(T-11, CRIME 류).
    /// </remarks>
    public PayloadCompressionOptions DoNotCompress(MessageId messageId)
    {
        if (messageId.IsNone)
        {
            throw new ArgumentException(
                "메시지 식별자 0 은 '설정되지 않음'을 뜻하는 센티넬이다.", nameof(messageId));
        }

        if (!_excluded.Add(messageId.Value))
        {
            throw new ArgumentException(
                $"메시지 식별자 {messageId.Value} 는 이미 압축 제외 목록에 있다.", nameof(messageId));
        }

        return this;
    }

    /// <summary>메시지가 압축 제외 대상인지 검사한다.</summary>
    internal bool IsExcluded(MessageId messageId) => _excluded.Contains(messageId.Value);

    /// <summary>설정을 검증한다.</summary>
    /// <exception cref="InvalidOperationException">값이 유효하지 않을 때.</exception>
    public void Validate()
    {
        if (MinCompressLength < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(MinCompressLength)} 는 0 이상이어야 한다: {MinCompressLength}");
        }
    }
}
