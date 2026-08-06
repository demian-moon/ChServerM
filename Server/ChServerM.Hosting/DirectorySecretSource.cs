using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using ChServerM.Security;

namespace ChServerM.Hosting;

/// <summary>
/// 디렉터리의 파일-퍼-시크릿에서 읽는 원천 (k8s Secret 마운트 표준 경로).
/// </summary>
/// <remarks>
/// <para>
/// <b>존재 이유.</b> k8s 는 Secret 을 <c>/run/secrets/&lt;이름&gt;</c> 형태의 파일로
/// 마운트한다(docker secrets 도 동일 관례). 이름 = 파일명, 값 = 파일 내용이다.
/// </para>
/// <para>
/// <b>캐시가 없다 — 조회마다 다시 읽는다.</b> 시크릿 조회는 시작·재연결 시점의
/// 저빈도 경로라 캐시할 이득이 없고, k8s 는 마운트된 파일을 회전시키므로
/// 캐시는 회전을 가리는 장치가 된다.
/// </para>
/// <para>
/// <b>끝 개행을 제거한다.</b> 에디터·<c>echo</c>·k8s 도구가 값 끝에 개행을 붙이는
/// 일이 흔하다 — 개행 붙은 암호는 "맞는데 틀리는" 진단 최악의 사고다.
/// 값 내부 공백·개행은 보존한다(다줄 시크릿 — PEM 류).
/// </para>
/// <para>
/// <b>이름의 경로 문자를 거부한다.</b> 시크릿 이름이 외부 입력일 일은 없어야 하지만,
/// 만에 하나 섞였을 때 <c>../</c> 로 디렉터리를 탈출해 임의 파일을 읽는 경로를
/// 원천에서 차단한다.
/// </para>
/// <para><b>스레드 규약.</b> 무상태다. 어디서 불러도 안전하다.</para>
/// </remarks>
public sealed class DirectorySecretSource : ISecretSource
{
    private readonly string _directoryPath;

    /// <summary>시크릿 디렉터리를 지정해 원천을 만든다.</summary>
    /// <param name="directoryPath">파일-퍼-시크릿 디렉터리 경로.</param>
    /// <exception cref="ArgumentException">경로가 비어 있을 때.</exception>
    /// <exception cref="DirectoryNotFoundException">디렉터리가 없을 때 — 잘못 조립된
    /// 서버는 첫 조회가 아니라 조립 시점에 실패해야 한다.</exception>
    public DirectorySecretSource(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"시크릿 디렉터리가 없다: {directoryPath}. 마운트·경로 설정을 확인한다.");
        }

        _directoryPath = directoryPath;
    }

    /// <inheritdoc />
    public bool TryGetSecret(string name, [NotNullWhen(true)] out string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        value = null;

        // 경로 탈출 방어 — 이름은 파일명이지 경로가 아니다.
        if (name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string path = Path.Combine(_directoryPath, name);
        if (!File.Exists(path))
        {
            return false;
        }

        // 끝 개행만 제거 — 값 내부 개행(PEM 류 다줄 시크릿)은 보존한다.
        string contents = File.ReadAllText(path).TrimEnd('\r', '\n');

        // 빈 값 = 부재(계약) — 빈 파일로 조용히 진행하지 않는다.
        if (contents.Length == 0)
        {
            return false;
        }

        value = contents;
        return true;
    }
}
