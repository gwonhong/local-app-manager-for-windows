# 결정 기록

결정을 내릴 때마다 여기에 남긴다. 형식: 결정 / 이유 / 대안.

## D-000 이미 정해진 전제 (재논의 안 함)
- UI는 WinUI 3 (Windows App SDK) + C#, Windows 전용.
- 패턴은 MVVM (CommunityToolkit.Mvvm). 백엔드 서비스(`LocalAppManager.Core`)가 상태의 유일한 주인이고 UI는 표시만 한다.
- 기존 오픈소스(LocalDock, DevDock) 대신 직접 만든다.

## D-001 배포 방식: Unpackaged + Windows App SDK self-contained (2026-11-14)
- **결정**: MSIX 없이 일반 exe(`WindowsPackageType=None`)로 빌드하고 Windows App SDK 런타임을 exe 옆에 포함한다(`WindowsAppSDKSelfContained=true`).
- **이유**
  - 개인용이라 MSIX의 이점(스토어, 자동 업데이트, 깨끗한 제거)이 필요 없다.
  - 인증서 서명이나 빌드마다 재설치하는 과정 없이 `dotnet build` 후 exe를 바로 실행할 수 있다.
  - Windows App SDK 런타임을 따로 설치하거나 버전을 맞출 필요가 없다.
  - 트레이(H.NotifyIcon), Job Object, 토스트(`AppNotificationManager`)는 unpackaged에서도 동작한다.
- **영향**
  - 로그인 시 자동 실행은 `StartupTask`가 아니라 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 레지스트리 값으로 구현한다.
  - 패키지 ID가 필요한 API(패키지 전용 StartupTask, 패키지 단위 파일 시스템 가상화 등)는 쓰지 않는다.
- **.NET 런타임**: 개발 빌드는 설치된 .NET 10 런타임을 쓴다(framework-dependent). publish 프로필(`Properties/PublishProfiles`)은 .NET도 포함하는 self-contained 빌드를 만든다. .NET 업데이트와 무관하게 동작하게 하기 위해서다.
- **대안**
  - Unpackaged + framework-dependent Windows App SDK: 출력이 작지만 런타임 설치가 필요하다.
  - MSIX 패키지: 서명과 재설치 비용 때문에 배제했다.

## D-002 환경 버전 (2026-11-14 NuGet에서 직접 확인)
- .NET SDK 10.0.404, TFM `net10.0-windows10.0.26100.0`, 최소 지원 OS 10.0.19041.
- Microsoft.WindowsAppSDK 2.6.5, Microsoft.Windows.SDK.BuildTools 10.0.28000.2957.
- CommunityToolkit.Mvvm 8.4.2, H.NotifyIcon.WinUI 2.4.1.
- 템플릿: `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`(`dotnet new winui --packaging Unpackaged`).
- Visual Studio 없이 `dotnet` CLI만으로 빌드한다.
- 템플릿 기본값과 다르게 Release publish의 트리밍(`PublishTrimmed`)은 끈다. XAML 바인딩과 MVVM 툴킷이 리플렉션을 쓰기 때문이다.

