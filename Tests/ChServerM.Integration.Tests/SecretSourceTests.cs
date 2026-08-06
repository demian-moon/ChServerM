using System;
using System.IO;
using ChServerM.Hosting;
using ChServerM.Security;
using Xunit;

namespace ChServerM.Integration.Tests;

/// <summary>
/// 시크릿 원천 2종의 계약 — 프리픽스·부재·<b>빈 값 = 부재</b>·끝 개행 제거·경로 탈출
/// 방어·회전(캐시 없음)을 고정한다.
/// </summary>
public sealed class SecretSourceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("chsm-secret-").FullName;

    /// <summary>테스트 간 충돌을 막는 고유 환경변수 이름.</summary>
    private readonly string _variableName = $"CHSM_TEST_{Guid.NewGuid():N}";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_variableName, null);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 임시 디렉터리 정리 실패는 테스트 실패가 아니다.
        }
    }

    // ── 환경변수 원천 ────────────────────────────────────────────

    [Fact]
    public void Environment_source_reads_with_prefix()
    {
        Environment.SetEnvironmentVariable(_variableName, "s3cret");

        // 이름을 프리픽스+나머지로 갈라 프리픽스 결합을 검증한다.
        string prefix = _variableName[..8];
        string name = _variableName[8..];
        EnvironmentSecretSource source = new(prefix);

        Assert.True(source.TryGetSecret(name, out string? value));
        Assert.Equal("s3cret", value);
    }

    [Fact]
    public void Environment_source_treats_missing_and_empty_as_absent()
    {
        EnvironmentSecretSource source = new();

        // 부재.
        Assert.False(source.TryGetSecret(_variableName, out string? missing));
        Assert.Null(missing);

        // 존재하지만 빈 값 — "변수는 만들고 값을 빠뜨린" 착각은 부재로 드러나야 한다.
        Environment.SetEnvironmentVariable(_variableName, "");
        Assert.False(source.TryGetSecret(_variableName, out string? empty));
        Assert.Null(empty);
    }

    // ── 디렉터리 원천 ────────────────────────────────────────────

    [Fact]
    public void Directory_source_reads_file_and_strips_trailing_newline()
    {
        // 에디터·echo·k8s 도구가 붙이는 끝 개행 — "맞는데 틀리는" 암호 사고의 원인.
        File.WriteAllText(Path.Combine(_directory, "db-password"), "hunter2\r\n");
        DirectorySecretSource source = new(_directory);

        Assert.True(source.TryGetSecret("db-password", out string? value));
        Assert.Equal("hunter2", value);
    }

    [Fact]
    public void Directory_source_preserves_interior_newlines()
    {
        // PEM 류 다줄 시크릿 — 내부 개행은 값의 일부다.
        File.WriteAllText(Path.Combine(_directory, "tls-key"), "line1\nline2\n");
        DirectorySecretSource source = new(_directory);

        Assert.True(source.TryGetSecret("tls-key", out string? value));
        Assert.Equal("line1\nline2", value);
    }

    [Fact]
    public void Directory_source_treats_missing_and_empty_as_absent()
    {
        File.WriteAllText(Path.Combine(_directory, "empty"), "");
        DirectorySecretSource source = new(_directory);

        Assert.False(source.TryGetSecret("nope", out _));
        Assert.False(source.TryGetSecret("empty", out _));
    }

    [Fact]
    public void Directory_source_rejects_path_traversal_names()
    {
        File.WriteAllText(Path.Combine(_directory, "legit"), "value");
        DirectorySecretSource source = new(_directory);

        Assert.False(source.TryGetSecret("../legit", out _));
        Assert.False(source.TryGetSecret("sub/legit", out _));
        Assert.False(source.TryGetSecret(@"sub\legit", out _));
        Assert.False(source.TryGetSecret("..", out _));
    }

    [Fact]
    public void Directory_source_sees_rotated_value_without_restart()
    {
        // 캐시가 없어야 k8s 마운트 회전이 즉시 보인다.
        string path = Path.Combine(_directory, "rotating");
        File.WriteAllText(path, "old");
        DirectorySecretSource source = new(_directory);

        Assert.True(source.TryGetSecret("rotating", out string? first));
        Assert.Equal("old", first);

        File.WriteAllText(path, "new");
        Assert.True(source.TryGetSecret("rotating", out string? second));
        Assert.Equal("new", second);
    }

    [Fact]
    public void Directory_source_requires_existing_directory_at_assembly()
    {
        // 잘못 조립된 서버는 첫 조회가 아니라 조립 시점에 실패해야 한다.
        Assert.Throws<DirectoryNotFoundException>(
            () => new DirectorySecretSource(Path.Combine(_directory, "does-not-exist")));
    }
}
