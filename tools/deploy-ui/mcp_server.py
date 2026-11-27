#!/usr/bin/env python3
"""Zero-dependency stdio MCP server exposing the NetDaemon Deploy MCP App."""

from __future__ import annotations

import argparse
import json
import os
import sys
import threading
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Any

from deploy_ops import DeployOps

APP_URI = "ui://netdaemon-deploy/app.html"
PROTOCOL_VERSION = "2025-03-26"

APP_META = {
    "ui": {"resourceUri": APP_URI},
    "com.google.antigravity/ui": {
        "entrypoints": [
            {"type": "aux_pane", "title": "NetDaemon Deploy"},
            {"type": "global", "title": "NetDaemon Deploy"},
        ]
    },
}

APP_ONLY_META = {"ui": {"visibility": ["app"]}}


def _tool_definitions() -> list[dict[str, Any]]:
    return [
        {
            "name": "open_deploy_dashboard",
            "title": "NetDaemon Deploy",
            "description": (
                "Open the NetDaemon PR, Worktree, and Deploy dashboard to manage "
                "GitHub pull requests, pull main, and run deploy.sh."
            ),
            "inputSchema": {
                "type": "object",
                "properties": {
                    "fetch": {
                        "type": "boolean",
                        "description": "Fetch origin/main before loading status (default true).",
                    }
                },
            },
            "_meta": APP_META,
        },
        {
            "name": "app_get_status",
            "title": "Get deploy status",
            "description": "Fetch git, PR, worktree, and deploy status.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "fetch": {"type": "boolean"},
                },
            },
            "_meta": APP_ONLY_META,
        },
        {
            "name": "app_merge_pr",
            "title": "Merge PR",
            "description": "Merge a GitHub pull request.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "number": {"type": "integer"},
                    "method": {"type": "string", "enum": ["squash", "merge", "rebase"]},
                    "deleteBranch": {"type": "boolean"},
                },
                "required": ["number"],
            },
            "_meta": APP_ONLY_META,
        },
        {
            "name": "app_close_pr",
            "title": "Close PR",
            "description": "Close a GitHub pull request.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "number": {"type": "integer"},
                },
                "required": ["number"],
            },
            "_meta": APP_ONLY_META,
        },
        {
            "name": "app_remove_worktree",
            "title": "Remove worktree",
            "description": "Remove a git worktree and optionally delete its local branch.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "path": {"type": "string"},
                    "force": {"type": "boolean"},
                    "deleteBranch": {"type": "boolean"},
                },
                "required": ["path"],
            },
            "_meta": APP_ONLY_META,
        },
        {
            "name": "app_start_deploy",
            "title": "Start deploy",
            "description": "Pull main and run deploy.sh (optionally merging a PR, running tests, or codegen).",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "runTests": {"type": "boolean"},
                    "runCodegen": {"type": "boolean"},
                    "mergePrNumber": {"type": "integer"},
                    "mergeMethod": {"type": "string", "enum": ["squash", "merge", "rebase"]},
                    "deleteBranch": {"type": "boolean"},
                },
            },
            "_meta": APP_ONLY_META,
        },
        {
            "name": "app_poll_deploy",
            "title": "Poll deploy logs",
            "description": "Poll incremental deploy logs and status.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "jobId": {"type": "string"},
                    "offset": {"type": "integer"},
                },
            },
            "_meta": APP_ONLY_META,
        },
    ]


