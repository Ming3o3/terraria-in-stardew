#!/usr/bin/env python3
"""Local two-process bridge for Stardew Valley and Terraria.

The bridge owns lifecycle and cross-game events. Each game remains the authority
for its own world and rendering. Messages are UTF-8 JSON Lines on localhost.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import shlex
import signal
import subprocess
import sys
import time
import uuid
from collections import defaultdict, deque
from dataclasses import dataclass
from typing import Any


MAGIC = "SDTV-TERRARIA-BRIDGE"
VERSION = 1


def now_ms() -> int:
    return int(time.time() * 1000)


def focus_process(name: str | None) -> None:
    """Best-effort focus handoff on macOS; harmless on other platforms."""
    if not name or sys.platform != "darwin":
        return
    script = (
        'tell application "System Events"\n'
        f'  if exists process "{name}" then set frontmost of process "{name}" to true\n'
        "end tell"
    )
    subprocess.run(["osascript", "-e", script], check=False, capture_output=True)


@dataclass
class Peer:
    sender: str
    reader: asyncio.StreamReader
    writer: asyncio.StreamWriter
    generation: str
    last_seen: float

    async def send(self, message: dict[str, Any]) -> None:
        message.setdefault("protocol", MAGIC)
        message.setdefault("version", VERSION)
        message.setdefault("time", now_ms())
        self.writer.write((json.dumps(message, separators=(",", ":")) + "\n").encode())
        await self.writer.drain()


class Bridge:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.peers: dict[str, Peer] = {}
        self.pending: dict[str, deque[dict[str, Any]]] = defaultdict(lambda: deque(maxlen=128))
        self.outbox: dict[str, dict[str, dict[str, Any]]] = defaultdict(dict)
        self.snapshots: dict[str, dict[str, Any]] = {}
        self.processes: list[subprocess.Popen[Any]] = []
        self.closed = False
        self.state_file = self.args.state_file
        self.load_state()

    async def run(self) -> None:
        if not self.args.no_launch:
            self.validate_launch_commands()
        servers = [await asyncio.start_server(self.handle_client, "127.0.0.1", port)
                   for port in dict.fromkeys([self.args.port, *self.args.port_alias])]
        sockets = ", ".join(
            str(sock.getsockname()) for server in servers for sock in server.sockets or []
        )
        print(f"bridge listening on {sockets}", flush=True)
        tasks = [asyncio.create_task(server.serve_forever()) for server in servers]
        tasks.append(asyncio.create_task(self.watchdog()))
        try:
            if not self.args.no_launch:
                self.launch_games()
                tasks.append(asyncio.create_task(self.watch_processes()))
            done, pending = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
            for task in done:
                exception = task.exception()
                if exception and not isinstance(exception, asyncio.CancelledError):
                    raise exception
        except asyncio.CancelledError:
            pass
        finally:
            self.closed = True
            for task in tasks:
                task.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)
            for server in servers:
                server.close()
            await asyncio.gather(*(server.wait_closed() for server in servers))
            self.stop_games()

    def launch_games(self) -> None:
        self.validate_launch_commands()
        commands = (("stardew", self.args.stardew_cmd), ("terraria", self.args.terraria_cmd))
        for label, command in commands:
            argv = shlex.split(command)
            if not argv:
                raise ValueError(f"{label} launch command is empty")
            print(f"starting {label}: {argv[0]}", flush=True)
            self.processes.append(subprocess.Popen(argv, cwd=self.args.cwd or None))

    def validate_launch_commands(self) -> None:
        commands = (("stardew", self.args.stardew_cmd), ("terraria", self.args.terraria_cmd))
        missing = [label for label, command in commands if not command or not shlex.split(command)]
        if missing:
            raise ValueError(
                "both game launch commands are required unless --no-launch is used; missing or empty: "
                + ", ".join(missing)
            )

    async def watch_processes(self) -> None:
        while not self.closed:
            await asyncio.sleep(0.5)
            for process in self.processes:
                code = process.poll()
                if code is not None:
                    print(f"game process {process.pid} exited with status {code}; stopping bridge session", flush=True)
                    return

    def stop_games(self) -> None:
        for process in self.processes:
            if process.poll() is None:
                process.send_signal(signal.SIGTERM)

    async def handle_client(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        peer: Peer | None = None
        address = writer.get_extra_info("peername")
        try:
            while line := await reader.readline():
                if len(line) > 1024 * 1024:
                    raise ValueError("message too large")
                message = json.loads(line)
                if not isinstance(message, dict):
                    raise ValueError("message must be an object")
                if message.get("protocol") != MAGIC or message.get("version") != VERSION:
                    raise ValueError("protocol mismatch")
                if peer is None:
                    sender = str(message.get("sender", ""))
                    if sender not in {"stardew", "terraria", "terraria_server"}:
                        raise ValueError("invalid sender")
                    peer = Peer(sender, reader, writer, str(message.get("generation", "")), time.monotonic())
                    old = self.peers.get(sender)
                    if old:
                        old.writer.close()
                    self.peers[sender] = peer
                    print(f"connected {sender} from {address}", flush=True)
                peer.last_seen = time.monotonic()
                await self.handle_message(peer, message)
        except (asyncio.IncompleteReadError, ConnectionError, json.JSONDecodeError, ValueError) as exc:
            print(f"client {address} disconnected: {exc}", flush=True)
        finally:
            if peer and self.peers.get(peer.sender) is peer:
                del self.peers[peer.sender]
            writer.close()
            await writer.wait_closed()

    async def handle_message(self, peer: Peer, message: dict[str, Any]) -> None:
        kind = message.get("kind")
        if kind == "hello":
            await peer.send({"kind": "hello_ack", "sender": "bridge", "peer": list(self.peers)})
            await self.broadcast({"kind": "peer_status", "peer": peer.sender, "connected": True}, exclude=peer.sender)
            snapshot = self.snapshots.get(self.other_sender(peer.sender))
            if snapshot:
                await peer.send({
                    "kind": "peer_snapshot",
                    "sender": snapshot["sender"],
                    "generation": snapshot.get("generation", ""),
                    "sequence": snapshot.get("sequence", 0),
                    "payload": snapshot.get("payload", {}),
                })
            sent_ids: set[str] = set()
            while self.pending[peer.sender]:
                queued = self.pending[peer.sender].popleft()
                await peer.send(queued)
                if queued.get("kind") == "event":
                    event_id = queued.get("event_id")
                    if isinstance(event_id, str):
                        sent_ids.add(event_id)
                        self.outbox[peer.sender][event_id] = queued
                    self.focus_for_event(queued.get("event"))
                self.save_state()
            for event_id, queued in self.outbox[peer.sender].items():
                if event_id in sent_ids:
                    continue
                await peer.send(queued)
                if queued.get("kind") == "event":
                    self.focus_for_event(queued.get("event"))
            return
        if kind == "heartbeat":
            await peer.send({"kind": "heartbeat_ack", "sender": "bridge"})
            return
        if kind == "snapshot":
            self.record_snapshot(peer.sender, message)
            target = self.other(peer.sender)
            if target:
                await target.send({
                    "kind": "peer_snapshot",
                    "sender": peer.sender,
                    "generation": message.get("generation", peer.generation),
                    "sequence": message.get("sequence", 0),
                    "payload": message.get("payload", {}),
                })
            return
        if kind == "event":
            await self.route_event(peer, message)
            return
        if kind == "event_receipt":
            event_id = message.get("event_id")
            if isinstance(event_id, str) and event_id in self.outbox[peer.sender]:
                del self.outbox[peer.sender][event_id]
                self.save_state()
                await peer.send({"kind": "event_receipt_ack", "sender": "bridge", "event_id": event_id})
            return
        print(f"ignored {peer.sender} message kind={kind!r}", flush=True)

    async def route_event(self, peer: Peer, message: dict[str, Any]) -> None:
        event = message.get("event")
        payload = message.get("payload", {})
        if event == "terraria_control":
            target = self.peers.get("terraria") if peer.sender == "stardew" else self.other(peer.sender)
            forwarded = {
                "kind": "event",
                "sender": peer.sender,
                "event": event,
                "payload": payload,
                "sequence": message.get("sequence", 0),
            }
            if target:
                await target.send(forwarded)
            await peer.send({
                "kind": "event_ack",
                "sender": "bridge",
                "event": event,
                "delivered": target is not None,
            })
            return
        event_id = message.get("event_id")
        if not isinstance(event_id, str) or not event_id:
            sequence = message.get("sequence")
            if isinstance(sequence, int) and sequence > 0:
                event_id = f"{peer.sender}:{peer.generation}:{sequence}"
            else:
                event_id = f"{peer.sender}:{peer.generation}:{uuid.uuid4().hex}"
        if isinstance(payload, dict) and isinstance(payload.get("player"), dict):
            self.record_snapshot(peer.sender, {
                "generation": message.get("generation", peer.generation),
                "sequence": message.get("sequence", 0),
                "payload": payload["player"],
            })
        if event == "enter_terraria" and peer.sender == "stardew":
            target = self.peers.get("terraria_server") or self.peers.get("terraria")
        else:
            target = self.other(peer.sender)
        forwarded = {
            "kind": "event",
            "sender": peer.sender,
            "generation": message.get("generation", peer.generation),
            "event": event,
            "event_id": event_id,
            "payload": payload,
            "sequence": message.get("sequence", 0),
        }
        if target:
            recipient = target.sender
        elif event == "enter_terraria" and peer.sender == "stardew":
            recipient = "terraria_server"
        else:
            recipient = self.other_sender(peer.sender)
        self.outbox[recipient][event_id] = forwarded
        delivered = target is not None
        self.save_state()
        if target:
            await target.send(forwarded)
        else:
            self.pending[recipient].append(forwarded)
        await peer.send({"kind": "event_ack", "sender": "bridge", "event": event, "delivered": delivered})
        if delivered:
            self.focus_for_event(event)

    def focus_for_event(self, event: str | None) -> None:
        if self.args.no_focus:
            return
        if event == "return_stardew":
            focus_process(self.args.stardew_process)

    async def broadcast(self, message: dict[str, Any], exclude: str | None = None) -> None:
        await asyncio.gather(*(peer.send(message) for sender, peer in self.peers.items() if sender != exclude))

    def other(self, sender: str) -> Peer | None:
        if sender == "stardew":
            return self.peers.get("terraria_server") or self.peers.get("terraria")
        return self.peers.get("stardew")

    @staticmethod
    def other_sender(sender: str) -> str:
        return "terraria" if sender == "stardew" else "stardew"

    def record_snapshot(self, sender: str, message: dict[str, Any]) -> None:
        if sender == "terraria_server":
            sender = "terraria"
        self.snapshots[sender] = {
            "sender": sender,
            "generation": message.get("generation", ""),
            "sequence": message.get("sequence", 0),
            "payload": message.get("payload", {}),
            "time": now_ms(),
        }
        self.save_state()

    def load_state(self) -> None:
        try:
            with open(self.state_file, encoding="utf-8") as stream:
                state = json.load(stream)
            snapshots = state.get("snapshots", {})
            if isinstance(snapshots, dict):
                self.snapshots = {
                    sender: value for sender, value in snapshots.items()
                    if sender in {"stardew", "terraria"} and isinstance(value, dict)
                }
            pending_events = state.get("pending_events", {})
            if isinstance(pending_events, dict):
                for sender in ("stardew", "terraria"):
                    queued = pending_events.get(sender, [])
                    if isinstance(queued, list):
                        self.pending[sender].extend(item for item in queued if isinstance(item, dict))
            outbox = state.get("outbox", {})
            if isinstance(outbox, dict):
                for sender in ("stardew", "terraria"):
                    queued = outbox.get(sender, {})
                    if isinstance(queued, dict):
                        self.outbox[sender].update(
                            (event_id, item) for event_id, item in queued.items()
                            if isinstance(event_id, str) and isinstance(item, dict)
                        )
        except FileNotFoundError:
            return
        except (OSError, json.JSONDecodeError) as exc:
            print(f"bridge state warning: cannot load {self.state_file}: {exc}", file=sys.stderr)

    def save_state(self) -> None:
        directory = os.path.dirname(os.path.abspath(self.state_file))
        temporary = f"{self.state_file}.tmp"
        try:
            os.makedirs(directory, exist_ok=True)
            with open(temporary, "w", encoding="utf-8") as stream:
                json.dump({
                    "version": VERSION,
                    "snapshots": self.snapshots,
                    "pending_events": {sender: list(queue) for sender, queue in self.pending.items() if queue},
                    "outbox": {sender: events for sender, events in self.outbox.items() if events},
                }, stream, ensure_ascii=False, separators=(",", ":"))
            os.replace(temporary, self.state_file)
        except OSError as exc:
            print(f"bridge state warning: cannot save {self.state_file}: {exc}", file=sys.stderr)

    async def watchdog(self) -> None:
        while not self.closed:
            await asyncio.sleep(0.5)
            now = time.monotonic()
            for peer in list(self.peers.values()):
                if now - peer.last_seen > 1.5:
                    try:
                        await peer.send({"kind": "peer_timeout", "sender": "bridge"})
                    except ConnectionError:
                        pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=int(os.getenv("SDTV_BRIDGE_PORT", "28741")))
    parser.add_argument(
        "--port-alias", type=int, action="append", default=[],
        help="also accept game clients on this localhost port; may be repeated",
    )
    parser.add_argument("--cwd", help="working directory for launched games")
    parser.add_argument("--stardew-cmd", default=os.getenv("SDTV_COMMAND"))
    parser.add_argument("--terraria-cmd", default=os.getenv("TERRARIA_COMMAND"))
    parser.add_argument("--stardew-process", default="StardewModdingAPI")
    parser.add_argument("--terraria-process", default="Terraria")
    parser.add_argument("--state-file", default=os.getenv("SDTV_BRIDGE_STATE", "bridge-state.json"))
    parser.add_argument("--no-launch", action="store_true", help="only listen for already-running game mods")
    parser.add_argument("--no-focus", action="store_true", help="disable operating-system window focus changes")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        asyncio.run(Bridge(args).run())
    except KeyboardInterrupt:
        return 130
    except (OSError, ValueError) as exc:
        print(f"bridge configuration error: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
