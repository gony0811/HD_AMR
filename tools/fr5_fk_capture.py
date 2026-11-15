# -*- coding: utf-8 -*-
"""FR5-WML 실물 순기구학 표본 수집 — d6(106 / 160)·관절 0점·부호 대조용.

컨트롤러에 **조회만** 한다(Get* 호출뿐). 로봇은 움직이지 않고, 공구·작업물 좌표계도 바꾸지 않는다.
GetForwardKin 은 "이 관절각이면 TCP 가 어디인가"를 계산해 줄 뿐이다.

사용(코봇 컨트롤러와 같은 망의 PC, 파이썬 3 표준 라이브러리만 사용):

    py -I tools/fr5_fk_capture.py                     # 기본 192.168.58.2
    py -I tools/fr5_fk_capture.py --host 192.168.58.2 --out fr5_fk_samples.json

만들어진 파일을 아래 두 곳에 그대로 덮어쓰고 테스트를 돌린다.

    HD_AMR  src/HD.AMR.Tests/Data/fr5_fk_samples.json       dotnet test --filter Fr5FkCheck
    HD_ACS  src/HD.Acs.Core.Tests/Data/fr5_fk_samples.json  dotnet test --filter Fr5FkCheck

테스트 출력에 d6 후보(106 / 160)별 오차와 표본에서 구한 d6, 관절 0점·부호 진단이 나온다.

주의
- 이 펌웨어의 GetForwardKin 은 '현재 활성 작업물 좌표계' 기준 '현재 활성 공구' TCP 를 준다. 그래서 활성 공구·작업물 번호와
  그 좌표값을 함께 적어 두고, 대조 코드가 BASE 기준 플랜지로 되돌린다. 번호를 읽지 못하는 펌웨어면 --tool / --wobj 로 직접 준다.
- 가장 깔끔한 것은 공구 0·작업물 0(없음) 상태에서 실행하는 것이다. 다만 이 스크립트가 바꿔 주지는 않는다.
"""
import argparse
import datetime
import json
import sys
import xmlrpc.client

# 서로 다른 자세 8개 — HD_ACS·HD_AMR 의 Fr5FkCheckTests.Poses 와 같다. 관절 범위(J2·J4 −265~+85) 안.
POSES = [
    [0.158, 59.456, -140.907, 80.848, 90.048, -179.696],   # 홈 위치_2
    [-0.391, -9.414, 119.984, -110.76, 90.001, 180.392],   # 작업 준비 위치
    [0.0, -90.0, 90.0, -90.0, 90.0, 0.0],
    [30.0, -60.0, 100.0, -130.0, 60.0, 45.0],
    [-45.0, -120.0, -80.0, -70.0, -75.0, -90.0],
    [90.0, -30.0, 60.0, -160.0, 120.0, 120.0],
    [-120.0, -75.0, 110.0, 20.0, 45.0, -30.0],
    [150.0, 20.0, -100.0, -200.0, -100.0, 170.0],
]


def call(proxy, name, *args):
    """[err, 값...] 응답에서 값 목록을 돌려준다. 실패·미지원이면 None."""
    try:
        raw = getattr(proxy, name)(*args)
    except Exception as ex:   # 미지원 메서드·통신 오류
        print('  %s 실패: %s' % (name, ex), file=sys.stderr)
        return None
    if isinstance(raw, (list, tuple)) and len(raw) >= 1:
        try:
            if int(raw[0]) != 0:
                print('  %s errcode=%s' % (name, raw[0]), file=sys.stderr)
                return None
        except (TypeError, ValueError):
            return None
        # 값이 [err, x, y, …] 로 펴져 오기도, [err, [x, y, …]] 로 묶여 오기도 한다
        flat = []
        for v in raw[1:]:
            flat.extend(v if isinstance(v, (list, tuple)) else [v])
        try:
            return [float(v) for v in flat]
        except (TypeError, ValueError):
            return None
    return None


