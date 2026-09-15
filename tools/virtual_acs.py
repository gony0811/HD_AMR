#!/usr/bin/env python3
"""Virtual ACS (master control) for VDA5050 adapter testing.

Connects to the local MQTT broker, watches the AMR's connection/state topics,
and publishes orders / instantActions the way a real ACS would.

Usage:
  python tools/virtual_acs.py              interactive menu
  python tools/virtual_acs.py watch        print connection/state messages only
  python tools/virtual_acs.py order        send order to the AMR's current position, watch until finished
  python tools/virtual_acs.py order X Y TH send order to explicit coordinates
  python tools/virtual_acs.py bad          send invalid order (wrong mapId) to test rejection
  python tools/virtual_acs.py inspect [SEAM [WALL [N]]]
                                           send order with N startWeldInspection actions
                                           (SEAM: LINE|CROSS|CORNER, WALL: B/T/SM/PM/F/A/SL/PL/SU/PU
                                           or an undefined code like W03; default LINE SM 2)
  python tools/virtual_acs.py estop        send emergencyStop instant action
  python tools/virtual_acs.py die          exit without MQTT DISCONNECT so the broker
                                           publishes the ACS Last Will (CONNECTIONBROKEN)

Publishes the ACS liveness signal per spec §7.2 [N12]: retained ONLINE on connect,
retained OFFLINE on graceful exit, Last Will CONNECTIONBROKEN on abnormal death.
"""

import json
import os
import sys
import threading
import time
import uuid
from datetime import datetime, timezone

import paho.mqtt.client as mqtt

BROKER_HOST = "127.0.0.1"
BROKER_PORT = 1883
TOPIC_BASE = "uagv/v2/HHI/AMR-01"
MANUFACTURER = "HHI"
SERIAL_NUMBER = "AMR-01"
MAP_ID = "CT1-L1"

# ACS liveness identity (spec §7.2 [N12]) - one per ACS instance, distinct from robot topics
ACS_MANUFACTURER = "HD_ACS"
ACS_SERIAL_NUMBER = "hd-acs-master"
ACS_CONN_TOPIC = f"uagv/v2/{ACS_MANUFACTURER}/{ACS_SERIAL_NUMBER}/connection"

_header_id = 0
_last_state = None
_state_event = threading.Event()


def _timestamp():
    now = datetime.now(timezone.utc)
    return now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond // 1000:03d}Z"


def _header():
    global _header_id
    _header_id += 1
    return {
        "headerId": _header_id,
        "timestamp": _timestamp(),
        "version": "2.0.0",
        "manufacturer": MANUFACTURER,
        "serialNumber": SERIAL_NUMBER,
    }


def build_order(x, y, theta, map_id=MAP_ID, node_id="TEST-N1", actions=None):
    return {
        **_header(),
        "orderId": str(uuid.uuid4()),
        "orderUpdateId": 0,
        "nodes": [
            {
                "nodeId": node_id,
                "sequenceId": 0,
                "released": True,
                "nodePosition": {
                    "x": x,
                    "y": y,
                    "theta": theta,
                    "allowedDeviationXY": 0.3,
                    "allowedDeviationTheta": 0.5,
                    "mapId": map_id,
                },
                "actions": actions or [],
            }
        ],
        "edges": [],
    }


def build_weld_inspection_action(seam_type="LINE", wall_code="SM", seq_in_group=1,
                                 anchor_group="CT1-L1-TEST-ST01", dxf_id="DXF-TEST-01"):
    """startWeldInspection action per spec §8.1/§8.4 (golden example shape)."""
    return {
        "actionType": "startWeldInspection",
        "actionId": str(uuid.uuid4()),
        "blockingType": "HARD",
        "actionParameters": [
            {"key": "jobRef", "value": f"JOB-CT1-L1-{wall_code}-S{seq_in_group:02d}"},
            {"key": "position", "value": {
                "seamStartW": [12.510, 5.980, 1.420],
                "seamEndW": [13.310, 5.980, 1.420],
                "drawingPos": {"tank": "CT1", "level": 1, "wall_code": wall_code,
                               "u": 3.120, "v": 1.420,
                               "x": 3.120, "y": 0.0, "z": 1.420}}},
            {"key": "params", "value": {
                "seamType": seam_type,
                "sectionDxfId": dxf_id,
                "inspectionProfileId": "INSPECT-STD-01",
                "standoffMm": 400,
                "workingDistanceMm": 400,
                "anchorGroupId": anchor_group,
                "seqInGroup": seq_in_group}},
        ],
    }


