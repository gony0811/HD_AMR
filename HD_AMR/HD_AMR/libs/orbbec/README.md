# libOrbbecSDK 네이티브 라이브러리

이 디렉터리는 Orbbec Gemini 2 깊이 카메라용 네이티브 라이브러리(`libOrbbecSDK`)를 보관한다.
실제 바이너리는 `.gitignore` 에서 제외 — 첫 `dotnet build` 때 MSBuild 의 `RestoreOrbbecSdk*`
타깃이 GitHub Release 에서 자동으로 받아 `runtimes/<rid>/native/` 에 풀어 둔다.

## 자동 다운로드 동작

`HD_AMR/HD_AMR/HD_AMR.csproj` 에 다음 세 타깃이 정의돼 있다:

| 타깃 | 호스트 조건 | 받는 zip |
| --- | --- | --- |
| `RestoreOrbbecSdkMacArm64` | macOS arm64 (Apple Silicon) | `OrbbecSDK_C_C++_v1.10.16_..._macos_arm64_x86.zip` |
| `RestoreOrbbecSdkLinuxArm64` | Linux arm64 (Jetson/RPi64) | `OrbbecSDK_C_C++_v1.10.16_..._linux_arm64_release.zip` |
| `RestoreOrbbecSdkWinX64` | Windows x64 | `OrbbecSDK_C_C++_v1.10.16_..._win_x64_release.zip` |

각 타깃은 자기 RID 의 라이브러리가 이미 풀려 있으면 (`Exists()` 체크) 건너뛰므로 두 번째
빌드부터는 네트워크를 쓰지 않는다.

## 수동 설치 (오프라인 / 다운로드 실패 시)

1. 해당 OS 의 zip 을 [Orbbec OrbbecSDK v1.10.16 릴리스](https://github.com/orbbec/OrbbecSDK/releases/tag/v1.10.16) 에서 직접 받기.
2. 압축 풀고 `<root>/SDK/lib/` 아래의 파일을 다음 위치로 복사:
   - **macOS arm64**: `libs/orbbec/runtimes/osx-arm64/native/` 에 `libOrbbecSDK.1.10.16.dylib`,
     `liblive555.dylib`, `libob_usb.dylib`. 동일 디렉터리에 `libOrbbecSDK.dylib` 도 만들어 둘 것
     (실제 바이너리를 복사하거나 `ln -s libOrbbecSDK.1.10.16.dylib libOrbbecSDK.dylib`).
   - **Linux arm64**: `libs/orbbec/runtimes/linux-arm64/native/` 에 `libOrbbecSDK.so.1.10.16`,
     `liblive555.so`, `libob_usb.so`, `libdepthengine.so.2.0`. + `libOrbbecSDK.so`(복사 또는 심볼릭).
   - **Windows x64**: `libs/orbbec/runtimes/win-x64/native/` 에 `OrbbecSDK.dll`, `live555.dll`,
     `ob_usb.dll`, `depthengine_2_0.dll`.
3. 다시 `dotnet build` — csproj 의 `IncludeOrbbecNatives` 타깃이 자동으로 출력 디렉터리로 복사.

## OS 별 추가 절차

### macOS (arm64)

다운로드 직후 Gatekeeper 격리 속성 제거(없으면 dlopen 차단):
```sh
xattr -dr com.apple.quarantine HD_AMR/HD_AMR/libs/orbbec/runtimes/osx-arm64/native/
```

**중요**: macOS 의 시스템 카메라 도우미(`UVCAssistant`, `VDCAssistant`)가 Gemini 2 를 일반
웹캠으로 자동 점유한다 → OrbbecSDK 가 디바이스를 열 때 `uvc_open already opened` 로 실패.
실가동 테스트는 Linux/Windows 머신을 권장. macOS 에서 강제로 풀려면:
```sh
sudo killall VDCAssistant AppleCameraAssistant UVCAssistant
# 즉시 dotnet run — 시스템이 곧 다시 띄움
```
이건 macOS 의 UVC 카메라 다중 점유 한계라 우회 외 근본 해결책은 없음.

### Linux (arm64)

zip 안의 udev 규칙 파일을 시스템에 설치 — USB 디바이스 권한 부여:
```sh
sudo cp libs/orbbec/downloads/linux-arm64-extracted/OrbbecSDK_v1.10.16/Script/99-obsensor-libusb.rules \
    /etc/udev/rules.d/
sudo udevadm control --reload-rules && sudo udevadm trigger
```
이후 사용자가 `plugdev` 그룹에 속하거나 root 로 실행해야 한다.

### Windows (x64)

별도 절차 없음. `OrbbecSDK.dll` 과 의존성 DLL 이 같은 폴더에 있으면 자동 로드. MSVC 런타임
(`msvcp140.dll`, `vcruntime140.dll`) 은 Windows 에 이미 있는 경우가 일반적.

## 출처 / 라이선스

- 다운로드: https://github.com/orbbec/OrbbecSDK/releases/tag/v1.10.16
- 라이선스: Apache 2.0 — `LICENSE.txt` 가 각 zip 의 루트에 포함.
- v2.x 로 업그레이드 시 P/Invoke 시그니처(`HD_AMR/Communication/OrbbecGeminiClient.cs` 의
  `OrbbecNative` 클래스) 동기 수정 필요.
