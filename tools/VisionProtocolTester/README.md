# VisionProtocolTester

HD_AMR ↔ 비전 검사 S/W **v3.2 프로토콜 검증기** (독립 콘솔 TCP 서버).

비전 S/W(TCP 서버, 장치 `0x02`) 역할을 흉내 낸다. `HD.AMR.Desktop` 의 **Vision Interface**
탭에서 우리 앱(자동화, TCP 클라이언트 `0x01`)이 접속해 **Send** 를 누르면, 이 프로그램이
프레임을 수신·파싱·검증하고 결과를 콘솔에 ✅/❌ 로 출력한 뒤 `CAPTURE_RES` 를 회신한다.

> **독립 파싱**: `HD.AMR.App` 을 참조하지 않고 사양(`docs/vision_interface_v3.2`) 기준으로
> 바이트를 직접 디코드한다. 우리 인코더로 만든 프레임을 우리 파서로 검사하는 순환 검증을
> 피해, "정말 사양대로 나갔는가" 를 독립적으로 검증한다.

## 실행

```bash
dotnet run --project tools/VisionProtocolTester
```

옵션:

| 옵션 | 기본 | 설명 |
|---|---|---|
| `--port <n>` | `5000` | LISTEN 포트 (VisionPanel 기본값과 일치) |
| `--result <code>` | `0x0000` | 회신할 `CAPTURE_RES` 결과 코드 (예: `0x0001` ERR_TIMEOUT) |
| `--bad-checksum` | off | 응답 CHECKSUM 을 고의로 틀리게 (우리 수신 파서 음성 테스트) |

## 사용 절차

1. 검증기 실행 (`--port 5000`).
2. Desktop 앱 → **Vision Interface** 탭 → `Host=127.0.0.1`, `Port=5000` → **Connect**.
3. 명령 `CAPTURE_REQ` 선택 → Wall ID·PosX/Y/Z·taskId·attempt·captureSeq 입력 → **Send**.
4. 검증기 콘솔에서 필드별 ✅/❌ 와 `PASS`/`FAIL` 확인. `CAPTURE_RES` 가 앱 로그로 회신된다.

## 검증 항목

- **프레임**: `STX=0x02`, `ETX=0x03`, `LENGTH == 3 + DATA`, `CHECKSUM == XOR(LENGTH~DATA)`
- **라우팅**: `FROM=0x01`(자동화) → `TO=0x02`(비전)
- **CAPTURE_REQ(34B)**: `surfaceType∈{0,1,2}`, `wallId∈1~10`, `u/v/h` Int32 LE,
  `taskId` GUID(RFC 4122 빅엔디안) 복원, `attempt≥1`, `captureSeq≥1`
- **HEARTBEAT(20B)**: 타임스탬프 14자리 ASCII + 예약 6B
- **재동기화**: 깨진 프레임 후 STX 재탐색 복구

## 출력 예시

```
◀ RX 43B  02 25 00 10 02 01 02 02 03 00 ... 3B 47 01 03 00 CC 03
✅ COMMAND    = 0x02 CAPTURE_REQ
✅ LENGTH     = 37  (DATA 34B)
✅ CHECKSUM   = 0xCC
✅ wallId     = 3 (좌현벽 PM)
✅ taskId     = 3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47
   ─────────  ✅ PASS
▶ TX CAPTURE_RES  Result=0x0000 SUCCESS
```

솔루션(`HD.AMR.sln`)에 포함되지 않은 독립 개발 도구다.