def main():
    ap = argparse.ArgumentParser(description='FR5-WML GetForwardKin 표본 수집(조회 전용)')
    ap.add_argument('--host', default='192.168.58.2')
    ap.add_argument('--port', type=int, default=20003)
    ap.add_argument('--out', default='fr5_fk_samples.json')
    ap.add_argument('--tool', type=int, default=None, help='활성 공구 번호(컨트롤러가 알려 주지 않을 때)')
    ap.add_argument('--wobj', type=int, default=None, help='활성 작업물 좌표계 번호(컨트롤러가 알려 주지 않을 때)')
    a = ap.parse_args()

    proxy = xmlrpc.client.ServerProxy('http://%s:%d/RPC2' % (a.host, a.port))

    tool_id = a.tool
    if tool_id is None:
        v = call(proxy, 'GetActualTCPNum', 1)
        tool_id = int(v[0]) if v else None
    wobj_id = a.wobj
    if wobj_id is None:
        v = call(proxy, 'GetActualWObjNum', 1)
        wobj_id = int(v[0]) if v else None
    if tool_id is None or wobj_id is None:
        print('활성 공구/작업물 번호를 읽지 못했습니다. 티치 펜던트에서 확인해 --tool N --wobj N 으로 다시 실행하세요.', file=sys.stderr)
        return 2

    tool = {'id': tool_id, 'pose': None}
    if tool_id > 0:
        v = call(proxy, 'GetToolCoordWithID', tool_id)
        if not v or len(v) < 6:
            print('공구 #%d 좌표를 읽지 못했습니다.' % tool_id, file=sys.stderr)
            return 2
        tool['pose'] = v[:6]
    wobj = {'id': wobj_id, 'pose': None}
    if wobj_id > 0:
        v = call(proxy, 'GetWObjCoordWithID', wobj_id)
        if not v or len(v) < 6:
            print('작업물 좌표계 #%d 를 읽지 못했습니다.' % wobj_id, file=sys.stderr)
            return 2
        wobj['pose'] = v[:6]

    samples = []
    for q in POSES:
        v = call(proxy, 'GetForwardKin', q)
        if not v or len(v) < 6:
            print('GetForwardKin 실패 — 관절 %s' % q, file=sys.stderr)
            continue
        samples.append({'jointsDeg': q, 'fk': v[:6]})
        print('J %s -> %s' % (q, ['%.3f' % x for x in v[:6]]))

    # 지금 자세도 하나 더(관절값·TCP 를 각각 읽은 실측 쌍) — 계산값이 아니라 실제 값끼리의 대조
    actual = None
    j = call(proxy, 'GetActualJointPosDegree', 1)
    p = call(proxy, 'GetActualTCPPose', 1)
    if j and p and len(j) >= 6 and len(p) >= 6:
        actual = {'jointsDeg': j[:6], 'tcpPose': p[:6]}

    # 컨트롤러 DH 보정값(있으면 참고로만 적는다)
    dh = call(proxy, 'GetDHCompensation')

    doc = {
        'note': 'FR5-WML GetForwardKin 표본 — tools/fr5_fk_capture.py',
        'capturedAt': datetime.datetime.now().astimezone().isoformat(timespec='seconds'),
        'controller': '%s:%d' % (a.host, a.port),
        'tool': tool if tool['pose'] else None,
        'wobj': wobj if wobj['pose'] else None,
        'toolId': tool_id,
        'wobjId': wobj_id,
        'actual': actual,
        'dhCompensation': dh,
        'samples': samples,
    }
    with open(a.out, 'w', encoding='utf-8') as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
    print('표본 %d개 저장: %s (공구 #%s, 작업물 #%s)' % (len(samples), a.out, tool_id, wobj_id))
    return 0 if len(samples) >= 5 else 1


if __name__ == '__main__':
    sys.exit(main())
