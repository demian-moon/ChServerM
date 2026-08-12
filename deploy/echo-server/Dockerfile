# EchoServer(realtime-stateful 참조 프로필) Native AOT 컨테이너 이미지 (Phase 22).
#
# 저장소 루트를 컨텍스트로 빌드한다:
#   docker build -t chserverm/echo-server:0.1.0 -f deploy/echo-server/Dockerfile .
#
# 이미지 구성 원칙:
#   - Native AOT 산출물이므로 런타임 이미지는 runtime-deps(닷넷 런타임 없음)로 충분하다.
#   - InvariantGlobalization=true(루트 Directory.Build.props)라 ICU 도 필요 없다.
#   - 비루트(app 사용자)로 실행한다. 5000 포트는 특권 포트가 아니므로 문제없다.

# ── 빌드 스테이지 ─────────────────────────────────────────────
# SDK 태그는 global.json 의 피처 밴드(10.0.2xx)와 맞춘다. `sdk:10.0` 같은
# 유동 태그는 다른 밴드로 흘러가 rollForward=latestPatch 를 만족하지 못하면
# 컨테이너 안에서만 빌드가 깨진다 — CI SDK 드리프트와 같은 함정이다.
FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build

# Native AOT 의 네이티브 링크 단계는 clang 과 zlib 헤더가 필요하다.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# .dockerignore 가 bin/obj·레거시·문서를 걸러낸다 — 소스와 빌드 규약 파일만 온다.
COPY . .

# PublishAot 는 csproj 의 선언을 따른다(전역 속성으로 넘기면 참조 그래프의
# netstandard2.0 생성기가 NETSDK1207 로 터진다 — eng/build.ps1 과 같은 규칙).
RUN dotnet publish Samples/ChServerM.Samples.EchoServer/ChServerM.Samples.EchoServer.csproj \
    -c Release -o /app/publish

# ── 런타임 스테이지 ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime

WORKDIR /app
COPY --from=build /app/publish/ChServerM.Samples.EchoServer .

# .NET 공식 이미지가 제공하는 비루트 사용자.
USER $APP_UID

EXPOSE 5000

ENTRYPOINT ["./ChServerM.Samples.EchoServer"]
# 인자를 바꾸면 자체 검증 모드(무인자)로도 돌릴 수 있다.
CMD ["--serve", "5000"]