class McpServer:
    def __init__(self, ops: DeployOps, html_path: Path) -> None:
        self.ops = ops
        self.html_path = html_path
        self._write_lock = threading.Lock()
        self._pool = ThreadPoolExecutor(max_workers=4)

    def _send(self, resp: dict[str, Any]) -> None:
        line = json.dumps(resp) + "\n"
        with self._write_lock:
            sys.stdout.write(line)
            sys.stdout.flush()

    def _call_tool(self, name: str, args: dict[str, Any]) -> dict[str, Any]:
        if name == "open_deploy_dashboard":
            status = self.ops.get_status(fetch=bool(args.get("fetch", True)))
            summary_lines = [
                f"Repo: {status['repoPath']} (branch: {status['currentBranch']})",
                f"Deployed: {(status['deployed'] or {}).get('shortSha', 'never')}",
                f"origin/main: {(status['originMain'] or {}).get('shortSha', 'unknown')}",
                f"Open PRs: {len(status['openPrs'])} | Worktrees: {len(status['worktrees'])}",
            ]
            return {
                "content": [{"type": "text", "text": "\n".join(summary_lines)}],
                "structuredContent": status,
            }

        if name == "app_get_status":
            status = self.ops.get_status(fetch=bool(args.get("fetch", True)))
            return {
                "content": [{"type": "text", "text": "Status updated."}],
                "structuredContent": status,
            }

        if name == "app_merge_pr":
            res = self.ops.merge_pr(
                number=int(args["number"]),
                method=str(args.get("method", "squash")),
                delete_branch=bool(args.get("deleteBranch", True)),
            )
            return {
                "content": [{"type": "text", "text": res.get("output") or res.get("error") or ""}],
                "structuredContent": res,
                "isError": not res.get("ok", False),
            }

        if name == "app_close_pr":
            res = self.ops.close_pr(number=int(args["number"]))
            return {
                "content": [{"type": "text", "text": res.get("output") or res.get("error") or ""}],
                "structuredContent": res,
                "isError": not res.get("ok", False),
            }

        if name == "app_remove_worktree":
            res = self.ops.remove_worktree(
                path=str(args["path"]),
                force=bool(args.get("force", False)),
                delete_branch=bool(args.get("deleteBranch", False)),
            )
            return {
                "content": [{"type": "text", "text": res.get("output") or res.get("error") or ""}],
                "structuredContent": res,
                "isError": not res.get("ok", False),
            }

        if name == "app_start_deploy":
            merge_pr = args.get("mergePrNumber")
            res = self.ops.start_deploy(
                run_tests=bool(args.get("runTests", False)),
                run_codegen=bool(args.get("runCodegen", False)),
                merge_pr_number=int(merge_pr) if merge_pr is not None else None,
                merge_method=str(args.get("mergeMethod", "squash")),
                delete_branch=bool(args.get("deleteBranch", True)),
            )
            return {
                "content": [{"type": "text", "text": json.dumps(res)}],
                "structuredContent": res,
                "isError": not res.get("ok", False),
            }

        if name == "app_poll_deploy":
            res = self.ops.poll_deploy(
                job_id=args.get("jobId"),
                offset=int(args.get("offset", 0)),
            )
            return {
                "content": [{"type": "text", "text": res.get("phase", "")}],
                "structuredContent": res,
            }

        raise ValueError(f"Unknown tool: {name}")

    def handle_request(self, req: dict[str, Any]) -> dict[str, Any] | None:
        method = req.get("method")
        req_id = req.get("id")
        params = req.get("params") or {}

        # Notifications have no id and expect no response.
        if req_id is None:
            return None

        try:
            if method == "initialize":
                client_version = params.get("protocolVersion", PROTOCOL_VERSION)
                result = {
                    "protocolVersion": client_version,
                    "capabilities": {
                        "tools": {"listChanged": False},
                        "resources": {"subscribe": False, "listChanged": False},
                    },
                    "serverInfo": {
                        "name": "netdaemon-deploy",
                        "version": "1.0.0",
                    },
                }
            elif method == "ping":
                result = {}
            elif method == "tools/list":
                result = {"tools": _tool_definitions()}
            elif method == "tools/call":
                result = self._call_tool(
                    name=str(params.get("name", "")),
                    args=params.get("arguments") or {},
                )
            elif method == "resources/list":
                result = {
                    "resources": [
                        {
                            "uri": APP_URI,
                            "name": "NetDaemon Deploy Dashboard",
                            "description": "Interactive PR, Worktree, and Deploy dashboard.",
                            "mimeType": "text/html;profile=mcp-app",
                            "_meta": {"ui": {"prefersBorder": False}},
                        }
                    ]
                }
            elif method == "resources/read":
                uri = params.get("uri")
                if uri != APP_URI:
                    raise ValueError(f"Unknown resource URI: {uri}")
                html = self.html_path.read_text(encoding="utf-8")
                result = {
                    "contents": [
                        {
                            "uri": APP_URI,
                            "mimeType": "text/html;profile=mcp-app",
                            "text": html,
                            "_meta": {"ui": {"prefersBorder": False}},
                        }
                    ]
                }
            else:
                return {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "error": {"code": -32601, "message": f"Method not found: {method}"},
                }

            return {"jsonrpc": "2.0", "id": req_id, "result": result}
        except Exception as exc:
            return {
                "jsonrpc": "2.0",
                "id": req_id,
                "error": {"code": -32603, "message": str(exc)},
            }

    def serve_stdio(self) -> None:
        def _dispatch(req_obj: dict[str, Any]) -> None:
            resp = self.handle_request(req_obj)
            if resp is not None:
                self._send(resp)

        for raw_line in sys.stdin:
            line = raw_line.strip()
            if not line:
                continue
            try:
                req = json.loads(line)
            except json.JSONDecodeError as exc:
                self._send(
                    {
                        "jsonrpc": "2.0",
                        "id": None,
                        "error": {"code": -32700, "message": f"Parse error: {exc}"},
                    }
                )
                continue

            if req.get("method") == "tools/call":
                self._pool.submit(_dispatch, req)
            else:
                _dispatch(req)


def main() -> None:
    script_dir = Path(__file__).resolve().parent
    default_repo = script_dir.parent.parent
    default_data = Path.home() / ".local" / "share" / "netdaemon-deploy"

    parser = argparse.ArgumentParser(description="NetDaemon Deploy MCP Server")
    parser.add_argument("--repo", type=Path, default=default_repo)
    parser.add_argument("--data-dir", type=Path, default=default_data)
    parser.add_argument(
        "--app-html",
        type=Path,
        default=Path(os.environ.get("NETDAEMON_DEPLOY_APP_HTML", script_dir / "app.html")),
    )
    args = parser.parse_args()

    ops = DeployOps(repo_path=args.repo, data_dir=args.data_dir)
    server = McpServer(ops=ops, html_path=args.app_html)
    server.serve_stdio()


if __name__ == "__main__":
    main()
