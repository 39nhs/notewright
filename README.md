# notewright — Claude Code 작곡 플러그인

Claude가 **com.graze.music 엔진(재생 V2)** 으로 곡을 작곡·편곡·믹싱하고 **WAV, 스템, MIDI, 악보(MusicXML/PDF), Unity 곡 폴더**로
출력하는 Claude Code 플러그인입니다. 엔진(`engine/`)을 Unity 없이 컴파일해 오프라인으로 렌더링합니다.

이 프로젝트는 com.graze.music Unity 모듈과 **따로 개발**합니다. 엔진은 2026-10-01에 Unity 모듈에서 복사해 온 뒤 여기서 독립적으로
관리하며, 두 저장소는 서로 병합하지 않습니다([AGENTS.md](AGENTS.md)). `--unity` 출력은 분리 시점의 Unity 모듈 song.json 형식입니다.

## 할 수 있는 것

1. **가상악기 자동 탐색 / 내 컴퓨터 악기 사용**
   - 조건(“따뜻한 패드”, “첼로 피치카토”, “드럼”)으로 검색해 가장 맞는 악기를 고르고, 필요하면 자동 다운로드
   - 내장 신스 11종(서브 베이스, 리드, 패드, 일렉 피아노, 벨, 플럭, 드럼킷 …) — 다운로드 없음
   - **VSCO 2 Community Edition** 오케스트라 67종(현악, 목관, 금관, 하프, 피아노, 타악기 …, CC0) — 쓰는 샘플만 받음
   - 내 컴퓨터의 **SFZ 악기, 음이름이 붙은 WAV 세트, 드럼 원샷 폴더, graze `Instruments~` 라이브러리**를 스캔해 등록
   - VST/Kontakt는 직접 로드할 수 없습니다 → WAV/SFZ로 내보낸 뒤 스캔(스킬 문서에 방법 있음)
2. **V2 엔진으로 원하는 형식의 작곡·믹싱**
   - 곡 스펙(JSON): 악기 · 트랙 · 패턴(이벤트/멜로디/드럼 그리드) · 섹션 구성 · 템포 변화 · 마스터/트랙 이펙트 · 글리치 구간
   - 렌더 리포트(피크, RMS, 섹션별 음량, 트랙별 음량, 클리핑, 드롭된 노트)와 자동 조언으로 Claude가 수치 기반 믹싱
3. **출력**: WAV(48 kHz 24-bit 기본) · 트랙별 스템 · MIDI · MusicXML 악보 · PDF 악보(MuseScore 설치 시) · Unity `SongJsonBuilder` 폴더

## 설치

필요: **Python 3.9+**와 렌더러를 실행할 .NET 중 하나.

