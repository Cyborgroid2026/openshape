# OpenShape CAD

Ubuntu용 파라메트릭 3D CAD입니다. 화면 구성(응용프로그램 막대, 도구막대, 브라우저, 타임라인, ViewCube, 탐색 막대)과 사용 흐름은 Autodesk Fusion을 참고했고, 코드는 C#(Avalonia)과 Rust로 새로 작성했습니다.
Autodesk의 코드, 아이콘, 리소스는 포함되어 있지 않으며 Autodesk와 관련이 없습니다.

## 설치

[Releases](../../releases)에서 `openshape_<버전>_amd64.deb`를 받아 설치합니다.

```
sudo apt install ./openshape_0.1.0_amd64.deb
```

## 기능

- 스케치: XY·XZ·YZ 평면(간격띄우기 가능)에 선, 2점·중심 직사각형, 원, 3점 호, 다각형. 격자 스냅, 끝점 스냅, 곡선 수치 편집
- 작성: 돌출(한쪽/두 방향/대칭), 회전, 상자·원통·구·원환, 직사각형·원형 패턴, 대칭
- 작업: 새 바디, 접합, 절단, 교차 / 결합, 이동/복사, 축척, 제거, 모양(색)
- 파라메트릭 타임라인: 피쳐 편집 시 이후 피쳐 재계산, 롤백 마커, 재생
- 측정(부피·면적·크기), 메쉬 삽입(STL/OBJ), 내보내기(STL·OBJ·USD, mm·Z 위쪽 — Isaac Sim용)
- 디자인 파일 `.osc`(JSON)

지원하지 않는 기능: 모깎기·모따기·쉘, 구성요소·조인트, 곡면·메쉬·판금·플라스틱 작업공간, 치수·구속조건, Autodesk 클라우드(.f3d), STEP

## 구성

| 폴더 | 내용 |
|---|---|
| `engine/` | Rust 형상 엔진(cdylib, C ABI `osc_*`): BSP 불리언, 돌출/회전, 소프트웨어 렌더러, 파일 입출력 |
| `app/` | C# .NET 10 + Avalonia 11 UI |
| `uitest/` | Avalonia.Headless로 실제 창을 조작하는 UI 테스트 |
| `packaging/` | `build-deb.sh`: 엔진·UI 테스트를 통과하면 `dist/`에 .deb를 만듭니다 |

## 빌드

필요한 것: Rust 1.75 이상, .NET 10 SDK, `dpkg-deb`

```
sh packaging/build-deb.sh
```
