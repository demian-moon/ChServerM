# 서드파티 고지 (Third-Party Notices)

ChServerM 은 Apache License 2.0 으로 배포된다(`LICENSE`). 이 문서는 서드파티
의존성의 라이선스 전수 감사 결과다.

- **감사일**: 2026-08-12 (라이선스 확정과 동시 수행, ROADMAP Phase 21)
- **방법**: 선언이 아니라 실물 기준 — `Directory.Packages.props` 전 항목 +
  `Server/` 어셈블리의 **전이 의존성 포함** 그래프(`dotnet list package
  --include-transitive`)를 NuGet 캐시의 nuspec `license` 메타데이터에서 직접
  읽었다. nuspec 이 URL 표기인 구세대 패키지는 원문을 열어 확인했다.
- **판정**: **호환 충돌 없음.** 카피레프트(GPL/LGPL/AGPL) 계열 0건.
  런타임 재배포 의존성은 전부 MIT · BSD-3-Clause · Apache-2.0 · PostgreSQL
  라이선스 — 모두 Apache-2.0 배포와 호환된다.

## 런타임 의존성 (패키지 소비자에게 전파됨)

어댑터 패키지를 설치할 때만 해당 벤더 의존성이 따라온다(벤더 격리 규약).
Core 는 의존성 0.

| 패키지 | 버전 | 라이선스 | 유입 경로 |
|---|---|---|---|
| MemoryPack (+ .Core/.Generator) | 1.21.4 | MIT | Serialization.MemoryPack |
| Google.Protobuf | 3.35.1 | BSD-3-Clause | Serialization.Protobuf |
| FlatSharp.Runtime | 7.9.0 | Apache-2.0 | Serialization.FlatBuffers |
| K4os.Compression.LZ4 | 1.3.8 | MIT (원문 확인 — nuspec 은 URL 표기) | Compression.LZ4 |
| StackExchange.Redis (+ RESPite) | 3.1.13 | MIT | Persistence.Redis |
| Npgsql | 10.0.3 | PostgreSQL License | Persistence.Postgres |
| Microsoft.Extensions.* (Logging·Options·Configuration·DI·Diagnostics·Primitives) | 10.0.10 | MIT | Logging.Extensions · Cluster.Hosting 등 |
| Microsoft.Extensions.Identity.Core (+ AspNetCore.Cryptography.*) | 10.0.10 | MIT | Security.AspNetIdentity |
| System.IO.Hashing | 10.0.5 | MIT | Core 해싱(XxHash3) |
| System.Collections.Immutable · System.Reflection.Metadata 외 BCL 보조 | 9.0.0 등 | MIT | 전이 |
| 구세대 corefx 패키지 (System.Buffers·System.Memory·System.Numerics.Vectors·System.Threading.Tasks.Extensions·NETStandard.Library) | 4.5.x/2.0.3 | MIT (dotnet/corefx LICENSE.TXT — nuspec 은 URL 표기) | netstandard2.0 전이 (net10.0 앱에서는 프레임워크 내장으로 대체되는 참조용 파사드) |
| Microsoft.NETCore.Platforms | 1.1.0 | Microsoft .NET Library 라이선스(재배포 허용) | NETStandard.Library 전이 — 런타임 자산 없음(플랫폼 메타데이터 전용) |

## 빌드 타임 전용 (산출물에 실리지 않음)

| 패키지 | 버전 | 라이선스 | 용도 |
|---|---|---|---|
| Microsoft.CodeAnalysis.CSharp / .Common / .Analyzers | 4.14.0 / 3.11.0 | MIT | SourceGen·Analyzers 빌드 |
| Microsoft.CodeAnalysis.PublicApiAnalyzers | 5.6.0 | MIT | API 승인 게이트 (PrivateAssets) |
| FlatSharp.Compiler | 7.9.0 | Apache-2.0 | fbs 컴파일 |
| Grpc.Tools | 2.83.0 | Apache-2.0 | proto 컴파일 (PrivateAssets) |
| Microsoft.NET.ILLink.Tasks | 10.0.5 | MIT | AOT/트리밍 |

## 테스트·벤치 전용 (배포와 무관)

xunit(Apache-2.0) · xunit.runner.visualstudio(Apache-2.0) ·
Xunit.SkippableFact(MS-PL) · Microsoft.NET.Test.Sdk(MIT) ·
coverlet.collector(MIT) · Testcontainers 3종(MIT) · BenchmarkDotNet(MIT) ·
MessagePack(MIT, 벤치 비교용).

MS-PL(Xunit.SkippableFact)은 약한 카피레프트지만 **테스트 프로젝트에서만 쓰이고
재배포되지 않으므로** 제품 라이선스에 영향이 없다.

## 유지 규약

- 새 PackageReference 를 추가하면 이 문서에 행을 추가한다 — 특히 어댑터의
  런타임 의존성. Dependabot 메이저 업데이트 시 라이선스 변경 여부를 함께 본다
  (버전을 올리며 라이선스를 바꾸는 사례가 실재한다).
- 이 문서는 저장소 정본이다. 패키지에는 `LICENSE`·`NOTICE` 가 동봉되고,
  각 의존성의 라이선스 전문은 NuGet 패키지 자신이 나른다.
