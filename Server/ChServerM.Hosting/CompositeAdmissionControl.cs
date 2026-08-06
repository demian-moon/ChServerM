using System;
using System.Collections.Generic;
using System.Net;
using ChServerM.Resilience;

namespace ChServerM.Hosting;

/// <summary>
/// 여러 <see cref="IAdmissionControl"/>을 <b>AND</b>로 묶는다 — 하나라도 거부하면 거부.
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> 과부하 방어는 대개 한 규칙이 아니다 — 전역 연결 속도 + IP별 제한 +
/// 자원 워터마크를 동시에 걸고 싶다. 이 컴포지트가 축을 조합 가능하게 만든다(축은
/// 교체뿐 아니라 결합도 가능해야 한다).
/// </para>
/// <para>
/// <b>⚠ 단락(short-circuit) 순서가 부수효과에 영향을 준다.</b> 앞 규칙이 거부하면 뒤
/// 규칙의 <see cref="IAdmissionControl.TryAdmit"/> 은 <b>호출되지 않는다</b> — 토큰 버킷
/// 같은 소비형 규칙은 소비되지 않는다. 그래서 <b>싸고 자주 거부하는 규칙을 앞에</b> 둬야
/// 뒤의 비싼·희소한 규칙이 헛되이 소비되지 않는다. 이 순서 규약은 조립하는 쪽의 책임이다.
/// </para>
/// <para><b>스레드 규약.</b> 자식 규칙이 스레드 안전하면 이것도 안전하다. 목록은 불변이다.</para>
/// </remarks>
public sealed class CompositeAdmissionControl : IAdmissionControl
{
    private readonly IAdmissionControl[] _controls;

    /// <summary>규칙들을 순서대로 묶는다.</summary>
    /// <param name="controls">평가 순서대로의 규칙. 앞이 먼저 평가되고, 거부하면 뒤는 건너뛴다.</param>
    /// <exception cref="ArgumentNullException"><paramref name="controls"/>나 그 원소가 <see langword="null"/>일 때.</exception>
    /// <exception cref="ArgumentException">규칙이 하나도 없을 때 — 아무것도 안 하는 죽은 조립이다.</exception>
    public CompositeAdmissionControl(params IReadOnlyList<IAdmissionControl> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);

        if (controls.Count == 0)
        {
            throw new ArgumentException(
                "규칙이 하나도 없다. 아무것도 판정하지 않는 컴포지트는 무의미하다.", nameof(controls));
        }

        IAdmissionControl[] copy = new IAdmissionControl[controls.Count];
        for (int i = 0; i < controls.Count; i++)
        {
            copy[i] = controls[i] ?? throw new ArgumentNullException(nameof(controls), "규칙에 null 이 있다.");
        }

        _controls = copy;
    }

    /// <inheritdoc />
    public AdmissionDecision TryAdmit(EndPoint? remoteEndPoint)
    {
        foreach (IAdmissionControl control in _controls)
        {
            AdmissionDecision decision = control.TryAdmit(remoteEndPoint);
            if (!decision.IsAdmitted)
            {
                // 첫 거부에서 멈춘다 — 뒤 규칙의 소비형 부수효과를 일으키지 않는다.
                return decision;
            }
        }

        return AdmissionDecision.Admit();
    }
}
