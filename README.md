# OpenShape CAD

Ubuntu용 파라메트릭 3D CAD입니다. 화면 구성(응용프로그램 막대, 도구막대, 브라우저, 타임라인, ViewCube, 탐색 막대)과 사용 흐름은 Autodesk Fusion을 참고했고, 코드는 C#(Avalonia)과 Rust로 새로 작성했으며 형상 엔진은 OpenCASCADE, 스케치 구속조건 해석은 SolveSpace(libslvs)를 씁니다.
Autodesk의 코드, 아이콘, 리소스는 포함되어 있지 않으며 Autodesk와 관련이 없습니다.

## 설치

[Releases](../../releases)에서 `openshape_<버전>_amd64.deb`를 받아 설치합니다.

```
sudo apt install ./openshape_0.5.0_amd64.deb
```

## 기능

- 스케치: XY·XZ·YZ 평면(간격띄우기)과 본체의 평면 면 위에 선, 직사각형, 원, 3점 호, 다각형. 치수(거리·지름·각도)와 구속조건(일치·수평/수직·평행·직각·동일·고정·중간점·동심), 그리면서 자동 구속, 점 끌기
- 솔리드: 돌출, 회전, 로프트, 스윕, 상자·원통·구·원환, 패턴, 대칭, 모깎기, 모따기, 쉘, 누르기/당기기, 결합, 이동/복사, 축척
- 곡면: 곡면 돌출, 패치, 두껍게 / 판금: 플랜지, 모서리 플랜지, 판금 규칙 / 플라스틱: 리브, 보스
- 조립: 구성요소, 접합(강체·회전·슬라이더·원통형) 구동
- 파라메트릭 타임라인(편집·롤백·재생), 단면 분석, 측정
- 파일: `.osc` 디자인, STEP·IGES·STL·OBJ 열기/삽입, STEP·STL·OBJ·USD 내보내기, Isaac Sim용 물리·접합 USD

지원하지 않는 기능: 관리(매개변수), 스케치 자르기, 볼·핀-슬롯 접합, 접선·대칭 구속, 판금 펼치기, Fusion .f3d 직접 열기(Fusion 클라우드 동기화가 만드는 STEP으로 열 수 있음)

## 구성

| 폴더 | 내용 |
|---|---|
| `engine/` | Rust 엔진(cdylib, C ABI `osc_*`): 소프트웨어 렌더러(B-rep 모서리, 면 선택), 메쉬 파일 입출력 |
| `occt/` | C++ 연결 라이브러리(`ok_*`): OpenCASCADE B-rep 형상, STEP/IGES, SolveSpace 구속 해석 |
| `app/` | C# .NET 10 + Avalonia 11 UI |
| `uitest/` | Avalonia.Headless로 실제 창을 조작하는 UI 테스트 |
| `packaging/` | `build-deb.sh`: 엔진·UI 테스트를 통과하면 `dist/`에 .deb를 만듭니다 |

## 빌드

필요한 것: Rust 1.75 이상, .NET 10 SDK, g++, `dpkg-deb`. OpenCASCADE와 libslvs 개발 파일이 설치되어 있지 않으면 `packaging/fetch-occt.sh`가 Ubuntu 패키지를 받아 씁니다(관리자 권한 불필요).

```
sh packaging/build-deb.sh
```
