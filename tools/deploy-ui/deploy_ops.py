#!/usr/bin/env python3
"""Core Git, GitHub PR, worktree, and deploy operations for NetDaemon."""

from __future__ import annotations

import json
import os
import subprocess
import threading
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _run_cmd(
    args: list[str],
    cwd: Path,
    timeout: int = 30,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        args,
        cwd=str(cwd),
        capture_output=True,
        text=True,
        timeout=timeout,
        check=False,
    )


@dataclass
class DeployJob:
    job_id: str
    started_at: str
    running: bool = True
    success: bool | None = None
    phase: str = "Starting"
    finished_at: str | None = None
    error: str | None = None
    lines: list[str] = field(default_factory=list)


class DeployOps:
    def __init__(self, repo_path: Path, data_dir: Path) -> None:
        self.repo_path = repo_path.resolve()
        self.data_dir = data_dir.resolve()
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.state_file = self.data_dir / "state.json"

        self._lock = threading.Lock()
        self._current_job: DeployJob | None = None

    # ------------------------------------------------------------------
    # Persistent state
    # ------------------------------------------------------------------
    def _load_state(self) -> dict[str, Any]:
        if not self.state_file.exists():
            return {"deployed": None, "history": []}
        try:
            return json.loads(self.state_file.read_text(encoding="utf-8"))
        except Exception:
            return {"deployed": None, "history": []}

    def _save_state(self, state: dict[str, Any]) -> None:
        tmp = self.state_file.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(state, indent=2), encoding="utf-8")
        tmp.replace(self.state_file)

    def _record_deploy(
        self,
        sha: str,
        short_sha: str,
        subject: str,
        success: bool,
        error: str | None = None,
    ) -> None:
        state = self._load_state()
        entry = {
            "sha": sha,
            "shortSha": short_sha,
            "subject": subject,
            "timestamp": _utc_now(),
            "success": success,
            "error": error,
        }
        if success:
            state["deployed"] = entry
        history = state.get("history", [])
        history.insert(0, entry)
        state["history"] = history[:15]
        self._save_state(state)

    # ------------------------------------------------------------------
    # Git & GitHub queries
    # ------------------------------------------------------------------
    def _git_commit_info(self, ref: str) -> dict[str, Any] | None:
        res = _run_cmd(
            ["git", "log", "-1", "--format=%H%n%h%n%s%n%cI%n%an", ref],
            cwd=self.repo_path,
        )
        if res.returncode != 0:
            return None
        parts = res.stdout.strip().splitlines()
        if len(parts) < 5:
            return None
        return {
            "sha": parts[0],
            "shortSha": parts[1],
            "subject": parts[2],
            "date": parts[3],
            "author": parts[4],
        }

    def _git_commits_between(self, base_sha: str, target_ref: str) -> list[dict[str, str]]:
        res = _run_cmd(
            [
                "git",
                "log",
                "--format=%H%x1f%h%x1f%s%x1f%cI%x1f%an",
                f"{base_sha}..{target_ref}",
                "-n",
                "30",
            ],
            cwd=self.repo_path,
        )
        if res.returncode != 0:
            return []
        commits: list[dict[str, str]] = []
        for line in res.stdout.splitlines():
            parts = line.split("\x1f")
            if len(parts) == 5:
                commits.append(
                    {
                        "sha": parts[0],
                        "shortSha": parts[1],
                        "subject": parts[2],
                        "date": parts[3],
                        "author": parts[4],
                    }
                )
        return commits

    def _get_gh_auth(self) -> dict[str, Any]:
        res = _run_cmd(["gh", "auth", "status"], cwd=self.repo_path, timeout=10)
        msg = (res.stdout + "\n" + res.stderr).strip()
        return {
            "authenticated": res.returncode == 0,
            "message": msg.splitlines()[0] if msg else "",
        }

    def _get_prs(self, state: str, limit: int = 20) -> tuple[list[dict[str, Any]], str | None]:
        fields = (
            "number,title,headRefName,baseRefName,author,url,isDraft,"
            "mergeable,mergeStateStatus,additions,deletions,changedFiles,"
            "updatedAt,statusCheckRollup"
            if state == "open"
            else "number,title,headRefName,baseRefName,author,url,mergedAt,mergeCommit"
        )
        cmd = ["gh", "pr", "list", "--state", state, "--limit", str(limit), "--json", fields]
        if state == "merged":
            cmd.extend(["--base", "main"])
        res = _run_cmd(cmd, cwd=self.repo_path, timeout=20)
        if res.returncode != 0:
            return [], (res.stderr or res.stdout).strip()
        try:
            data = json.loads(res.stdout or "[]")
            return data, None
        except Exception as exc:
            return [], str(exc)

    def _get_worktrees(
        self,
        open_prs: list[dict[str, Any]],
        merged_prs: list[dict[str, Any]],
    ) -> list[dict[str, Any]]:
        res = _run_cmd(["git", "worktree", "list", "--porcelain"], cwd=self.repo_path)
        if res.returncode != 0:
            return []

        open_by_branch = {pr.get("headRefName"): pr for pr in open_prs}
        merged_by_branch = {pr.get("headRefName"): pr for pr in merged_prs}

        worktrees: list[dict[str, Any]] = []
        current: dict[str, Any] = {}
        for raw_line in res.stdout.splitlines() + [""]:
            line = raw_line.strip()
            if not line:
                if current.get("path"):
                    wt_path = Path(current["path"]).resolve()
                    branch = current.get("branch", "")
                    is_main = wt_path == self.repo_path
                    dirty = False
                    if wt_path.exists():
                        st = _run_cmd(["git", "status", "--porcelain"], cwd=wt_path, timeout=10)
                        dirty = bool(st.stdout.strip())
                    merged_into_main = False
                    if branch and branch != "main" and current.get("head"):
                        anc = _run_cmd(
                            ["git", "merge-base", "--is-ancestor", current["head"], "origin/main"],
                            cwd=self.repo_path,
                            timeout=10,
                        )
                        merged_into_main = anc.returncode == 0
                    open_pr = open_by_branch.get(branch)
                    merged_pr = merged_by_branch.get(branch)
                    worktrees.append(
                        {
                            "path": str(wt_path),
                            "name": wt_path.name,
                            "head": current.get("head", "")[:7],
                            "branch": branch or "(detached)",
                            "isMain": is_main,
                            "isDirty": dirty,
                            "mergedIntoMain": merged_into_main or bool(merged_pr),
                            "openPrNumber": open_pr["number"] if open_pr else None,
                            "mergedPrNumber": merged_pr["number"] if merged_pr else None,
                        }
                    )
                current = {}
                continue
            if line.startswith("worktree "):
                current["path"] = line[len("worktree ") :]
            elif line.startswith("HEAD "):
                current["head"] = line[len("HEAD ") :]
            elif line.startswith("branch refs/heads/"):
                current["branch"] = line[len("branch refs/heads/") :]

        return worktrees

    def get_status(self, fetch: bool = True) -> dict[str, Any]:
        # Run network-bound git fetch and gh queries in parallel so dashboard loads quickly.
        with ThreadPoolExecutor(max_workers=4) as pool:
            fetch_fut = (
                pool.submit(_run_cmd, ["git", "fetch", "origin", "main"], self.repo_path, 15)
                if fetch
                else None
            )
            gh_auth_fut = pool.submit(self._get_gh_auth)
            open_prs_fut = pool.submit(self._get_prs, "open", 25)
            merged_prs_fut = pool.submit(self._get_prs, "merged", 10)

            fetch_error: str | None = None
            if fetch_fut is not None:
                fr = fetch_fut.result()
                if fr.returncode != 0:
                    fetch_error = (fr.stderr or fr.stdout).strip()

            gh_auth = gh_auth_fut.result()
            if gh_auth["authenticated"]:
                open_prs, err1 = open_prs_fut.result()
                merged_prs, err2 = merged_prs_fut.result()
                pr_error = err1 or err2
            else:
                open_prs, merged_prs, pr_error = [], [], None

        branch_res = _run_cmd(["git", "branch", "--show-current"], cwd=self.repo_path)
        current_branch = branch_res.stdout.strip()

        status_res = _run_cmd(["git", "status", "--porcelain"], cwd=self.repo_path)
        dirty_files = [l for l in status_res.stdout.splitlines() if l.strip()]

        local_main = self._git_commit_info("main")
        origin_main = self._git_commit_info("origin/main")
        state = self._load_state()
        deployed = state.get("deployed")

        commits_behind_origin: list[dict[str, str]] = []
        if local_main and origin_main and local_main["sha"] != origin_main["sha"]:
            commits_behind_origin = self._git_commits_between(local_main["sha"], "origin/main")

        commits_since_deploy: list[dict[str, str]] = []
        needs_deploy = False
        if origin_main:
            if not deployed or not deployed.get("sha"):
                needs_deploy = True
            elif deployed["sha"] != origin_main["sha"]:
                needs_deploy = True
                commits_since_deploy = self._git_commits_between(deployed["sha"], "origin/main")

        worktrees = self._get_worktrees(open_prs, merged_prs)

        with self._lock:
            job = self._current_job
            job_summary = (
                {
                    "jobId": job.job_id,
                    "running": job.running,
                    "success": job.success,
                    "phase": job.phase,
                    "startedAt": job.started_at,
                    "finishedAt": job.finished_at,
                    "error": job.error,
                    "lineCount": len(job.lines),
                }
                if job
                else None
            )

        return {
            "repoPath": str(self.repo_path),
            "currentBranch": current_branch,
            "isDirty": bool(dirty_files),
            "dirtyFiles": dirty_files[:20],
            "localMain": local_main,
            "originMain": origin_main,
            "deployed": deployed,
            "needsDeploy": needs_deploy,
            "commitsBehindOrigin": commits_behind_origin,
            "commitsSinceDeploy": commits_since_deploy,
            "fetchError": fetch_error,
            "ghAuth": gh_auth,
            "prError": pr_error,
            "openPrs": open_prs,
            "mergedPrs": merged_prs,
            "worktrees": worktrees,
            "deployJob": job_summary,
            "history": state.get("history", [])[:5],
        }

    # ------------------------------------------------------------------
    # PR & worktree mutations
    # ------------------------------------------------------------------
    def merge_pr(
        self,
        number: int,
        method: str = "squash",
        delete_branch: bool = True,
    ) -> dict[str, Any]:
        if method not in ("squash", "merge", "rebase"):
            return {"ok": False, "error": f"Invalid merge method: {method}"}
        cmd = ["gh", "pr", "merge", str(number), f"--{method}"]
        if delete_branch:
            cmd.append("--delete-branch")
        res = _run_cmd(cmd, cwd=self.repo_path, timeout=30)
        return {
            "ok": res.returncode == 0,
            "output": (res.stdout + "\n" + res.stderr).strip(),
            "error": None if res.returncode == 0 else (res.stderr or res.stdout).strip(),
        }

    def close_pr(self, number: int) -> dict[str, Any]:
        res = _run_cmd(["gh", "pr", "close", str(number)], cwd=self.repo_path, timeout=20)
        return {
            "ok": res.returncode == 0,
            "output": (res.stdout + "\n" + res.stderr).strip(),
            "error": None if res.returncode == 0 else (res.stderr or res.stdout).strip(),
        }

    def remove_worktree(
        self,
        path: str,
        force: bool = False,
        delete_branch: bool = False,
    ) -> dict[str, Any]:
        target = Path(path).resolve()
        if target == self.repo_path:
            return {"ok": False, "error": "Cannot remove the primary main worktree."}

        # Verify target is actually a registered worktree of this repo
        wt_list = self._get_worktrees([], [])
        match = next((w for w in wt_list if Path(w["path"]).resolve() == target), None)
        if not match:
            return {"ok": False, "error": f"Not a registered worktree: {target}"}

        cmd = ["git", "worktree", "remove"]
        if force:
            cmd.append("--force")
        cmd.append(str(target))
        res = _run_cmd(cmd, cwd=self.repo_path, timeout=30)
        if res.returncode != 0:
            return {"ok": False, "error": (res.stderr or res.stdout).strip()}

        branch_msg = ""
        branch = match.get("branch", "")
        if delete_branch and branch and branch not in ("main", "(detached)"):
            flag = "-D" if force else "-d"
            br = _run_cmd(["git", "branch", flag, branch], cwd=self.repo_path, timeout=15)
            if br.returncode == 0:
                branch_msg = f" Deleted local branch '{branch}'."
            else:
                branch_msg = f" Worktree removed, but branch delete failed: {(br.stderr or br.stdout).strip()}"

        return {"ok": True, "output": f"Removed worktree {target.name}.{branch_msg}"}

    # ------------------------------------------------------------------
    # Async streaming deploy pipeline
    # ------------------------------------------------------------------
    def start_deploy(
        self,
        run_tests: bool = False,
        run_codegen: bool = False,
        merge_pr_number: int | None = None,
        merge_method: str = "squash",
        delete_branch: bool = True,
    ) -> dict[str, Any]:
        with self._lock:
            if self._current_job and self._current_job.running:
                return {
                    "ok": False,
                    "error": "A deploy is already in progress.",
                    "jobId": self._current_job.job_id,
                }
            job = DeployJob(job_id=uuid.uuid4().hex[:10], started_at=_utc_now())
            self._current_job = job

        thread = threading.Thread(
            target=self._run_deploy_pipeline,
            args=(job, run_tests, run_codegen, merge_pr_number, merge_method, delete_branch),
            daemon=True,
        )
        thread.start()
        return {"ok": True, "jobId": job.job_id}

    def _append_log(self, job: DeployJob, line: str) -> None:
        with self._lock:
            job.lines.append(line)

    def _set_phase(self, job: DeployJob, phase: str) -> None:
        with self._lock:
            job.phase = phase
            job.lines.append(f"\n==> {phase}")

    def _stream_cmd(self, job: DeployJob, cmd: list[str]) -> int:
        self._append_log(job, f"$ {' '.join(cmd)}")
        proc = subprocess.Popen(
            cmd,
            cwd=str(self.repo_path),
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
            env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        assert proc.stdout is not None
        for line in proc.stdout:
            self._append_log(job, line.rstrip("\n"))
        return proc.wait()

    def _run_deploy_pipeline(
        self,
        job: DeployJob,
        run_tests: bool,
        run_codegen: bool,
        merge_pr_number: int | None,
        merge_method: str,
        delete_branch: bool,
    ) -> None:
        try:
            if merge_pr_number is not None:
                if merge_method not in ("squash", "merge", "rebase"):
                    raise RuntimeError(f"Invalid merge method: {merge_method}")
                self._set_phase(job, f"Merging PR #{merge_pr_number} ({merge_method})")
                cmd = ["gh", "pr", "merge", str(merge_pr_number), f"--{merge_method}"]
                if delete_branch:
                    cmd.append("--delete-branch")
                if self._stream_cmd(job, cmd) != 0:
                    raise RuntimeError(f"Failed to merge PR #{merge_pr_number}")

            self._set_phase(job, "Checking local main worktree")
            branch_res = _run_cmd(["git", "branch", "--show-current"], cwd=self.repo_path)
            branch = branch_res.stdout.strip()
            if branch != "main":
                raise RuntimeError(
                    f"Primary repo is on branch '{branch}', expected 'main'. Switch to main first."
                )

            dirty_res = _run_cmd(["git", "status", "--porcelain"], cwd=self.repo_path)
            if dirty_res.stdout.strip():
                raise RuntimeError(
                    "Primary repo has uncommitted changes. Commit or stash them before deploying."
                )
            self._append_log(job, "Working tree on 'main' is clean.")

            self._set_phase(job, "Updating local main from origin/main")
            if self._stream_cmd(job, ["git", "fetch", "origin", "main"]) != 0:
                raise RuntimeError("git fetch origin main failed")
            if self._stream_cmd(job, ["git", "pull", "--ff-only", "origin", "main"]) != 0:
                raise RuntimeError("git pull --ff-only origin main failed")

            if run_codegen:
                self._set_phase(job, "Running codegen.sh")
                if self._stream_cmd(job, ["./codegen.sh"]) != 0:
                    raise RuntimeError("./codegen.sh failed")

            if run_tests:
                self._set_phase(job, "Running dotnet test")
                if self._stream_cmd(job, ["dotnet", "test"]) != 0:
                    raise RuntimeError("dotnet test failed")

            self._set_phase(job, "Running deploy.sh")
            if self._stream_cmd(job, ["./deploy.sh"]) != 0:
                raise RuntimeError("./deploy.sh failed")

            info = self._git_commit_info("HEAD") or {
                "sha": "unknown",
                "shortSha": "unknown",
                "subject": "",
            }
            self._record_deploy(info["sha"], info["shortSha"], info["subject"], success=True)
            self._set_phase(job, f"Deploy succeeded ({info['shortSha']})")
            with self._lock:
                job.success = True
        except Exception as exc:
            err = str(exc)
            self._append_log(job, f"\n❌ ERROR: {err}")
            info = self._git_commit_info("HEAD") or {
                "sha": "unknown",
                "shortSha": "unknown",
                "subject": "",
            }
            self._record_deploy(
                info["sha"], info["shortSha"], info["subject"], success=False, error=err
            )
            with self._lock:
                job.success = False
                job.error = err
                job.phase = "Failed"
        finally:
            with self._lock:
                job.running = False
                job.finished_at = _utc_now()

    def poll_deploy(self, job_id: str | None = None, offset: int = 0) -> dict[str, Any]:
        with self._lock:
            job = self._current_job
            if not job or (job_id and job.job_id != job_id):
                return {"found": False, "running": False, "lines": [], "nextOffset": 0}
            safe_offset = max(0, min(offset, len(job.lines)))
            return {
                "found": True,
                "jobId": job.job_id,
                "running": job.running,
                "success": job.success,
                "phase": job.phase,
                "startedAt": job.started_at,
                "finishedAt": job.finished_at,
                "error": job.error,
                "lines": job.lines[safe_offset:],
                "nextOffset": len(job.lines),
            }
