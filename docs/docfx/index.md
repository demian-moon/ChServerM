# ChServerM API 문서

XML 문서 주석에서 생성되는 API 레퍼런스다. **문서가 제품의 일부다** — public 표면 전부에
한글 XML 문서가 있고(CLAUDE.md 8.2), 이 사이트는 그것을 탐색 가능한 형태로 내보낸다.

- [API 레퍼런스](xref:ChServerM)
- **시각 가이드(초보자용)**: [시작·축 조립](guides/server.html) · [클라이언트](guides/client.html)
- 원문: 저장소의 `docs/GETTING-STARTED.md` · `docs/GUIDE-CHOOSING-AXES.md`

## 로컬에서 생성하기

```bash
dotnet tool restore
dotnet docfx docs/docfx/docfx.json          # metadata + build
dotnet docfx serve docs/docfx/_site         # http://localhost:8080
```

생성물(`api/`, `_site/`)은 커밋하지 않는다 — XML 주석이 정본이고 사이트는 파생물이다.
