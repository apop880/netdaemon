#!/usr/bin/env python3
"""Optional standalone localhost web server for the NetDaemon Deploy UI."""

from __future__ import annotations

import argparse
import json
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse

from deploy_ops import DeployOps


def make_handler(ops: DeployOps, html_path: Path) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: Any) -> None:
            pass

        def _send_json(self, payload: dict[str, Any], status: int = 200) -> None:
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _read_json(self) -> dict[str, Any]:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0:
                return {}
            return json.loads(self.rfile.read(length).decode("utf-8"))

        def do_GET(self) -> None:
            parsed = urlparse(self.path)
            if parsed.path in ("/", "/index.html"):
                html = html_path.read_text(encoding="utf-8").encode("utf-8")
                self.send_response(HTTPStatus.OK)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(html)))
                self.end_headers()
                self.wfile.write(html)
                return

            if parsed.path == "/api/status":
                qs = parse_qs(parsed.query)
                fetch = qs.get("fetch", ["true"])[0].lower() != "false"
                self._send_json(ops.get_status(fetch=fetch))
                return

            if parsed.path == "/api/deploy/poll":
                qs = parse_qs(parsed.query)
                job_id = qs.get("jobId", [None])[0]
                offset = int(qs.get("offset", ["0"])[0])
                self._send_json(ops.poll_deploy(job_id=job_id, offset=offset))
                return

            self._send_json({"error": "Not found"}, status=404)

        def do_POST(self) -> None:
            parsed = urlparse(self.path)
            body = self._read_json()

            if parsed.path == "/api/pr/merge":
                res = ops.merge_pr(
                    number=int(body["number"]),
                    method=str(body.get("method", "squash")),
                    delete_branch=bool(body.get("deleteBranch", True)),
                )
                self._send_json(res, status=200 if res.get("ok") else 400)
                return

            if parsed.path == "/api/pr/close":
                res = ops.close_pr(number=int(body["number"]))
                self._send_json(res, status=200 if res.get("ok") else 400)
                return

            if parsed.path == "/api/worktree/remove":
                res = ops.remove_worktree(
                    path=str(body["path"]),
                    force=bool(body.get("force", False)),
                    delete_branch=bool(body.get("deleteBranch", False)),
                )
                self._send_json(res, status=200 if res.get("ok") else 400)
                return

            if parsed.path == "/api/deploy/start":
                merge_pr = body.get("mergePrNumber")
                res = ops.start_deploy(
                    run_tests=bool(body.get("runTests", False)),
                    run_codegen=bool(body.get("runCodegen", False)),
                    merge_pr_number=int(merge_pr) if merge_pr is not None else None,
                    merge_method=str(body.get("mergeMethod", "squash")),
                    delete_branch=bool(body.get("deleteBranch", True)),
                )
                self._send_json(res, status=200 if res.get("ok") else 409)
                return

            self._send_json({"error": "Not found"}, status=404)

    return Handler


def main() -> None:
    script_dir = Path(__file__).resolve().parent
    default_repo = script_dir.parent.parent
    default_data = Path.home() / ".local" / "share" / "netdaemon-deploy"

    parser = argparse.ArgumentParser(description="Standalone NetDaemon Deploy UI")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--repo", type=Path, default=default_repo)
    parser.add_argument("--data-dir", type=Path, default=default_data)
    args = parser.parse_args()

    ops = DeployOps(repo_path=args.repo, data_dir=args.data_dir)
    server = ThreadingHTTPServer((args.host, args.port), make_handler(ops, script_dir / "app.html"))
    print(f"NetDaemon Deploy UI running at http://{args.host}:{args.port}")
    server.serve_forever()


if __name__ == "__main__":
    main()
