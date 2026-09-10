#!/usr/bin/env python3
"""
Minimal MCP client: proves the capability catalog is agent-callable.

Speaks the same protocol an AI agent's MCP client would - JSON-RPC 2.0 over
the server's stdin/stdout - and walks the flow an agent walks:

    initialize -> tools/list (discover) -> tools/call (invoke by name with
    typed args) -> read the structured result contract

Usage:
    python scripts/mcp_demo.py [tool_name] [account_id]

Defaults to the modern-web fee waiver on account 12345. The target app must
be running (see README).
"""

import json
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERVER = [os.path.join(REPO, "src", "Cua.Mcp", "bin", "Debug", "net9.0-windows", "cua-mcp.exe")]


class McpClient:
    def __init__(self, command):
        self.proc = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            bufsize=1,
            cwd=REPO,          # artifacts and evidence are repo-relative
        )
        self._id = 0

    def call(self, method, params=None):
        self._id += 1
        request = {"jsonrpc": "2.0", "id": self._id, "method": method}
        if params is not None:
            request["params"] = params
        self.proc.stdin.write(json.dumps(request) + "\n")
        self.proc.stdin.flush()

        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError("server closed the connection: " + self.proc.stderr.read())
        response = json.loads(line)
        if "error" in response:
            raise RuntimeError(f"{method} failed: {response['error']}")
        return response["result"]

    def notify(self, method, params=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self.proc.stdin.write(json.dumps(message) + "\n")
        self.proc.stdin.flush()

    def close(self):
        self.proc.stdin.close()
        self.proc.terminate()


def rule(title):
    print(f"\n{'=' * 72}\n{title}\n{'=' * 72}")


def main():
    tool_name = sys.argv[1] if len(sys.argv) > 1 else "firstcore_fee_waiver__firstcore_modern"
    account_id = sys.argv[2] if len(sys.argv) > 2 else "12345"

    client = McpClient(SERVER)
    try:
        rule("1. initialize - handshake")
        info = client.call("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "cua-demo-agent", "version": "1.0.0"},
        })
        client.notify("notifications/initialized")
        print(f"server      : {info['serverInfo']['name']} v{info['serverInfo']['version']}")
        print(f"protocol    : {info['protocolVersion']}")
        print(f"capabilities: {', '.join(info['capabilities'])}")
        print(f"\ninstructions to the agent:\n  {info['instructions']}")

        rule("2. tools/list - what can this agent invoke?")
        tools = client.call("tools/list")["tools"]
        for tool in tools:
            required = tool["inputSchema"].get("required", [])
            props = tool["inputSchema"].get("properties", {})
            args = ", ".join(
                f"{name}: {spec.get('type')}{'' if name in required else '?'}"
                for name, spec in props.items()
            )
            print(f"\n  {tool['name']}({args})")
            print(f"      {tool['description']}")

        rule("3. resources/list - artifacts are readable for review")
        for resource in client.call("resources/list")["resources"]:
            print(f"  {resource['uri']}\n      {resource['name']} - {resource['description']}")

        rule("4. tools/call - irreversible capability without acknowledgement is refused")
        refused_risk = client.call("tools/call", {
            "name": tool_name,
            "arguments": {"account_id": account_id},
        })
        print(refused_risk["content"][0]["text"])
        print(f"isError: {refused_risk.get('isError')}")

        rule(f"5. tools/call - invoking {tool_name}(account_id={account_id}, ack_risk=true)")
        print("(this replays the recorded artifact against the live app; no model in the loop)\n")
        result = client.call("tools/call", {
            "name": tool_name,
            "arguments": {"account_id": account_id, "ack_risk": True},
        })
        content = result.get("content", [])
        print(content[0]["text"] if content else "(no content)")
        if len(content) > 1:
            print("\nstructured result the calling agent receives:")
            print(json.dumps(json.loads(content[1]["text"]), indent=2))
        print(f"\nisError: {result.get('isError')}")

        rule("6. tools/call - an uncatalogued capability is refused, not guessed")
        refused = client.call("tools/call", {
            "name": "firstcore_wire_transfer",
            "arguments": {"amount": "100000"},
        })
        print(refused["content"][0]["text"])
        print(f"isError: {refused.get('isError')}")
        print("\n(A capability that exists only as a draft is refused the same way,")
        print(" naming its approval state - only approved artifacts are callable.)")
    finally:
        client.close()


if __name__ == "__main__":
    main()
