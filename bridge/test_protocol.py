#!/usr/bin/env python3
"""Smoke-test the bridge with two fake game clients."""

import asyncio
import json
import os


MAGIC = "SDTV-TERRARIA-BRIDGE"
PORT = int(os.getenv("SDTV_BRIDGE_PORT", "28741"))
TERRARIA_PORT = int(os.getenv("SDTV_BRIDGE_TERRARIA_PORT", str(PORT)))
READ_TIMEOUT_SECONDS = float(os.getenv("SDTV_BRIDGE_TEST_TIMEOUT", "5"))


async def read_message(reader: asyncio.StreamReader) -> dict:
    line = await asyncio.wait_for(reader.readline(), timeout=READ_TIMEOUT_SECONDS)
    if not line:
        raise AssertionError("bridge closed the test connection before sending a message")
    message = json.loads(line.decode())
    assert isinstance(message, dict), message
    return message


async def connect(sender: str, port: int = PORT):
    reader, writer = await asyncio.open_connection("127.0.0.1", port)
    message = {"protocol": MAGIC, "version": 1, "sender": sender, "generation": f"{sender}-test", "kind": "hello"}
    writer.write((json.dumps(message) + "\n").encode())
    await writer.drain()
    ack = await read_message(reader)
    assert ack["kind"] == "hello_ack", ack
    return reader, writer


async def acknowledge_event(reader, writer, event: dict) -> None:
    receipt = {
        "protocol": MAGIC,
        "version": 1,
        "sender": event["recipient"],
        "generation": f"{event['recipient']}-test",
        "kind": "event_receipt",
        "event_id": event["event_id"],
    }
    writer.write((json.dumps(receipt) + "\n").encode())
    await writer.drain()
    acknowledged = await read_message(reader)
    assert acknowledged["kind"] == "event_receipt_ack", acknowledged


