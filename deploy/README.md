# deploy/ — 컨테이너 이미지와 배포 예제 (Phase 22)

프레임워크 자체는 배포 형태를 강제하지 않는다. 여기는 **참조 프로필을 실제로
컨테이너·K8s 에 올리는 방법을 실증하는 예제**다 — 축 조합이 달라져도 같은 틀을 쓴다.

## echo-server/ — realtime-stateful 프로필의 컨테이너화

`Samples/ChServerM.Samples.EchoServer` 의 `--serve` 모드를 Native AOT 로 빌드해
runtime-deps 이미지에 싣는다. 닷넷 런타임도 ICU 도 이미지에 없다
(AOT + `InvariantGlobalization`).

### 이미지 빌드 (저장소 루트에서)

```bash
docker build -t chserverm/echo-server:0.1.0 -f deploy/echo-server/Dockerfile .
```

- 빌드 컨텍스트는 저장소 루트다 — `Server/` 소스 전체가 필요하다.
  `.dockerignore` 가 bin/obj·레거시 트리·문서를 걸러낸다.
- SDK 이미지 태그는 `global.json` 의 피처 밴드(10.0.2xx)와 맞춰져 있다.
  `global.json` 을 올리면 Dockerfile 의 `FROM` 태그도 함께 올린다.

### 단독 실행 확인

```bash
docker run --rm -p 5000:5000 chserverm/echo-server:0.1.0
# 인자를 바꾸면 자체 검증 모드(1000회 왕복 후 종료):
docker run --rm chserverm/echo-server:0.1.0 ""
```

`docker stop`(SIGTERM)이 정상 종료 경로다 — 신규 수용을 중단하고 진행 중
커넥션을 드레인한 뒤 내려간다. 샘플이 `PosixSignalRegistration` 으로 SIGTERM 을
받는 이유이며, 이것이 없으면 K8s 롤링 업데이트마다 드레인 없이 즉사한다.

### K8s 배포

```bash
kubectl apply -f deploy/echo-server/k8s/
kubectl port-forward svc/chserverm-echo 5000:5000   # 로컬 확인용
```

매니페스트가 고정하는 것:

| 항목 | 값 | 이유 |
|---|---|---|
| 종료 유예 | 30초 | 샘플의 드레인 타임아웃(10초)보다 여유 있게 |
| 프로브 | TCP 연결 수립 | 프레임 프로토콜이라 HTTP 프로브 불가. 심층 헬스는 `Diagnostics.Http` 조합에서 `httpGet` 으로 교체 |
| CPU limit | 없음 | ServerGC 힙 수에 영향 — 실측 후 정한다(측정 없는 최적화 금지) |
| 보안 | 비루트·특권 상승 금지·읽기 전용 루트 FS | AOT 단일 바이너리라 쓰기 가능한 파일시스템이 필요 없다 |

### 다른 조합을 배포하려면

이 틀에서 바뀌는 것은 셋뿐이다: publish 대상 프로젝트, 노출 포트,
프로브 방식(HTTP 전송 조합이면 `httpGet` 사용 가능). 세션을 외부화한
stateless-web 프로필은 여기에 세션 저장소(Redis/PostgreSQL) 연결 설정이 더해진다.
