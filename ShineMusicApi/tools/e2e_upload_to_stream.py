# -*- coding: utf-8 -*-
"""
ShineMusic 全链 E2E：上传 → 转码 → 审核 → 发布 → 音频流

前置：API 已以 media 模式启动（Worker__Mode=media），PostgreSQL / RabbitMQ 可用。
凭据只从环境变量读取，绝不写入文件、不打印。

退出码：0 = 全链通过；1 = 有失败；2 = 缺少凭据。
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request
import http.client
import mimetypes
import uuid

BASE = os.environ.get("SMOKE_BASE", "http://127.0.0.1:5180")
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

RESULTS = []


class Step:
    def __init__(self, name, method, path, expected, actual, ok, note=""):
        self.name = name
        self.method = method
        self.path = path
        self.expected = expected
        self.actual = actual
        self.ok = ok
        self.note = note


def api(method, path, body=None, token=None, timeout=30):
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with OPENER.open(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")
    except Exception as e:
        return 0, str(e)


def jbody(raw):
    try:
        return json.loads(raw)
    except (ValueError, TypeError):
        return None


def record(name, method, path, expected, status, raw, acceptable, note=""):
    ok = status in acceptable
    if status >= 500:
        ok = False
        note = "服务端 5xx"
    RESULTS.append(Step(name, method, path, expected, status, ok, note))
    return ok


def upload_multipart(path, filepath, token=None, field_name="files"):
    """上传文件用 multipart/form-data"""
    boundary = f"----SmokeBoundary{uuid.uuid4().hex[:16]}"
    filename = os.path.basename(filepath)
    content_type = mimetypes.guess_type(filepath)[0] or "application/octet-stream"

    with open(filepath, "rb") as f:
        file_data = f.read()

    body = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="{field_name}"; filename="{filename}"\r\n'
        f"Content-Type: {content_type}\r\n\r\n"
    ).encode("utf-8") + file_data + f"\r\n--{boundary}--\r\n".encode("utf-8")

    headers = {
        "Content-Type": f"multipart/form-data; boundary={boundary}",
        "Accept": "application/json",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    req = urllib.request.Request(BASE + path, data=body, headers=headers, method="POST")
    try:
        with OPENER.open(req, timeout=60) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")
    except Exception as e:
        return 0, str(e)


def login():
    username = os.environ.get("SMOKE_ADMIN_USERNAME", "")
    password = os.environ.get("SMOKE_ADMIN_PASSWORD", "")
    if not username or not password:
        return None
    status, raw = api("POST", "/api/v1/auth/login", {"username": username, "password": password})
    body = jbody(raw) or {}
    if status == 200 and body.get("accessToken"):
        return body["accessToken"]
    return None


def wait_for_status(item_id, token, target_statuses, timeout_s=120, poll_interval=3):
    """轮询导入条目状态直到达到目标状态或超时"""
    deadline = time.time() + timeout_s
    last_status = None
    while time.time() < deadline:
        status, raw = api("GET", f"/api/v1/admin/library/import-items/{item_id}", token=token)
        if status == 200:
            body = jbody(raw) or {}
            last_status = body.get("status", "")
            if last_status in target_statuses:
                return last_status, body
        time.sleep(poll_interval)
    return last_status, None


def main():
    print(f"\n全链 E2E：{BASE}")
    print("=" * 96)

    # 1) 健康检查
    status, raw = api("GET", "/health")
    record("健康检查", "GET", "/health", "200", status, raw, {200})

    # 2) 登录
    token = login()
    if token:
        record("管理员登录", "POST", "/api/v1/auth/login", "200", 200, "", {200})
    else:
        record("管理员登录", "POST", "/api/v1/auth/login", "200", 0, "无凭据或登录失败", set())
        print("缺少凭据，设置 SMOKE_ADMIN_USERNAME / SMOKE_ADMIN_PASSWORD 后重试")
        return 2

    # 3) 上传测试音频
    test_audio = os.environ.get("SMOKE_TEST_AUDIO", "")
    if not test_audio:
        # 默认路径
        for candidate in [
            os.path.join(os.path.dirname(__file__), "..", "..", "logs", "e2e-smoke.flac"),
            "C:/Learn/ShineMusic/logs/e2e-smoke.flac",
        ]:
            if os.path.isfile(candidate):
                test_audio = candidate
                break
    if not test_audio or not os.path.isfile(test_audio):
        record("上传测试音频", "POST", "/api/v1/me/uploads", "202", 0, f"测试音频不存在: {test_audio}", set())
        print(f"测试音频不存在: {test_audio}")
        _print_results()
        return 1

    print(f"\n测试音频: {test_audio} ({os.path.getsize(test_audio)} bytes)")
    status, raw = upload_multipart("/api/v1/me/uploads", test_audio, token=token)
    record("上传测试音频", "POST", "/api/v1/me/uploads", "202", status, raw, {202})
    body = jbody(raw) or {}
    queued = body.get("queued", [])
    if not queued:
        RESULTS[-1].ok = False
        RESULTS[-1].note = f"响应中无 queued 条目，raw={raw[:200]}"
        _print_results()
        return 1

    item_id = queued[0].get("itemId") or queued[0].get("item_id")
    batch_id = body.get("batchId") or body.get("batch_id")
    print(f"上传成功: itemId={item_id}, batchId={batch_id}")

    # 4) 轮询等待转码完成
    # 用户上传流程: Uploaded → (转码) → PendingPublication（等待管理员审核发布）
    print(f"\n等待转码完成（最多 120s）...")
    final_status, item_body = wait_for_status(
        item_id, token,
        target_statuses={"pending_publication", "processing_failed", "ready_to_publish"},
        timeout_s=120,
        poll_interval=3
    )
    record("转码完成轮询", "POLL", f"/api/v1/admin/library/import-items/{item_id}",
           "pending_publication", 200 if final_status else 0,
           f"最终状态: {final_status}",
           {200} if final_status == "pending_publication" else set())

    if final_status != "pending_publication":
        RESULTS[-1].note = f"转码未到 pending_publication，实际: {final_status}"
        # 即使失败也继续，看能否从管理端获取到信息
        if not item_body:
            _print_results()
            return 1

    # 5) 管理员审核：PATCH 元数据（标题/艺人）
    if item_body:
        patch_body = {
            "title": item_body.get("title") or "E2E Smoke Test",
            "artists": item_body.get("artists") or ["Smoke Artist"],
        }
        # 确保有艺人字段
        if not patch_body["artists"]:
            patch_body["artists"] = ["Smoke Artist"]
        status, raw = api("PATCH",
                          f"/api/v1/admin/library/import-items/{item_id}",
                          patch_body, token=token)
        record("管理员审核(PATCH元数据)", "PATCH",
               f"/api/v1/admin/library/import-items/{item_id}",
               "200", status, raw, {200, 409})

    # 6) 发布
    status, raw = api("POST",
                      f"/api/v1/admin/library/import-items/{item_id}/publish",
                      None, token=token)
    record("发布", "POST",
           f"/api/v1/admin/library/import-items/{item_id}/publish",
           "200", status, raw, {200, 409})
    pub_body = jbody(raw) or {}
    track_id = pub_body.get("trackId") or pub_body.get("track_id")
    print(f"发布结果: status={status}, trackId={track_id}")

    # 7) 验证曲库有数据
    status, raw = api("GET", "/api/v1/tracks?page=1&pageSize=5")
    record("曲库查询", "GET", "/api/v1/tracks", "200 + total>0", status, raw, {200})
    body = jbody(raw) or {}
    if status == 200 and isinstance(body, dict):
        total = body.get("total", 0)
        if total == 0:
            RESULTS[-1].ok = False
            RESULTS[-1].note = "曲库仍为空"

    # 8) 音频流验证（Range 请求）
    if track_id:
        # 先试不带 Range
        status, raw = _stream_request(f"/api/v1/tracks/{track_id}/stream", token=token)
        record("音频流(无Range)", "GET",
               f"/api/v1/tracks/{track_id}/stream",
               "200/206", status, f"{len(raw)} bytes", {200, 206})

        # 带 Range 请求前 16 字节
        status, raw = _stream_request(f"/api/v1/tracks/{track_id}/stream", token=token, range_header="bytes=0-15")
        record("音频流(Range 0-15)", "GET",
               f"/api/v1/tracks/{track_id}/stream",
               "206", status, f"{len(raw)} bytes", {206, 200})

    _print_results()
    failed = sum(1 for r in RESULTS if not r.ok)
    return 0 if failed == 0 else 1


def _stream_request(path, token=None, range_header=None):
    headers = {"Accept": "audio/*"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if range_header:
        headers["Range"] = f"bytes={range_header}"
    req = urllib.request.Request(BASE + path, headers=headers, method="GET")
    try:
        with OPENER.open(req, timeout=30) as resp:
            data = resp.read()
            return resp.status, data
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:
        return 0, str(e).encode("utf-8", errors="replace")


def _print_results():
    print("\n" + "=" * 96)
    print(f"{'步骤':<24}{'方法':<8}{'期望':<22}{'实际':<8}{'结论':<8}路径")
    print("-" * 96)
    for item in RESULTS:
        verdict = "PASS" if item.ok else "FAIL"
        print(f"{item.name:<24}{item.method:<8}{item.expected:<22}{item.actual:<8}{verdict:<8}{item.path}")
        if item.note:
            print(f"{'':<64}↳ {item.note}")
    passed = sum(1 for item in RESULTS if item.ok)
    failed = len(RESULTS) - passed
    print("-" * 96)
    print(f"合计 {len(RESULTS)} 步：通过 {passed} / 失败 {failed}")


if __name__ == "__main__":
    sys.exit(main())