| OS | 렌더러 런타임 (하나만 있으면 됨) |
|---|---|
| Windows | **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`), 없으면 기본 .NET Framework 4.x (설치 불필요) |
| Linux | **.NET 8 SDK** (`sudo apt-get install dotnet-sdk-8.0`) 또는 Mono (`sudo apt-get install mono-devel`) |
| macOS | **.NET 8 SDK** (`brew install --cask dotnet-sdk`) 또는 Mono (`brew install mono`) |

어느 런타임이든 바이트 단위로 같은 소리를 냅니다(엔진 테스트를 Mono와 .NET 양쪽에서 실행). .NET SDK가 있으면 먼저 쓰고,
.NET Framework나 Mono보다 약 2배 빠릅니다. `doctor`가 더 빠른 런타임을 쓸 수 있으면 `faster`로 알려 줍니다. 런타임이 없어도 MIDI와 악보는 만들 수 있습니다(`render --score-only`).
PDF 악보는 선택 사항: [MuseScore](https://musescore.org).

Claude Code에서:
```
/plugin marketplace add 39nhs/notewright
/plugin install notewright@notewright
```
claude.ai 채팅·Cowork에서는 **Customize → Plugins**에서 마켓플레이스 `39nhs/notewright`를 추가해 설치합니다. 그곳의 코드 실행
환경에 .NET이 없으면 Claude가 Ubuntu 패키지 저장소에서 `dotnet-sdk-8.0`을 설치하고, VSCO 2 악기는 github.com에서 git으로 받습니다
(claude.ai 기본 네트워크 설정 “패키지 관리자만”에서 허용되는 주소들). 설치할 수 없는 환경이면 MIDI와 악보만 만들어 줍니다.
로컬 클론으로 시험할 때: `claude --plugin-dir /path/to/notewright`.

설치 후 “신나는 8비트 보스전 BGM 1분짜리 만들어서 wav랑 악보로 줘”처럼 요청하면 `compose-song` 스킬이 동작합니다
(`/notewright:compose-song`으로 직접 호출도 가능).

## 명령어 (Claude가 쓰는 CLI, 직접 써도 됨)

```bash
python3 notewright.py doctor                              # 환경 점검 + 렌더러 빌드(첫 실행 시 C# 컴파일러 자동 다운로드)
python3 notewright.py instruments find warm pad           # 악기 검색
python3 notewright.py instruments scan ~/Samples/MyPiano  # 내 악기 등록
python3 notewright.py new mysong.json --template pop      # 템플릿(pop, orchestral)
python3 notewright.py check mysong.json                   # 컴파일 + 요약(렌더 없음)
python3 notewright.py render mysong.json --stems --pdf --unity
python3 notewright.py render mysong.json --section chorus # 일부만 빠르게
```
결과물: `<스펙 폴더>/out/<제목>/` 에 `<제목>.wav`, `.mid`, `.musicxml`(+`.pdf`), `report.json`, `stems/`, `unity/`.
사용자 데이터(다운로드한 악기, 카탈로그, 빌드된 렌더러)는 `~/.notewright`(`NOTEWRIGHT_HOME`으로 변경).

스펙 형식과 작곡/믹싱 가이드: [compose-song 스킬](skills/compose-song/SKILL.md) ·
[spec](skills/compose-song/reference/spec.md) · [instruments](skills/compose-song/reference/instruments.md) ·
[mixing](skills/compose-song/reference/mixing.md) · [composition](skills/compose-song/reference/composition.md).
예제: [pop.json](examples/pop.json) · [orchestral.json](examples/orchestral.json).

## 구조

| 경로 | 내용 |
|---|---|
| `.claude-plugin/` | 플러그인 매니페스트, 마켓플레이스 |
| `skills/compose-song/` | Claude 작업 절차 + 참조 문서 |
| `notewright.py`, `notewright/` | Python: 악기 카탈로그(`catalog`, `sfz`, `synthkit`), 스펙 컴파일러(`spec`), 내보내기(`midi`, `musicxml`, `export`), 엔진 빌드/실행(`engine`), CLI |
| `engine/Runtime/` | 음악 엔진(C#, 재생 버전 V1·V2) — [재생 버전](engine/playback-versions.md) |
| `engine/Renderer/` | 헤드리스 렌더러: [song.json](engine/song-json.md) → 엔진 → WAV + 리포트 |
| `engine/Tests/` | 엔진 테스트(재생 지문 포함): `bash engine/run-tests.sh` (Mono), `NOTEWRIGHT_RUNTIME=dotnet bash engine/run-tests.sh` (.NET) |
| `data/registry.json` | 다운로드 가능한 라이브러리 목록(VSCO 2 CE, 고정 커밋) — `tools/build_vsco2_registry.py`로 생성 |
| `examples/`, `tests/` | 예제 스펙, 플러그인 테스트(`python3 -m unittest discover -s tests`) |

## 네트워크와 파일

플러그인은 아래만 내려받고, 사용자 데이터를 외부로 보내지 않습니다.

- Mono·.NET Framework로 렌더할 때만, 첫 렌더에 C# 컴파일러 Roslyn 4.8.0 패키지 1회 —
  `https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/` (.NET SDK는 자체 컴파일러를 써서 받지 않음)
- VSCO 2 CE 악기: 곡에 쓰는 샘플만 — `https://raw.githubusercontent.com/sgossner/VSCO-2-CE/<고정 커밋>/`, 이 주소가 막혀 있으면
  같은 커밋을 git으로 `https://github.com/sgossner/VSCO-2-CE`에서 (필요한 파일만 받는 임시 저장소, 받은 뒤 삭제)
- `doctor`는 위 악기 주소에 접속되는지 확인합니다(작은 파일 1개 요청, 또는 `git ls-remote`).
- 플러그인이 시스템 패키지를 직접 설치하지는 않습니다. claude.ai·Cowork 같은 일회용 실행 환경에서는 스킬 안내에 따라 Claude가
  `dotnet-sdk-8.0`을 OS 패키지 저장소에서 설치할 수 있고, 내 컴퓨터에서는 설치 명령을 알려 주기만 합니다.
- 위 파일과 빌드된 렌더러, 악기 카탈로그는 `~/.notewright`(`NOTEWRIGHT_HOME`)에 저장합니다. 렌더 결과는 스펙 폴더의 `out/`에 씁니다.
- `instruments scan`은 사용자가 지정한 폴더만 읽습니다. `render --pdf`는 설치된 MuseScore를 실행합니다.

## 한계

- 엔진은 파트당 샘플 1개를 재생: 트랙은 사용한 샘플 영역마다 파트를 하나씩 쓰고, 곡당 최대 32파트(초과 시 자동으로 영역을 합침).
- 리버브·EQ·오토메이션·피치벤드 없음(에코, 필터, 컴프, 드라이브, 게이트, 글리치는 있음). 벨로시티 레이어는 악기당 하나.
- 악보는 박마다 32분음표/16분 셋잇단 격자로 양자화, 트랙당 최대 4성부, 트랙당 보표 1개(그랜드 스태프 없음), 빔/박 묶음은 표기 앱에 맡김.
- Claude는 소리를 들을 수 없어 수치로 믹싱합니다. 최종 판단은 직접 들어 보고 피드백해 주세요.

## 라이선스

코드: [GNU GPL v3](LICENSE) (`GPL-3.0-only`). 사용·수정·배포할 수 있으며, 수정본을 배포할 때는 수정했다는 사실과 날짜를 표시하고
소스 코드를 같은 GPL v3로 공개해야 합니다. 이 플러그인으로 만든 음악(WAV, MIDI, 악보 등)은 만든 사람의 것이며 GPL의 적용을 받지 않습니다.
악기: 내장 신스는 생성음(CC0), VSCO 2 CE는 CC0 1.0(Versilian Studios), 스캔한 사용자 라이브러리는 각자의 라이선스를 따릅니다.
`render --unity`는 사용한 악기 출처를 `CREDITS.md`로 남깁니다.