## D-003 프로세스 관리: 실행마다 Job Object 하나, 직접 CreateProcess (2026-11-14)
- **결정**: `System.Diagnostics.Process` 대신 `CreateProcessW`를 직접 호출한다(`Core/Processes/ChildProcess.cs`).
  - `PROC_THREAD_ATTRIBUTE_JOB_LIST`로 프로세스를 **생성되는 순간부터** 전용 Job 안에 넣는다.
    Start 후에 `AssignProcessToJobObject`를 하면 그 사이에 `cmd`가 손자를 띄워 Job 밖으로 빠질 틈이 생긴다.
  - Job에는 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`를 건다. 앱이 어떻게 죽든(강제 종료, FailFast) 커널이 핸들을 닫으면서 트리 전체를 죽인다.
  - `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`로 자식에게 넘길 핸들을 파이프 끝 두 개로 제한한다. 다른 핸들이 새지 않게 하기 위해서다.
  - stdout과 stderr는 **같은 파이프**를 쓴다. 두 출력의 순서가 섞이지 않는다.
  - 명령은 `cmd.exe /d /s /c "<명령>"`으로 실행한다. `/d`는 AutoRun을 무시하고, `/s`는 따옴표를 그대로 보존한다.
  - 루트(cmd)가 끝나면 Job을 Terminate한다. 루트가 남겨 둔 백그라운드 손자까지 정리된다.
  - 환경 변수는 실행할 때마다 레지스트리(HKLM/HKCU Environment)에서 PATH 등을 다시 읽어 만든다.
    앱이 트레이에 오래 떠 있어도 새로 설치한 Node나 Python이 보인다.
- **검증**
  - 단위 테스트: cmd → node → node 트리가 Kill, 루트 종료, Job 핸들 닫기 세 경우 모두 정리된다.
  - maple-timer(pnpm → cmd → vite.cmd → node)에서 정지와 앱 강제 종료(`Stop-Process -Force`, FailFast) 뒤에 남는 프로세스가 0개다.

## D-004 상태 정의와 자동 재시작 (2026-11-14)
- 상태 정의
  - Stopped: 사용자가 정지했다.
  - Starting: 실행했지만 아직 준비되지 않았다.
  - Running: 배정한 포트가 리슨 중이거나, 로그에 로컬 URL이 찍혔거나, 포트가 없는 프로젝트가 2초 넘게 살아 있다.
  - Exited: 스스로 종료했고 종료 코드가 0이다.
  - Crashed: 스스로 종료했고 종료 코드가 0이 아니다.
- 포트가 있는데 20초가 지나도 열리지 않으면, 경고를 남기고 Running으로 넘긴다.
  서버가 아닌 watcher나 worker를 영원히 "시작 중"으로 두지 않기 위해서다.
- 자동 재시작은 프로젝트별 옵션이고 Crashed일 때만 동작한다.
  - 간격은 1, 2, 4, 8 … 초로 늘리되 60초에서 멈춘다.
  - 직전 실행이 1분 넘게 살아 있었으면 간격을 처음(1초)부터 다시 센다.
  - 정지를 누르면 예약된 재시작을 취소한다.
- `cmd`가 "'npm'은(는) 내부 또는 외부 명령…"을 출력하고 크래시하면, PATH 문제라는 힌트를 오류 표시줄에 띄운다.
  이 PC에는 npm이 없고 pnpm만 있어서 실제로 겪는 상황이다.

## D-005 포트 배정 (2026-11-14)
- 범위는 기본 4100–4999이고 설정에서 바꿀 수 있다. 프로젝트를 등록할 때 비어 있는 가장 낮은 포트를 고정으로 배정하고 `projects.json`에 저장한다.
- "비어 있음"의 판정:
  1. TCP 리스너 테이블에 그 포트가 없다(IPv4와 IPv6, 모든 주소).
  2. `127.0.0.1`, `0.0.0.0`, `::1`에 직접 바인딩할 수 있다(`TcpListener`, ExclusiveAddressUse). 바인딩 시도는 Hyper-V/WSL의 excluded port range도 걸러 준다.
- 실행 직전에 다시 확인한다. 포트가 사용 중이면 **자동으로 바꾸지 않는다.**
  - 오류 표시줄에 점유 중인 프로세스(이름과 PID, `GetExtendedTcpTable`로 조회)를 보여 주고, "다른 포트로 시작" 버튼을 제공한다.
  - 이유: 북마크나 프록시 설정에 박힌 URL이 몰래 바뀌는 게 더 나쁘다.
- 포트 전달 방식(`PortMode`):
  - Vite와 SvelteKit은 `--port N --strictPort`를 붙인다. Vite는 PORT를 무시하고, `--strictPort`가 없으면 조용히 다음 포트로 옮겨 가기 때문이다.
  - Astro와 Angular는 `--port N`을 붙인다.
  - Next, Nuxt, CRA(+`BROWSER=none`), Remix, Nest, Express류는 `PORT` 환경 변수를 쓴다.
  - Django, FastAPI, Flask, Streamlit은 감지할 때 만든 명령에 `{port}` 자리표시자를 넣는다.
  - 명령에 `{port}`가 있으면 방식과 상관없이 그 자리에 포트를 넣는다.
- `npm run x`는 인자 앞에 `--`가 있어야 스크립트까지 전달된다.
  pnpm, yarn, bun은 `--`를 그대로 넘기기 때문에, 붙이면 오히려 일부 CLI가 옵션 해석을 멈춘다. 그래서 npm일 때만 `--`를 붙인다.
- 보조 정보:
  - stdout에서 로컬 URL을 정규식으로 찾는다. 0.0.0.0과 [::]는 localhost로 바꿔 표시한다. LAN 주소보다 로컬 주소를 우선한다.
  - Job에 속한 PID들이 실제로 리슨 중인 포트를 3초마다 TCP 테이블에서 읽어 "리슨 :4100"으로 보여 준다.
  - 배정 포트와 실제 포트가 다르면 경고한다.

## D-005a 포트는 관찰이 기본, 고정은 선택 (2026-11-17)
- D-005의 "등록할 때 포트를 배정"을 바꾼다. 새 프로젝트는 포트를 고정하지 않고(`Port = null`), 앱은 아무 포트도 넘기지 않는다.
  - 이유: 프레임워크 추측이 맞을 때만 포트 전달이 통했다. `app.listen(3000)`을 박아 둔 서버, 인자를 넘기지 않는 래퍼 스크립트, 감지 못 한 폴더에서는 20초 대기와 "배정 ≠ 실제" 경고만 남았다.
  - 반면 Job에 속한 PID의 리슨 포트를 읽는 관찰은 어떤 프로젝트에서든 정확하다.
- 화면에 보이는 포트(`ProjectRuntime.LivePort`): 로그 URL의 포트가 리슨 중이면 그것, 아니면 고정 포트, 아니면 가장 낮은 리슨 포트.
  - 실행 중: 초록 배지. 리슨 포트가 여러 개면 `+N`을 붙이고 툴팁에 모두 보여 준다.
  - 정지: 흐린 숫자. 고정했으면 고정 포트와 핀 아이콘(다음 시작이 쓰는 포트이므로 예전에 본 포트보다 우선), 아니면 마지막으로 본 포트(`ProjectDefinition.LastPort`, `projects.json`에 저장)와 기록 아이콘(E81C).
- 포트 고정은 설정 대화 상자가 아니라 프로젝트 헤더 오른쪽 위의 핀 버튼(플라이아웃)에서 한다. 자주 만지는 값이라 한 번에 닿아야 한다.
  - 버튼은 알려진 프레임워크(`FrameworkInfo.CanPinPort`, 즉 `PortMode != None`)나 이미 고정된 프로젝트에만 보인다.
  - 제안 포트(`ProjectManager.SuggestPinPort`): 이미 고정한 포트, 아니면 마지막으로 본 포트(다른 프로젝트가 고정하지 않았고 비어 있을 때), 아니면 범위 안의 빈 포트.
  - 다른 프로젝트가 고정한 포트는 거절한다(`PinPort`가 그 프로젝트를 돌려줌). 다른 프로그램이 쓰는 중이면 경고만 한다.
  - 설정 대화 상자에는 "포트 전달 방식"(환경 변수/인자/없음)만 남는다.
  - Django/FastAPI/Flask/Streamlit은 감지 명령에 `{port}`를 넣지 않고, `PortMode.Args`로 인자를 붙인다(`runserver N`, `--port N`, `--server.port N`). 그래서 고정하지 않으면 각 서버의 기본 포트로 뜬다.
- 준비 감지: 고정 포트가 있으면 그 포트, 없더라도 알려진 서버면 리슨 포트나 로그 URL이 나올 때까지 기다린다(최대 20초). 그 밖의 명령은 2초 살아 있으면 실행 중.
- 기존 `projects.json`의 고정 포트는 그대로 둔다. 설정에서 고정을 끄면 된다.

## D-006 로그: 파이프 + FORCE_COLOR, ConPTY는 보류 (2026-11-14)
- **결정**: MVP 방식(파이프)을 유지한다. ConPTY는 실행 계층만 준비해 두고(`ChildProcessOptions.UsePseudoConsole`) UI에는 연결하지 않는다.
- **ConPTY 검토 결과 (보류 이유)**
  - ConPTY 출력은 "줄"이 아니라 "터미널 화면" 스트림이다. 커서 이동, 화면 지우기, 고정 열 수에서의 줄바꿈이 섞여 있어서 VT 에뮬레이터 없이는 줄 목록으로 그릴 수 없다.
  - 자식이 TTY를 감지하면 인터랙티브 모드를 켠다(Vite 단축키 안내, 스피너, 진행률 다시 그리기). 줄 목록 UI에서는 오히려 지저분해진다.
  - 파이프에 `FORCE_COLOR=1`만 넣어도 지금 쓰는 도구들(Vite, pnpm, tsc, Node)은 색을 유지한다.
  - 나중에 "터미널 탭"이 필요해지면 ConPTY와 WebView2 + xterm.js(또는 VT 파서)를 프로젝트별 선택 옵션으로 붙인다.
- **디코딩**
  - 줄마다 UTF-8로 엄격하게 디코딩하고, 실패하면 콘솔 OEM 코드페이지(CP949)로 다시 디코딩한다. cmd 자체의 한글 오류 메시지가 깨지지 않는다.
  - 단독 `\r`(진행률 표시)은 그 줄을 덮어쓰는 것으로 처리한다.
  - 줄바꿈 없는 프롬프트("(y/N)")는 250ms 뒤에 내보낸다.
- **보관과 표시**
  - 프로젝트마다 5000줄 링버퍼를 두고, 줄마다 순번(seq)을 매긴다.
  - UI는 이벤트를 받지 않고 **50ms마다 "N번 이후"를 폴링한다.** 출력이 폭주해도 UI 갱신은 50ms당 한 번이다.
  - 화면은 ItemsRepeater(가상화)에 그린다. ANSI 스팬은 Run으로, 배경색과 검색 하이라이트는 TextHighlighter로 그린다(WinUI의 Run에는 배경이 없다).
  - 테마별 팔레트가 있고, 대비가 부족한 색은 자동으로 보정한다.
- **검증**: 20만 줄을 약 1초에 쏟아내도 UIA 응답 35ms를 유지했고, 메모리는 24MB만 늘었다.

## D-007 정지: Ctrl+C 먼저, 3초 뒤 강제 종료 (2026-11-14)
- 정지는 자식의 (숨은) 콘솔에 Ctrl+C를 보내고 최대 3초 기다린 뒤, 남아 있으면 Job을 Terminate한다. 설정으로 끌 수 있다.
- **Ctrl+C는 별도 도우미 프로세스가 보낸다.** 앱이 자기 exe를 `--send-ctrl-c <pid>`로 실행하고, 도우미가 AttachConsole → GenerateConsoleCtrlEvent 후 종료한다.
  - 처음에는 앱 안에서 직접 했다. 그런데 이벤트가 앱 자신에게도 비동기로 전달됐고, 기본 핸들러가 앱을 `0xC000013A`로 조용히 종료시켰다(그룹으로 두 프로젝트를 동시에 정지할 때 재현).
  - 커스텀 핸들러를 달아도 FreeConsole 이후에는 확실하지 않았다.
  - `SetConsoleCtrlHandler(NULL, TRUE)`는 자식에게 상속되므로 쓸 수 없다.
- npm.cmd나 `node_modules\.bin\*.cmd` 같은 배치 shim이 중단되면 cmd가 "일괄 작업을 끝내시겠습니까 (Y/N)?"를 shim마다 한 번씩 묻는다. 정지 중에는 이 프롬프트를 감지해 Y로 답한다.
- 앱 종료(트레이 → 종료)도 전체를 정상 정지(최대 4초)한 뒤 끝낸다. 이 과정이 실패해도 Job이 남은 것을 정리한다.
- **검증**: maple-timer가 약 80ms에 정상 종료한다. 그룹 시작/정지 5회 반복에서 강제 종료는 0회, 남은 프로세스도 0개였다.

## D-008 창, 트레이, 알림, 자동 실행 (2026-11-14)
- 단일 인스턴스: 커스텀 `Main`(`DISABLE_XAML_GENERATED_MAIN`)과 `AppInstance.FindOrRegisterForKey`를 쓴다. 두 번째 실행(시작 메뉴, `--add <폴더>`, 토스트 클릭)은 떠 있는 인스턴스로 넘긴다.
- 트레이: H.NotifyIcon.WinUI, `ContextMenuMode.PopupMenu`(Win32 네이티브 메뉴)를 쓴다.
  - 프로젝트마다 체크 표시(실행 중)가 있는 토글 항목을 둔다.
  - 그룹 시작/정지, 모두 정지, 종료 항목이 있다.
  - 왼쪽 클릭하면 창을 연다.
- 닫기 버튼은 트레이로 숨긴다(설정으로 끌 수 있음). 처음 숨길 때 한 번만 안내 토스트를 띄운다.
- 자동 실행: `HKCU\…\Run`에 `"<exe>" --background`를 쓴다. 작업 관리자 시작 프로그램 탭에서 끈 상태(StartupApproved)도 존중한다. exe 경로가 바뀌면 설정 화면에서 알려 준다.
- 토스트: `AppNotificationManager`를 쓴다(unpackaged). 크래시 알림에는 "다시 시작"과 "로그 보기" 버튼이 있다.
- 창 크기와 위치는 `GetWindowPlacement`로 저장한다. 최대화 상태에서도 복원 크기를 기억하고, 사라진 모니터 위치는 무시한다.
- 테마는 기본으로 Windows를 따른다. 설정에서 밝게/어둡게로 고정할 수 있다(라이트 팔레트를 확인할 때도 쓴다).

## D-009 명령과 스크립트 (2026-11-14)
- **프로젝트는 한 번에 하나만 실행한다 (2026-11-16).** 시작 명령이든 다른 명령이든, 무엇을 실행하면 돌고 있던 것을 먼저 정지한다.
  - 이유: "시작 버튼"과 "칩"이 따로 있고 여러 개가 동시에 도는 구조는, 지금 무엇이 돌고 있고 어느 버튼이 무엇을 멈추는지 알기 어려웠다.
  - 잃는 것: dev 서버를 켠 채로 test나 build를 함께 돌릴 수 없다. 필요해지면 명령별 "함께 실행" 옵션으로 되살린다.
  - 구현: `ProjectRuntime.SwitchAsync`가 전부 정지한 뒤 새 것을 시작한다. 전환 도중에 사용자가 정지를 누르면 정지가 이기고, 새 명령은 시작하지 않는다.
- 상세 화면에는 시작/재시작 버튼이 없다. 명령 칩이 유일한 실행 수단이다.
  - 칩을 누르면 그 명령을 실행한다. 다른 칩을 누르면 그쪽으로 바꾼다. 실행 중인 칩(초록 테두리, ↻ 아이콘)을 다시 누르면 재시작한다.
  - 정지는 실행 표시줄 오른쪽의 정지 버튼 하나다. 표시줄 왼쪽에는 상태 점, 실행 중인 명령 이름, 상태, 경과 시간이 있고, 가운데에 포트/주소/리슨이 있다.
    제목 옆 상태 배지는 표시줄과 중복이라 없앴다.
  - package.json의 scripts와 사용자 정의 명령(설정 화면에서 `이름: 명령`, `{port}` 사용 가능)이 칩이 된다. 우클릭 메뉴: 실행, 재시작, 정지, 숨기기.
- 시작 명령(프로젝트 설정의 "시작 명령")은 기본 명령으로 남는다. 칩에는 ★가 붙는다.
  - 목록의 ▶ 버튼, 트레이, 그룹, 앱과 함께 시작, Ctrl+Enter가 이 명령을 실행한다.
  - 배정 포트 전달, 준비 감지(시작 중 → 실행 중), 크래시 알림, 자동 재시작은 이 명령에만 적용된다. 다른 명령은 `{port}` 치환만 받는다.
  - 시작 명령이 스크립트가 아니면(예: `python manage.py runserver`) 그 명령 자체가 맨 앞 칩으로 나온다.
  - 칩 우클릭의 "시작 명령으로 사용"은 없앴다. 기본 명령은 프로젝트 설정에서 바꾼다.
- 모든 출력은 같은 로그에 들어가고, 시작 명령이 아닌 명령의 줄에는 `[이름]` 접두사가 붙는다.

## D-010 UI 구성 (2026-11-14)
- UI 문구는 한국어다.
- 화면 구성: 왼쪽은 프로젝트 목록(상태 점, 바로 시작/정지, 드래그로 순서 변경)과 그룹, 오른쪽은 상세 화면이다.
- 상세 화면 내용(위에서 아래로): 제목과 열기 버튼, 명령 칩, 실행 표시줄(상태·이름·경과 시간·포트/주소/리슨·정지), 오류 표시줄, 로그.
  - 제목 오른쪽 위: 포트 고정, VS Code, 탐색기, 터미널, 프로젝트 설정. "⋯ 더 보기" 메뉴와 브라우저(지구본) 버튼은 없앴다 (2026-11-17).
    자동 재시작과 목록에서 제거는 프로젝트 설정에 있다. 브라우저는 실행 표시줄의 주소 링크, 로그의 URL, 목록 우클릭으로 연다.
  - ContentDialog는 한 번에 하나만 열 수 있어서, "목록에서 제거…"는 설정 창을 닫은 뒤 확인 창을 띄운다.
  - 프로젝트별 "앱이 시작될 때 실행"(StartWithApp) 옵션은 없앴다 (2026-11-17, 쓰지 않는 옵션이라 정리). 예전 파일의 값은 무시된다.
- 대화상자(프로젝트 설정, 앱 설정, 그룹)는 코드로 만든 ContentDialog다. 프로젝트 설정에서는 실제로 실행될 명령과 환경 변수를 미리 보여 준다.
- `x:Bind` 함수는 인자가 null이면 호출되지 않는다. 그래서 표시 여부는 모두 ViewModel의 bool 속성에 바인딩한다(`HasUrl` 등).
- 한국어 UI 글꼴에서는 `\`가 `₩`로 그려진다. 그래서 경로에는 Latin 글꼴(`PathFontFamily`)이나 Cascadia Mono를 쓴다.
- 단축키:
  - Ctrl+N: 추가
  - Ctrl+F: 로그 검색
  - Ctrl+Enter: 기본 명령 시작 / 실행 중인 것 정지
  - Ctrl+R: 실행 중인 것 재시작
  - Ctrl+L: 로그 지우기
- 폴더를 창에 끌어다 놓아도 등록된다.

## D-011 검증 방법 (2026-11-14)
- `tests/LocalAppManager.Core.Tests`: 파서, 감지, 명령 빌더, 포트, 상태 머신, 실제 프로세스 트리 정리를 xunit으로 테스트한다.
- `tools/LocalAppManager.Probe`: UI 없이 Core로 실제 프로젝트를 실행/정지하고, 남은 프로세스와 FailFast 후 고아 프로세스를 검사한다.
- `tools/WindowShot`: Windows.Graphics.Capture로 창을 캡처한다. GDI 캡처는 WinUI 내용을 검게 찍는다.
- `tools/e2e`: UI Automation 스크립트다. 다시 빌드 → 실행(임시 데이터 폴더) → 버튼 클릭 → 상태 읽기.
- 데이터 폴더는 `LOCALAPPMANAGER_DATA` 환경 변수로 바꿀 수 있다. 테스트는 실제 `%LOCALAPPDATA%\LocalAppManager`를 건드리지 않는다.

## 알려진 한계 / 다음 후보
- 실행 중인 서버가 `open: true`로 새 브라우저를 직접 띄우면, 그 브라우저도 Job에 속해서 정지할 때 같이 닫힌다(이미 떠 있는 브라우저에 탭이 열리는 경우는 괜찮다). CRA는 `BROWSER=none`으로 막아 두었다.
- 로그는 줄 단위로만 선택해 복사할 수 있다(TextBlock 단위 선택). 보이는 줄 전체를 복사하는 버튼은 있다.
- 앱 이름은 임시(`LocalAppManager`)다. 바꿀 곳은 `AppPaths.AppName`, csproj의 `AssemblyName`과 `RootNamespace`, XAML 제목이다.
- ConPTY 기반 터미널 보기(D-006)를 붙일 수 있다.