async def main() -> None:
    stardew_reader, stardew_writer = await connect("stardew")
    enter = {
        "protocol": MAGIC,
        "version": 1,
        "sender": "stardew",
        "generation": "stardew-test",
        "kind": "event",
        "event": "enter_terraria",
        "payload": {
            "destination": "terraria:surface_spawn",
            "challenge_id": "challenge-test-001",
            "player": {"health": 60, "max_health": 100},
        },
    }
    stardew_writer.write((json.dumps(enter) + "\n").encode())
    await stardew_writer.drain()
    acknowledged = await read_message(stardew_reader)
    assert acknowledged["kind"] == "event_ack", acknowledged
    assert acknowledged["delivered"] is False, acknowledged

    terraria_reader, terraria_writer = await connect("terraria", TERRARIA_PORT)
    peer_status = await read_message(stardew_reader)
    assert peer_status["kind"] == "peer_status", peer_status
    snapshot = await read_message(terraria_reader)
    assert snapshot["kind"] == "peer_snapshot", snapshot

    server_reader, server_writer = await connect("terraria_server", TERRARIA_PORT)
    peer_status = await read_message(stardew_reader)
    assert peer_status["kind"] == "peer_status", peer_status
    terraria_peer_status = await read_message(terraria_reader)
    assert terraria_peer_status["kind"] == "peer_status", terraria_peer_status
    server_snapshot = await read_message(server_reader)
    assert server_snapshot["kind"] == "peer_snapshot", server_snapshot
    forwarded = await read_message(server_reader)
    assert forwarded["event"] == "enter_terraria", forwarded
    assert forwarded["payload"]["challenge_id"] == "challenge-test-001", forwarded
    assert forwarded["event_id"], forwarded
    await acknowledge_event(server_reader, server_writer, {**forwarded, "recipient": "terraria_server"})

    control = {
        "protocol": MAGIC,
        "version": 1,
        "sender": "stardew",
        "generation": "stardew-test",
        "kind": "event",
        "event": "terraria_control",
        "payload": {"active": True, "right": True},
    }
    stardew_writer.write((json.dumps(control) + "\n").encode())
    await stardew_writer.drain()
    acknowledged = await read_message(stardew_reader)
    assert acknowledged["kind"] == "event_ack" and acknowledged["delivered"] is True, acknowledged
    forwarded_control = await read_message(terraria_reader)
    assert forwarded_control["event"] == "terraria_control", forwarded_control
    assert forwarded_control["payload"]["right"] is True, forwarded_control
    try:
        unexpected = await asyncio.wait_for(server_reader.readline(), timeout=0.1)
        assert not unexpected, unexpected
    except asyncio.TimeoutError:
        pass

    consumption = {
        "protocol": MAGIC,
        "version": 1,
        "sender": "stardew",
        "generation": "stardew-test",
        "kind": "event",
        "event": "scene_item_consumed",
        "payload": {
            "action_id": "stardew-test:1",
            "item_id": "Terraria/ArrowGun",
            "slot": 2,
            "owner": 0,
            "consume_item": False,
            "consume_ammo": True,
            "ammo_id": "Terraria/WoodenArrow",
            "ammo_slot": 54,
        },
    }
    stardew_writer.write((json.dumps(consumption) + "\n").encode())
    await stardew_writer.drain()
    acknowledged = await read_message(stardew_reader)
    assert acknowledged["kind"] == "event_ack" and acknowledged["delivered"] is True, acknowledged
    forwarded_consumption = await read_message(server_reader)
    assert forwarded_consumption["event"] == "scene_item_consumed", forwarded_consumption
    assert forwarded_consumption["payload"]["action_id"] == "stardew-test:1", forwarded_consumption
    assert forwarded_consumption["payload"]["consume_item"] is False, forwarded_consumption
    assert forwarded_consumption["payload"]["consume_ammo"] is True, forwarded_consumption
    assert forwarded_consumption["payload"]["ammo_id"] == "Terraria/WoodenArrow", forwarded_consumption
    assert forwarded_consumption["payload"]["ammo_slot"] == 54, forwarded_consumption
    await acknowledge_event(server_reader, server_writer, {**forwarded_consumption, "recipient": "terraria_server"})

    fishing_bait_consumption = {
        "protocol": MAGIC,
        "version": 1,
        "sender": "stardew",
        "generation": "stardew-test",
        "kind": "event",
        "event": "scene_item_consumed",
        "payload": {
            "action_id": "stardew-test:2",
            "item_id": "Terraria/ReinforcedFishingPole",
            "slot": 3,
            "owner": 0,
            "consume_item": False,
            "consume_ammo": False,
            "consume_bait": True,
            "bait_id": "Terraria/JourneymanBait",
            "bait_slot": 12,
        },
    }
    stardew_writer.write((json.dumps(fishing_bait_consumption) + "\n").encode())
    await stardew_writer.drain()
    acknowledged = await read_message(stardew_reader)
    assert acknowledged["kind"] == "event_ack" and acknowledged["delivered"] is True, acknowledged
    forwarded_bait = await read_message(server_reader)
    assert forwarded_bait["event"] == "scene_item_consumed", forwarded_bait
    assert forwarded_bait["payload"]["consume_bait"] is True, forwarded_bait
    assert forwarded_bait["payload"]["bait_id"] == "Terraria/JourneymanBait", forwarded_bait
    assert forwarded_bait["payload"]["bait_slot"] == 12, forwarded_bait
    await acknowledge_event(server_reader, server_writer, {**forwarded_bait, "recipient": "terraria_server"})

    consumption_result = {
        "protocol": MAGIC,
        "version": 1,
        "sender": "terraria_server",
        "generation": "terraria-server-test",
        "kind": "event",
        "event": "scene_item_consumption_result",
        "payload": {"action_id": "stardew-test:1", "item_id": "Terraria/ArrowGun", "consumed": True, "ammo_slot": 54},
    }
    server_writer.write((json.dumps(consumption_result) + "\n").encode())
    await server_writer.drain()
    acknowledged = await read_message(server_reader)
    assert acknowledged["kind"] == "event_ack" and acknowledged["delivered"] is True, acknowledged
    forwarded_result = await read_message(stardew_reader)
    assert forwarded_result["event"] == "scene_item_consumption_result", forwarded_result
    assert forwarded_result["payload"]["consumed"] is True, forwarded_result
    await acknowledge_event(stardew_reader, stardew_writer, {**forwarded_result, "recipient": "stardew"})

    returned = {
        "protocol": MAGIC,
        "version": 1,
        "sender": "terraria",
        "generation": "terraria-test",
        "kind": "event",
        "event": "return_stardew",
        "payload": {"destination": "stardew:portal", "player": {"life": 40, "life_max": 100}},
    }
    terraria_writer.write((json.dumps(returned) + "\n").encode())
    await terraria_writer.drain()
    forwarded = await read_message(stardew_reader)
    assert forwarded["event"] == "return_stardew", forwarded
    await acknowledge_event(stardew_reader, stardew_writer, {**forwarded, "recipient": "stardew"})
    acknowledged = await read_message(terraria_reader)
    assert acknowledged["kind"] == "event_ack", acknowledged
    assert acknowledged["delivered"] is True, acknowledged

    cleared = {
        "protocol": MAGIC,
        "version": 1,
        "sender": "terraria",
        "generation": "terraria-test",
        "kind": "event",
        "event": "terraria_challenge_cleared",
        "payload": {"challenge_id": "challenge-test-001", "reward": "stardew:coal", "quantity": 5},
    }
    terraria_writer.write((json.dumps(cleared) + "\n").encode())
    await terraria_writer.drain()
    forwarded = await read_message(stardew_reader)
    assert forwarded["event"] == "terraria_challenge_cleared", forwarded
    assert forwarded["payload"]["quantity"] == 5, forwarded
    await acknowledge_event(stardew_reader, stardew_writer, {**forwarded, "recipient": "stardew"})
    acknowledged = await read_message(terraria_reader)
    assert acknowledged["kind"] == "event_ack", acknowledged
    assert acknowledged["delivered"] is True, acknowledged

    state_file = os.getenv("SDTV_BRIDGE_STATE", "bridge-state.json")
    with open(state_file, encoding="utf-8") as stream:
        saved = json.load(stream)
    assert saved["snapshots"]["stardew"]["payload"]["health"] == 60, saved
    assert saved["snapshots"]["terraria"]["payload"]["life"] == 40, saved
    assert saved.get("outbox", {}) == {}, saved

    resumed_reader, resumed_writer = await connect("stardew")
    resumed_snapshot = await read_message(resumed_reader)
    assert resumed_snapshot["kind"] == "peer_snapshot", resumed_snapshot
    assert resumed_snapshot["sender"] == "terraria", resumed_snapshot
    assert resumed_snapshot["payload"]["life"] == 40, resumed_snapshot
    print("bridge-protocol-test-ok")

    stardew_writer.close()
    terraria_writer.close()
    server_writer.close()
    resumed_writer.close()
    await stardew_writer.wait_closed()
    await terraria_writer.wait_closed()
    await server_writer.wait_closed()
    await resumed_writer.wait_closed()


if __name__ == "__main__":
    asyncio.run(main())
