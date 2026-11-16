#!/usr/bin/env python3
"""HD_AMR 설정 옮기기 — 한 AMR 의 hd_amr.db 에서 파라미터·검사 레시피(와 레시피가 쓰는 티칭 프로필)를
내보내고, 다른 AMR 의 hd_amr.db 에 넣는다. 표준 라이브러리만 쓴다.

  내보내기 (원본 DB → 설정 파일):
      python tools/import_settings.py --export 원본.db --out tools/settings/이름.json

  가져오기 (설정 파일 → 이 PC 의 DB). 기본은 미리보기이고 --apply 를 줘야 바뀐다:
      python tools/import_settings.py
      python tools/import_settings.py --apply

  HD_AMR 앱(데스크톱·웹)을 끄고 실행한다. --apply 는 먼저 DB 를 백업한다.
  앱은 파라미터를 기동 때 읽으므로 가져온 뒤 앱을 다시 켠다.

옮기지 않는 파라미터(장비마다 다른 값) — 가져올 때 대상 DB 의 값을 그대로 둔다:
  calibration  Calib.*, Laser.Head*, Laser.TiltRef.*, Laser.TouchTcp.*, Motion.PostureLimits.Json
  j6           inspection.safeJ6*  (J6 안전 범위·대기 자세 — 홈 자세가 다른 장비에 넣으면 안 된다)
  path         PC 경로 (Camera.Capture.Dir, Vision.Python.Exe, Weld.*.ModelPath)
  test         Sequence.Test.*  (시험용 좌표)
  selection    Sequence.Inspection.*  (화면에서 마지막으로 고른 도면·프로필 등)
  camera       Camera.*  (카메라 축 대응·ROI — 카메라 장착에 따라 다르다)
  tool         검사 툴 치수 (Sequence.InspectCam.Offset*, Sequence.FlatSurface.CameraToLaserShiftYmm,
               Sequence.Laser.WorkingDistanceMm*, Sequence.Camera.TargetDistance, Sequence.WObj.Id)
  internal     Migration.*  (앱 내부 표시)
  ttc          T_T_C 보정 결과 (Calib.HandEye.Pose·Tool — 툴에서 본 카메라 위치)
  tab          T_A_B 보정 결과 (Calib.Mount.Pose — AMR 에서 본 코봇 BASE 위치)
이 중 일부를 함께 옮기려면 --also 로 묶음 이름을 준다(예: --also tool,camera).
ttc·tab 은 장비마다 측정한 값이다. 툴·카메라 장착과 코봇 장착이 원본 장비와 같을 때만 옮기고,
미리보기에 나오는 '지금 값 → 새 값'을 먼저 비교한다. 측정 표본·산출 기록은 옮기지 않는다(결과값만).
calibration·j6·path·internal 은 --also 로도 옮기지 않고 설정 파일에도 싣지 않는다 — 필요하면 파라미터 화면에서 직접 넣는다.

레시피는 Id(LINE, CROSS4 …)로 덮어쓴다. 레시피가 가리키는 티칭 프로필은 이름으로 찾아
같은 이름이 있으면 내용을 바꾸고 없으면 추가한 뒤, 레시피의 프로필 번호를 대상 DB 의 번호로 바꿔 넣는다.
"""
from __future__ import annotations

import argparse
import datetime as dt
import fnmatch
import json
import os
import sqlite3
import sys
from pathlib import Path

DEFAULT_DATA = Path(__file__).resolve().parent / "settings" / "weld_amr_20261117.json"