def build_estop():
    return {
        **_header(),
        "actions": [
            {
                "actionType": "emergencyStop",
                "actionId": str(uuid.uuid4()),
                "blockingType": "HARD",
                "actionParameters": [],
            }
        ],
    }


def build_acs_connection(state):
    return {
        "headerId": 1,  # ACS-session monotonic; a fixed value is fine for the will payload (§7.2)
        "timestamp": _timestamp(),
        "version": "2.0.0",
        "manufacturer": ACS_MANUFACTURER,
        "serialNumber": ACS_SERIAL_NUMBER,
        "connectionState": state,
    }


def _summarize_state(s):
    pos = s.get("agvPosition") or {}
    errors = ", ".join(e.get("errorType", "?") for e in s.get("errors") or []) or "-"
    actions = ", ".join(
        f"{a.get('actionType', a.get('actionId', '?'))}:{a.get('actionStatus')}"
        for a in s.get("actionStates") or []
    ) or "-"
    return (
        f"order={s.get('orderId') or '-':<10.10} lastNode={s.get('lastNodeId') or '-':<8} "
        f"driving={s.get('driving')} mode={s.get('operatingMode')} "
        f"pos=({pos.get('x')}, {pos.get('y')}, {pos.get('theta')}) "
        f"nodeStates={len(s.get('nodeStates') or [])} actions=[{actions}] errors=[{errors}]"
    )


def _on_connect(client, userdata, flags, reason_code, properties):
    print(f"[acs] broker connected ({reason_code})")
    client.subscribe([(f"{TOPIC_BASE}/state", 1), (f"{TOPIC_BASE}/connection", 1)])
    client.publish(ACS_CONN_TOPIC, json.dumps(build_acs_connection("ONLINE")), qos=1, retain=True)
    print(f"[acs] liveness ONLINE (retained) -> {ACS_CONN_TOPIC}")


def _on_message(client, userdata, msg):
    global _last_state
    try:
        payload = json.loads(msg.payload)
    except json.JSONDecodeError:
        print(f"[acs] {msg.topic}: unparseable payload {msg.payload[:100]!r}")
        return
    if msg.topic.endswith("/connection"):
        print(f"[acs] connection: {payload.get('connectionState')} (retain={msg.retain})")
    elif msg.topic.endswith("/state"):
        _last_state = payload
        _state_event.set()
        print(f"[acs] state: {_summarize_state(payload)}")


def connect():
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="virtual-acs")
    client.on_connect = _on_connect
    client.on_message = _on_message
    # Last Will = liveness CONNECTIONBROKEN (retained) - broker publishes it on abnormal death (§7.2)
    client.will_set(ACS_CONN_TOPIC, json.dumps(build_acs_connection("CONNECTIONBROKEN")), qos=1, retain=True)
    client.connect(BROKER_HOST, BROKER_PORT, keepalive=30)
    client.loop_start()
    return client


def wait_for_position(timeout=15):
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = _last_state
        if s and s.get("agvPosition") and s["agvPosition"].get("x") is not None:
            return s["agvPosition"]
        _state_event.clear()
        _state_event.wait(timeout=1)
    return None


def publish(client, channel, payload):
    topic = f"{TOPIC_BASE}/{channel}"
    client.publish(topic, json.dumps(payload), qos=1).wait_for_publish(5)
    print(f"[acs] published to {topic}:\n{json.dumps(payload, indent=2)}")


def send_order(client, coords=None, map_id=MAP_ID, watch=True):
    if coords is None:
        pos = wait_for_position()
        if pos is None:
            print("[acs] no agvPosition seen in state yet - cannot target current position")
            return None
        coords = (pos["x"], pos["y"], pos.get("theta") or 0.0)
        print(f"[acs] targeting current position: {coords}")
    order = build_order(*coords, map_id=map_id)
    publish(client, "order", order)
    if watch:
        watch_order(order["orderId"])
    return order


def send_inspect(client, seam="LINE", wall="SM", count=2, watch=True):
    """Order to current position carrying N startWeldInspection actions (shared anchorGroup)."""
    pos = wait_for_position()
    if pos is None:
        print("[acs] no agvPosition seen in state yet - cannot target current position")
        return None
    actions = [build_weld_inspection_action(seam, wall, seq_in_group=i + 1) for i in range(count)]
    order = build_order(pos["x"], pos["y"], pos.get("theta") or 0.0, actions=actions)
    publish(client, "order", order)
    if watch:
        watch_actions(order["orderId"], [a["actionId"] for a in actions])
    return order


