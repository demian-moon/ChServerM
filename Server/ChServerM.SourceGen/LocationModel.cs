using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ChServerM.SourceGen;

/// <summary><see cref="Location"/> 의 값 표현.</summary>
/// <remarks>
/// <b>존재 이유는 증분 캐시다.</b> Roslyn 의 <see cref="Location"/> 은 값 동등성이 없어서
/// 모델에 그대로 담으면 <b>편집할 때마다 모델이 달라진 것으로 보여</b> 생성이 다시 돈다.
/// 제너레이터 모델은 전부 값 동등성이어야 한다는 규약(Phase 7)의 부속물이다.
/// </remarks>
internal sealed record LocationModel(
    string FilePath,
    int Start,
    int Length,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter)
{
    /// <summary>Roslyn 위치에서 값 표현을 만든다.</summary>
    public static LocationModel From(Location location)
    {
        FileLinePositionSpan lineSpan = location.GetLineSpan();
        return new LocationModel(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            lineSpan.StartLinePosition.Line,
            lineSpan.StartLinePosition.Character,
            lineSpan.EndLinePosition.Line,
            lineSpan.EndLinePosition.Character);
    }

    /// <summary>진단에 붙일 Roslyn 위치로 되돌린다.</summary>
    public Location ToLocation()
        => FilePath.Length == 0
            ? Location.None
            : Location.Create(
                FilePath,
                new TextSpan(Start, Length),
                new LinePositionSpan(
                    new LinePosition(StartLine, StartCharacter),
                    new LinePosition(EndLine, EndCharacter)));
}