# 묶음 이름 → 파라미터 이름 패턴. 위에서부터 처음 맞는 묶음에 속한다.
GROUPS: list[tuple[str, list[str]]] = [
    ("internal", ["Migration.*"]),
    ("ttc", ["Calib.HandEye.Pose", "Calib.HandEye.Tool"]),   # T_T_C — 툴 → 카메라 (핸드아이 결과값)
    ("tab", ["Calib.Mount.Pose"]),                            # T_A_B — AMR → 코봇 BASE (장착 보정 결과값)
    ("calibration", ["Calib.*", "Laser.Head*", "Laser.TiltRef.*", "Laser.TouchTcp.*", "Motion.PostureLimits.Json"]),
    ("j6", ["inspection.safeJ6*"]),
    ("path", ["Camera.Capture.Dir", "Vision.Python.Exe", "Weld.*.ModelPath"]),
    ("test", ["Sequence.Test.*"]),
    ("selection", ["Sequence.Inspection.*"]),
    ("camera", ["Camera.*"]),
    ("tool", ["Sequence.InspectCam.Offset*", "Sequence.FlatSurface.CameraToLaserShiftYmm",
              "Sequence.Laser.WorkingDistanceMm*", "Sequence.Camera.TargetDistance", "Sequence.WObj.Id"]),
]
NEVER = {"internal", "calibration", "j6", "path"}      # --also 로도 옮기지 않는 묶음
OPTIONAL = [g for g, _ in GROUPS if g not in NEVER]    # --also 로 고를 수 있는 묶음


def group_of(name: str) -> str | None:
    """파라미터가 속한 '옮기지 않는' 묶음 이름. 옮기는 파라미터면 None."""
    for group, patterns in GROUPS:
        if any(fnmatch.fnmatchcase(name, p) for p in patterns):
            return group
    return None


def default_db_path() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".local" / "share")
    return Path(base) / "HD.AMR" / "hd_amr.db"


def columns(conn: sqlite3.Connection, table: str) -> list[str]:
    return [r[1] for r in conn.execute(f'PRAGMA table_info("{table}")')]


def rows(conn: sqlite3.Connection, table: str) -> list[dict]:
    cols = columns(conn, table)
    return [dict(zip(cols, r)) for r in conn.execute(f'SELECT * FROM "{table}"')]


def now_text() -> str:
    return dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f") + "0"


