# Local App Manager For Windows

로컬 개발 서버(Vite, Next, Node, Python 등)를 즐겨찾기처럼 등록해 두고, 버튼으로 실행 / 정지 / 재시작하는 Windows 데스크톱 앱입니다.
스크립트와 빌드도 버튼으로 돌립니다. 실제로 열린 포트를 보여 주고(원하면 포트를 고정), 프로세스 트리를 확실하게 정리합니다.

WinUI 3 (Windows App SDK 2.6) + C# / .NET 10. Windows 10 2004 이상, 개인용입니다.
설계 결정과 그 이유는 [docs/DECISIONS.md](docs/DECISIONS.md)에 있습니다.

## 기능

- **등록**: 폴더 선택, 창에 폴더 끌어다 놓기, `LocalAppManager.exe --add <폴더>` 중 하나로 등록합니다.
  - 프레임워크를 자동으로 감지합니다: Vite, SvelteKit, Next, Nuxt, Astro, Angular, CRA, Remix, Nest, Express류, Django, FastAPI, Flask, Streamlit.
  - 패키지 매니저(npm/pnpm/yarn/bun)와 시작 스크립트도 알아냅니다.
- **포트**:
  - 기본은 관찰만 합니다. 앱은 포트를 넘기지 않고, 프로세스 트리가 실제로 리슨하는 포트와 로그에 찍힌 URL을 보여 줍니다.
  - 목록에는 실행 중이면 지금 리슨 중인 포트를 초록 배지로, 정지 상태면 흐린 숫자로 보여 줍니다. 고정했으면 고정 포트(핀 아이콘), 아니면 마지막으로 쓴 포트(기록 아이콘)입니다.
  - 알려진 프레임워크(Vite, Next, Django 등)는 프로젝트 화면 오른쪽 위의 **📌 포트 고정** 버튼으로 포트를 고정할 수 있습니다. 마지막으로 쓴 포트가 비어 있으면 그것을, 아니면 4100–4999 범위의 빈 포트를 제안합니다. 고정한 포트는 Vite면 `--port N --strictPort`로, Next/CRA/Express류면 `PORT` 환경 변수로, Django/FastAPI/Flask/Streamlit은 인자로 넘깁니다(전달 방식은 프로젝트 설정에서 바꿀 수 있습니다). 명령 안의 `{port}`도 고정 포트로 바뀝니다.
  - 고정 포트는 실행 직전에 비었는지 확인합니다. 사용 중이면 점유 중인 프로세스를 보여 주고, "다른 포트로 시작" 버튼을 띄웁니다.
- **프로세스**:
  - 실행마다 Job Object(`KILL_ON_JOB_CLOSE`)를 하나씩 쓰고, 프로세스를 생성하는 순간부터 그 Job 안에 넣습니다.
  - 정지할 때는 Ctrl+C를 먼저 보내고(npm/pnpm `.cmd` shim의 "일괄 작업을 끝내시겠습니까?"에도 자동으로 답합니다), 3초 안에 끝나지 않으면 트리 전체를 종료합니다.
  - 앱이 비정상 종료해도 고아 프로세스가 남지 않습니다.
- **상태**: 정지됨 / 시작 중 / 실행 중 / 종료됨 / 크래시(코드). 크래시 때 자동 재시작은 선택이며, 간격은 1초부터 두 배씩 늘어 최대 60초입니다.
- **로그**:
  - 프로젝트마다 실시간 로그를 5000줄까지 보관합니다. ANSI 색상을 표시하고, 밝은/어두운 테마별 팔레트를 씁니다.
  - 검색(하이라이트 포함), 복사, 지우기, 줄바꿈, 시간 표시, 맨 아래 따라가기를 지원합니다.
  - 로그에 찍힌 URL은 밑줄 친 링크가 되고, 클릭하면 브라우저로 엽니다(0.0.0.0, [::]는 localhost로).
  - 실행 중인 프로세스에 입력을 보낼 수 있습니다.
  - cmd의 한글 메시지(CP949)도 깨지지 않습니다.
- **명령**: package.json 스크립트와 사용자 정의 명령(`{port}` 사용 가능)을 칩으로 보여 줍니다.
  - 칩을 누르면 실행하고, 다른 칩을 누르면 그쪽으로 바꾸고, 실행 중인 칩을 다시 누르면 재시작합니다. 프로젝트마다 한 번에 하나만 실행합니다.
  - ★ 칩이 기본 명령입니다(목록의 ▶, 트레이, 그룹이 실행). 칩 우클릭 "기본 명령으로"나 명령 관리 창의 ☆로 바꿉니다.
  - 명령 관리 창(칩 줄 오른쪽 버튼)에서 끌어서 순서를 바꾸고, 기본 명령을 고르고, 명령을 추가·수정·삭제하고, 뺀 스크립트를 다시 넣습니다. package.json에 새로 생긴 스크립트는 저절로 뒤에 붙습니다.
  - 서버를 띄우는 명령(vite, next dev, runserver 등)은 고정 포트를 받고, 포트가 열려야 "실행 중"이 되고, 크래시하면 알림과 자동 재시작을 받습니다. build나 lint 같은 명령은 "완료"/"실패"로만 끝납니다.
  - 실행 표시줄에 무엇이 얼마나 돌고 있는지와 정지 버튼이 있습니다.
