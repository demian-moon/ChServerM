using System;
using ChServerM.Security;
using ChServerM.Security.AspNetIdentity;
using Xunit;

namespace ChServerM.Security.AspNetIdentity.Tests;

/// <summary>
/// PBKDF2 어댑터의 계약 — 왕복·오답·재해싱 신호·레거시 형식 호환을 고정한다.
/// </summary>
/// <remarks>
/// 반복 횟수를 낮춰(1,000) 돌린다 — 검증 대상은 어댑터의 매핑·형식이지 PBKDF2 의
/// 속도가 아니다. 기본값(600,000)으로 돌리면 테스트당 수백 ms 를 태운다.
/// </remarks>
public sealed class AspNetIdentityPasswordHasherTests
{
    private const int FastIterations = 1_000;

    [Fact]
    public void Hash_then_verify_roundtrips()
    {
        AspNetIdentityPasswordHasher hasher = new(FastIterations);

        string hashed = hasher.Hash("correct horse battery staple");

        Assert.Equal(PasswordVerification.Success, hasher.Verify(hashed, "correct horse battery staple"));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        AspNetIdentityPasswordHasher hasher = new(FastIterations);

        string hashed = hasher.Hash("correct horse battery staple");

        Assert.Equal(PasswordVerification.Failed, hasher.Verify(hashed, "Tr0ub4dor&3"));
    }

    [Fact]
    public void Same_password_produces_different_hashes()
    {
        // 비밀번호별 랜덤 솔트의 관측 — 같은 입력이 같은 해시면 레인보 테이블이 성립한다.
        AspNetIdentityPasswordHasher hasher = new(FastIterations);

        Assert.NotEqual(hasher.Hash("pw"), hasher.Hash("pw"));
    }

    [Fact]
    public void Old_iteration_count_verifies_but_signals_rehash()
    {
        // 반복 횟수를 올린 뒤에도 기존 해시는 검증돼야 하고(형식에 파라미터 내장),
        // 재해싱 신호가 와야 한다 — 레거시는 이 신호 자체를 버렸다(AuthM #4 의 역).
        AspNetIdentityPasswordHasher old = new(FastIterations);
        string oldHash = old.Hash("pw");

        AspNetIdentityPasswordHasher upgraded = new(FastIterations * 2);

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, upgraded.Verify(oldHash, "pw"));
        Assert.Equal(PasswordVerification.Failed, upgraded.Verify(oldHash, "wrong"));
    }

    [Fact]
    public void Garbage_stored_hash_fails_instead_of_throwing()
    {
        // 저장소가 오염돼도 인증 경로는 값으로 실패해야 한다 — 예외는 T-16 비용 증폭 경로다.
        AspNetIdentityPasswordHasher hasher = new(FastIterations);

        Assert.Equal(PasswordVerification.Failed, hasher.Verify("not-a-real-hash", "pw"));
    }

    [Fact]
    public void Legacy_default_options_hash_still_verifies()
    {
        // 레거시 AuthM 은 옵션 없는 new PasswordHasher<object>() 였다 — 그 형식의 해시가
        // 이 어댑터로 검증돼야 계정 이전 경로가 성립한다(ADR-0018 채택 근거).
        Microsoft.AspNetCore.Identity.PasswordHasher<object> legacy = new();
        string legacyHash = legacy.HashPassword(new object(), "legacy-user-pw");

        AspNetIdentityPasswordHasher adapter = new(FastIterations);
        PasswordVerification verdict = adapter.Verify(legacyHash, "legacy-user-pw");

        // 레거시 기본(V3, 100k)과 반복 횟수가 다르므로 Success 또는 SuccessRehashNeeded —
        // 어느 쪽이든 "검증된다"가 계약이다.
        Assert.True(
            verdict is PasswordVerification.Success or PasswordVerification.SuccessRehashNeeded,
            $"레거시 형식 해시가 검증되지 않았다: {verdict}");
    }

    [Fact]
    public void Invalid_iteration_count_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(static () => new AspNetIdentityPasswordHasher(0));
    }
}