# ── 내보내기 ─────────────────────────────────────────────────────────
def export(src: Path, out: Path) -> int:
    conn = sqlite3.connect(f"file:{src}?mode=ro", uri=True)
    # 어떤 경우에도 옮기지 않는 묶음(보정값·J6 범위·PC 경로·내부 표시)은 파일에 싣지 않는다.
    all_params = sorted(rows(conn, "Parameters"), key=lambda p: p["Name"])
    params = [{"name": p["Name"], "value": p["Value"], "description": p["Description"], "group": group_of(p["Name"])}
              for p in all_params if group_of(p["Name"]) not in NEVER]
    left_out = len(all_params) - len(params)
    recipes = rows(conn, "InspectionRecipes")
    used = {r[k] for r in recipes for k in ("InspectionProfileId", "ChamferSlopeProfileId") if r.get(k) is not None}
    profiles = [p for p in rows(conn, "InspectionProfiles") if p["Id"] in used]
    missing = used - {p["Id"] for p in profiles}
    if missing:
        print(f"경고: 레시피가 가리키는 프로필 번호 {sorted(missing)} 가 원본 DB 에 없습니다.")
    drawings = {d["Id"]: d["Name"] for d in rows(conn, "Drawings")}
    for p in profiles:
        p["DrawingName"] = drawings.get(p.pop("DrawingId"))
    data = {
        "format": 1,
        "source": src.name,
        "exportedAt": dt.datetime.now().isoformat(timespec="seconds"),
        "parameters": params,
        "profiles": profiles,
        "recipes": recipes,
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    moved = sum(1 for p in params if p["group"] is None)
    print(f"내보냄: {out}")
    print(f"  파라미터 {len(params)}개 (기본으로 옮기는 것 {moved}개, 싣지 않은 장비 고유값 {left_out}개), "
          f"레시피 {len(recipes)}개, 프로필 {len(profiles)}개")
    return 0


# ── 가져오기 ─────────────────────────────────────────────────────────
def plan_parameters(conn: sqlite3.Connection, data: dict, also: set[str]) -> tuple[list, list, list, list]:
    """(추가, 변경, 같음, 건너뜀) — 추가·변경은 (이름, 옛값, 새값, 설명), 건너뜀은 (이름, 묶음)."""
    have = {p["Name"]: p["Value"] for p in rows(conn, "Parameters")}
    add, change, same, skip = [], [], [], []
    for p in data["parameters"]:
        name, group = p["name"], group_of(p["name"])   # 묶음은 파일이 아니라 지금 규칙으로 다시 정한다
        if group is not None and group not in also:
            skip.append((name, group))
        elif name not in have:
            add.append((name, None, p["value"], p.get("description")))
        elif have[name] != p["value"]:
            change.append((name, have[name], p["value"], p.get("description")))
        else:
            same.append(name)
    return add, change, same, skip


def apply_parameters(conn: sqlite3.Connection, add: list, change: list) -> None:
    stamp = now_text()
    for name, _, value, desc in add:
        conn.execute('INSERT INTO "Parameters" (Name, Value, Description, UpdatedAt) VALUES (?, ?, ?, ?)',
                     (name, value, desc, stamp))
    for name, _, value, _ in change:
        conn.execute('UPDATE "Parameters" SET Value = ?, UpdatedAt = ? WHERE Name = ?', (value, stamp, name))


def import_profiles(conn: sqlite3.Connection, data: dict, apply: bool) -> tuple[dict[int, int | None], list[str]]:
    """프로필을 이름으로 넣고 (원본 번호 → 대상 번호) 표와 안내 문구를 돌려준다. 미리보기에서 새로 생길 번호는 None."""
    cols = set(columns(conn, "InspectionProfiles"))
    by_name = {p["Name"]: p for p in rows(conn, "InspectionProfiles")}
    drawings = {d["Name"]: d["Id"] for d in rows(conn, "Drawings")}
    id_map: dict[int, int | None] = {}
    notes: list[str] = []
    stamp = now_text()
    for src in data["profiles"]:
        values = {k: v for k, v in src.items() if k in cols and k not in ("Id", "DrawingId")}
        if "DrawingId" in cols:
            values["DrawingId"] = drawings.get(src.get("DrawingName"))   # 같은 이름의 도면이 없으면 비운다
        values["UpdatedAt"] = stamp
        points = len(json.loads(src["WaypointsJson"]))
        existing = by_name.get(src["Name"])
        if existing is None:
            notes.append(f"  + 프로필 추가   '{src['Name']}' ({src['SeamType']}, 경유점 {points}개)")
            if apply:
                keys = list(values)
                cur = conn.execute(
                    f'INSERT INTO "InspectionProfiles" ({", ".join(keys)}) VALUES ({", ".join("?" * len(keys))})',
                    [values[k] for k in keys])
                id_map[src["Id"]] = cur.lastrowid
            else:
                id_map[src["Id"]] = None
            continue
        id_map[src["Id"]] = existing["Id"]
        differs = [k for k in values if k not in ("UpdatedAt", "CreatedAt") and existing.get(k) != values[k]]
        if not differs:
            notes.append(f"  = 프로필 같음   '{src['Name']}' (번호 {existing['Id']})")
            continue
        notes.append(f"  ~ 프로필 바꿈   '{src['Name']}' (번호 {existing['Id']}, 달라진 항목: {', '.join(differs)})")
        if apply:
            keys = [k for k in values if k != "CreatedAt"]
            conn.execute(f'UPDATE "InspectionProfiles" SET {", ".join(k + " = ?" for k in keys)} WHERE Id = ?',
                         [values[k] for k in keys] + [existing["Id"]])
    return id_map, notes


def import_recipes(conn: sqlite3.Connection, data: dict, id_map: dict[int, int | None], apply: bool) -> list[str]:
    cols = set(columns(conn, "InspectionRecipes"))
    have = {r["Id"]: r for r in rows(conn, "InspectionRecipes")}
    notes: list[str] = []
    stamp = now_text()
    for src in data["recipes"]:
        values = {k: v for k, v in src.items() if k in cols}
        pending = False   # 미리보기에서 프로필이 아직 없어 번호를 모르는 경우
        for key in ("InspectionProfileId", "ChamferSlopeProfileId"):
            if key in values and values[key] is not None:
                mapped = id_map.get(values[key], "missing")
                if mapped == "missing":
                    notes.append(f"  ! 레시피 {src['Id']}: 프로필 번호 {values[key]} 가 설정 파일에 없어 비웁니다.")
                    mapped = None
                elif mapped is None:
                    pending = True
                values[key] = mapped
        values["UpdatedAt"] = stamp
        existing = have.get(src["Id"])
        if existing is None:
            notes.append(f"  + 레시피 추가   {src['Id']}")
            if apply:
                keys = list(values)
                conn.execute(
                    f'INSERT INTO "InspectionRecipes" ({", ".join(keys)}) VALUES ({", ".join("?" * len(keys))})',
                    [values[k] for k in keys])
            continue
        differs = [k for k in values if k not in ("UpdatedAt", "CreatedAt") and existing.get(k) != values[k]]
        if pending:
            differs = sorted(set(differs) | {"InspectionProfileId"})
        if not differs:
            notes.append(f"  = 레시피 같음   {src['Id']}")
            continue
        notes.append(f"  ~ 레시피 바꿈   {src['Id']} (달라진 항목: {', '.join(differs)})")
        if apply:
            keys = [k for k in values if k not in ("Id", "CreatedAt")]
            conn.execute(f'UPDATE "InspectionRecipes" SET {", ".join(k + " = ?" for k in keys)} WHERE Id = ?',
                         [values[k] for k in keys] + [src["Id"]])
    return notes


def schema_problem(conn: sqlite3.Connection) -> str | None:
    """레시피·프로필을 넣을 수 없는 옛 구조면 사유. 앱이 기동 때 구조를 갱신하므로 한 번 실행하면 풀린다."""
    recipe_cols = set(columns(conn, "InspectionRecipes"))
    for need in ("InspectionProfileId", "ChamferSlopeProfileId"):
        if need not in recipe_cols:
            return f"InspectionRecipes 에 {need} 칸이 없습니다."
    info = {r[1]: r for r in conn.execute('PRAGMA table_info("InspectionProfiles")')}
    if not info:
        return "InspectionProfiles 표가 없습니다."
    if "DrawingId" in info and info["DrawingId"][3]:
        return "InspectionProfiles.DrawingId 가 비울 수 없는 칸입니다."
    return None


def backup(db: Path) -> Path:
    target = db.with_name(f"{db.stem}.backup-{dt.datetime.now():%Y%m%d-%H%M%S}.db")
    src = sqlite3.connect(str(db))
    dst = sqlite3.connect(str(target))
    with dst:
        src.backup(dst)   # WAL 에 남은 내용까지 포함한 일관된 사본
    dst.close()
    src.close()
    return target


def short(value: str | None, width: int = 48) -> str:
    if value is None:
        return "(없음)"
    return value if len(value) <= width else value[: width - 1] + "…"


def run_import(db: Path, data_path: Path, also: set[str], apply: bool, do_params: bool, do_recipes: bool) -> int:
    if not db.exists():
        print(f"DB 파일이 없습니다: {db}\nHD_AMR 앱을 한 번 실행해 DB 를 만든 뒤 다시 실행하거나 --db 로 경로를 주세요.")
        return 2
    data = json.loads(data_path.read_text(encoding="utf-8"))
    if data.get("format") != 1:
        print(f"설정 파일 형식을 알 수 없습니다(format={data.get('format')}).")
        return 2

    print(f"설정 파일: {data_path.name} (원본 {data.get('source')}, 내보낸 때 {data.get('exportedAt')})")
    print(f"대상 DB : {db}")
    print("실행 방식: " + ("적용" if apply else "미리보기 — 바꾸지 않습니다. 적용하려면 --apply"))

    conn = sqlite3.connect(str(db), timeout=5)
    conn.execute("PRAGMA foreign_keys = ON")
    if do_recipes and (problem := schema_problem(conn)):
        print(f"\n대상 DB 구조가 오래됐습니다 — {problem}\n"
              "지금 버전의 HD_AMR 앱을 한 번 실행해 DB 를 갱신한 뒤 다시 실행하세요. 바뀐 것은 없습니다.")
        conn.close()
        return 2
    if apply:
        print(f"백업    : {backup(db)}")

    try:
        conn.execute("BEGIN IMMEDIATE")
    except sqlite3.OperationalError as ex:
        print(f"DB 를 잠글 수 없습니다({ex}). HD_AMR 앱(데스크톱·웹)을 끄고 다시 실행하세요.")
        return 3

    try:
        if do_params:
            add, change, same, skip = plan_parameters(conn, data, also)
            print(f"\n[파라미터] 추가 {len(add)} · 변경 {len(change)} · 같음 {len(same)} · 옮기지 않음 {len(skip)}")
            for name, _, value, _ in add:
                print(f"  + {name} = {short(value)}")
            for name, old, value, _ in change:
                print(f"  ~ {name}: {short(old, 30)} → {short(value, 30)}")
            by_group: dict[str, list[str]] = {}
            for name, group in skip:
                by_group.setdefault(group, []).append(name)
            for group, names in by_group.items():
                print(f"  - 옮기지 않음 [{group}] {len(names)}개: {', '.join(names[:4])}{' …' if len(names) > 4 else ''}")
            if apply:
                apply_parameters(conn, add, change)

        if do_recipes:
            id_map, profile_notes = import_profiles(conn, data, apply)
            recipe_notes = import_recipes(conn, data, id_map, apply)
            print(f"\n[티칭 프로필] {len(data['profiles'])}개")
            print("\n".join(profile_notes))
            print(f"\n[검사 레시피] {len(data['recipes'])}개")
            print("\n".join(recipe_notes))

        if apply:
            conn.commit()
            print("\n적용했습니다. HD_AMR 앱을 다시 켜면 반영됩니다.")
        else:
            conn.rollback()
            print("\n미리보기입니다. 바뀐 것은 없습니다.")
    except Exception:
        conn.rollback()
        print("\n오류로 중단했습니다. DB 는 바뀌지 않았습니다.")
        raise
    finally:
        conn.close()
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="HD_AMR 파라미터·검사 레시피 옮기기")
    ap.add_argument("--export", metavar="원본.db", type=Path, help="이 DB 에서 설정 파일을 만든다")
    ap.add_argument("--out", type=Path, default=DEFAULT_DATA, help="내보낼 설정 파일")
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA, help="가져올 설정 파일")
    ap.add_argument("--db", type=Path, default=None, help="대상 DB (기본: %%LOCALAPPDATA%%\\HD.AMR\\hd_amr.db)")
    ap.add_argument("--apply", action="store_true", help="실제로 바꾼다(기본은 미리보기)")
    ap.add_argument("--also", default="", help=f"함께 옮길 묶음(쉼표로 구분): {', '.join(OPTIONAL)}")
    ap.add_argument("--no-params", action="store_true", help="파라미터는 옮기지 않는다")
    ap.add_argument("--no-recipes", action="store_true", help="레시피·프로필은 옮기지 않는다")
    args = ap.parse_args()

    if args.export:
        return export(args.export, args.out)

    also = {g.strip() for g in args.also.split(",") if g.strip()}
    bad = also - set(OPTIONAL)
    if bad:
        print(f"--also 에 쓸 수 없는 묶음: {', '.join(sorted(bad))} (쓸 수 있는 것: {', '.join(OPTIONAL)})")
        return 2
    return run_import(args.db or default_db_path(), args.data, also, args.apply,
                      not args.no_params, not args.no_recipes)


if __name__ == "__main__":
    sys.exit(main())
