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