def watch_actions(order_id, action_ids, timeout=600):
    """Wait until every listed action reaches FINISHED/FAILED, then print the outcome."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        _state_event.clear()
        _state_event.wait(timeout=5)
        s = _last_state
        if not s or s.get("orderId") != order_id:
            continue
        states = {a.get("actionId"): a for a in s.get("actionStates") or []}
        done = [states.get(aid) for aid in action_ids]
        if all(a and a.get("actionStatus") in ("FINISHED", "FAILED") for a in done):
            print("[acs] ALL ACTIONS TERMINAL:")
            for a in done:
                print(f"  - {a['actionId'][:8]}… {a['actionStatus']}: {a.get('resultDescription')}")
            errors = s.get("errors") or []
            print(f"  errors: {errors if errors else '-'}")
            return True
    print("[acs] timed out waiting for action completion")
    return False


def watch_order(order_id, timeout=120):
    deadline = time.time() + timeout
    while time.time() < deadline:
        _state_event.clear()
        _state_event.wait(timeout=5)
        s = _last_state
        if not s:
            continue
        errors = s.get("errors") or []
        if any(e.get("errorType") == "orderValidationError" for e in errors):
            print(f"[acs] ORDER REJECTED: {errors}")
            return False
        if s.get("orderId") == order_id and not s.get("driving") and not s.get("nodeStates"):
            print(f"[acs] ORDER COMPLETE: lastNodeId={s.get('lastNodeId')}")
            return True
    print("[acs] timed out waiting for order completion")
    return False


def interactive(client):
    print(
        "commands: order | order X Y TH | bad | inspect [SEAM [WALL [N]]] | estop | die | quit"
    )
    for line in sys.stdin:
        parts = line.split()
        if not parts:
            continue
        cmd = parts[0].lower()
        if cmd == "order" and len(parts) == 4:
            send_order(client, coords=tuple(map(float, parts[1:4])), watch=False)
        elif cmd == "order":
            send_order(client, watch=False)
        elif cmd == "bad":
            send_order(client, map_id="WRONG-MAP", watch=False)
        elif cmd == "inspect":
            seam = parts[1].upper() if len(parts) > 1 else "LINE"
            wall = parts[2] if len(parts) > 2 else "SM"
            count = int(parts[3]) if len(parts) > 3 else 2
            send_inspect(client, seam=seam, wall=wall, count=count, watch=False)
        elif cmd == "estop":
            publish(client, "instantActions", build_estop())
        elif cmd == "die":
            _die()
        elif cmd in ("quit", "exit", "q"):
            break


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = sys.argv[1:]
    client = connect()
    try:
        if not args:
            interactive(client)
        elif args[0] == "watch":
            while True:
                time.sleep(1)
        elif args[0] == "order" and len(args) == 4:
            send_order(client, coords=tuple(map(float, args[1:4])))
        elif args[0] == "order":
            send_order(client)
        elif args[0] == "bad":
            order = send_order(client, map_id="WRONG-MAP", watch=False)
            if order:
                watch_order(order["orderId"], timeout=15)
        elif args[0] == "inspect":
            seam = args[1].upper() if len(args) > 1 else "LINE"
            wall = args[2] if len(args) > 2 else "SM"
            count = int(args[3]) if len(args) > 3 else 2
            send_inspect(client, seam=seam, wall=wall, count=count)
        elif args[0] == "estop":
            time.sleep(1)  # let subscriptions settle so the resulting state is visible
            publish(client, "instantActions", build_estop())
            time.sleep(5)
        elif args[0] == "die":
            time.sleep(1)  # ensure ONLINE went out first so BROKEN is an observable transition
            _die()
        else:
            print(__doc__)
    except KeyboardInterrupt:
        pass
    finally:
        # graceful exit - retained OFFLINE replaces ONLINE before the clean DISCONNECT (§7.2)
        client.publish(ACS_CONN_TOPIC, json.dumps(build_acs_connection("OFFLINE")), qos=1, retain=True).wait_for_publish(5)
        print("[acs] liveness OFFLINE (retained) published - disconnecting")
        client.loop_stop()
        client.disconnect()


def _die():
    # exit without sending MQTT DISCONNECT: the TCP drop makes the broker publish the Last Will
    print("[acs] dying without DISCONNECT - broker will publish CONNECTIONBROKEN")
    os._exit(1)


if __name__ == "__main__":
    main()