- **그룹**: 여러 프로젝트를 묶어 한 번에 시작/정지합니다.
- **열기**: 브라우저(실행 표시줄의 주소 링크, 로그의 URL, 목록 우클릭 메뉴), VS Code, 탐색기, 터미널(Windows Terminal, PowerShell, cmd).
- **시스템 연동**:
  - 트레이에 상주합니다. 트레이 메뉴에서 프로젝트별 시작/정지, 그룹, 모두 정지를 할 수 있고, 닫기 버튼은 트레이로 숨깁니다.
  - 로그인 시 자동 실행(선택)과 크래시 토스트 알림이 있습니다.
  - 단일 인스턴스로 동작합니다.
- **화면**: Mica 배경, 테마는 시스템 설정을 따릅니다(설정에서 고정 가능). 창 크기와 위치를 기억합니다.

단축키: `Ctrl+N` 추가 · `Ctrl+F` 로그 검색 · `Ctrl+Enter` 시작/정지 · `Ctrl+R` 재시작 · `Ctrl+L` 로그 지우기

## 빌드와 실행

필요한 것은 .NET SDK 10입니다. Visual Studio는 없어도 됩니다.

```powershell
dotnet build src/LocalAppManager/LocalAppManager.csproj
./src/LocalAppManager/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/LocalAppManager.exe
```

빌드가 꼬였을 때(XAML 로드 오류 등)는 앱을 종료한 뒤 `bin`과 `obj`를 지우고 다시 빌드합니다.

```powershell
Get-ChildItem src, tests, tools -Recurse -Directory -Include bin, obj | Remove-Item -Recurse -Force
dotnet build src/LocalAppManager/LocalAppManager.csproj
```

배포용 빌드는 .NET과 Windows App SDK를 모두 포함하는 self-contained 빌드입니다. 설치할 필요 없이 폴더째 복사해서 실행합니다.

```powershell
dotnet publish src/LocalAppManager/LocalAppManager.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
# → src/LocalAppManager/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/LocalAppManager.exe
```

테스트:

```powershell
dotnet test tests/LocalAppManager.Core.Tests/LocalAppManager.Core.Tests.csproj
```

### 릴리스

`v`로 시작하는 버전 태그를 push하면 GitHub Actions(`.github/workflows/release.yml`)가 테스트를 돌리고 x64와 ARM64 빌드를 zip으로 묶어 GitHub Releases에 올립니다. 일반 커밋 push로는 아무것도 실행되지 않습니다.

```powershell
git tag v0.2.0
git push origin v0.2.0
```

버전은 태그에서 정해집니다(`v0.2.0` → 0.2.0). `v0.3.0-beta.1`처럼 `-`가 붙은 태그는 pre-release로 올라갑니다. 릴리스 노트는 이전 태그 이후의 커밋과 PR로 자동 생성됩니다.

## 명령줄

| 인자 | 동작 |
|---|---|
| `--add <폴더>` (여러 번 가능) 또는 `<폴더>` | 폴더를 프로젝트로 등록합니다. 앱이 이미 떠 있으면 그 창으로 전달합니다. |
| `--background` | 창 없이 트레이로 시작합니다(로그인 자동 실행이 사용). |

## 데이터

`%LOCALAPPDATA%\LocalAppManager\`

- `projects.json`: 프로젝트와 그룹(이름, 경로, 명령, 고정 포트, 마지막으로 쓴 포트, 환경 변수 등)
- `settings.json`: 앱 설정, 창 위치
- `logs\app.log`: 앱 자체의 진단 로그(프로젝트 출력은 저장하지 않습니다)

`LOCALAPPMANAGER_DATA` 환경 변수로 다른 폴더를 쓰게 할 수 있습니다(테스트용).

## 구조

```
src/LocalAppManager.Core   UI 없는 백엔드 — 상태의 유일한 주인
  Processes/               Job Object, CreateProcess, 환경 변수
  Logs/                    줄 디코더(UTF-8/CP949), ANSI 파서, 링버퍼
  Ports/                   포트 확인/점유자 조회/배정, URL 감지
  Projects/                정의, 프레임워크 감지, 명령 빌더
  Runtime/                 프로젝트 상태 머신, ProjectManager
  Storage/, Diagnostics/   JSON 저장, 앱 로그
src/LocalAppManager        WinUI 앱 (MVVM, CommunityToolkit.Mvvm) — 표시만 담당
tests/                     xunit
tools/LocalAppManager.Probe  UI 없이 실제 프로젝트를 실행해 트리 정리를 검사
tools/WindowShot           Windows.Graphics.Capture 창 캡처
tools/e2e                  UI Automation 검증 스크립트
tools/icon                 아이콘 생성 스크립트
```

## 개발 메모

- 샌드박스 셸에서 띄운 WinUI 앱은 검은 화면으로 그려집니다. UI를 확인할 때는 일반 셸에서 실행하세요.
- GDI 캡처(PrintWindow 등)도 WinUI 내용을 검게 찍습니다. 대신 `tools/WindowShot`을 쓰세요.
- 앱 이름을 바꾸려면 `AppPaths.AppName`, csproj의 `AssemblyName`/`RootNamespace`, `MainWindow.xaml`의 제목을 고치세요.
