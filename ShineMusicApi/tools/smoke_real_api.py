# -*- coding: utf-8 -*-
"""
ShineMusic 真机 API 业务链路回归（连真实 PostgreSQL / RabbitMQ、真实启动的 API）。

与 dotnet test 里的纯逻辑单测互补：这里按真实 HTTP 顺序验证
「健康检查 → 登录 → 鉴权 → 管理端业务查询 → 歌单写读删 → 上传预检 → 曲库」。

用法：
  SMOKE_ADMIN_USERNAME=admin SMOKE_ADMIN_PASSWORD=<密码> \
      python ShineMusicApi/tools/smoke_real_api.py [--base http://127.0.0.1:5180]

凭据只从环境变量读取，绝不写入文件、不打印；响应里的 token 也不打印。
系统代理会干扰本机回环，脚本强制 ProxyHandler({}) 直连。

退出码：0 = 核心断言全过且无 5xx；1 = 有失败；2 = 缺少凭据（只跑匿名部分）。
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5180"
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class Result:
    def __init__(self, name, method, path, expected, actual, ok, note=""):
        self.name = name
        self.method = method
        self.path = path
        self.expected = expected
        self.actual = actual
        self.ok = ok
        self.note = note


RESULTS = []


def call(method, path, body=None, token=None, timeout=20):
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with OPENER.open(request, timeout=timeout) as response:
            raw = response.read().decode("utf-8", errors="replace")
            return response.status, raw
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", errors="replace")
    except Exception as error:  # 连接失败 / 超时
        return 0, str(error)


def json_body(raw):
    try:
        return json.loads(raw)
    except (ValueError, TypeError):
        return None


def record(name, method, path, expected, status, raw, acceptable, note=""):
    ok = status in acceptable
    RESULTS.append(Result(name, method, path, expected, status, ok, note))
    if status >= 500:
        RESULTS[-1].ok = False
        RESULTS[-1].note = "服务端 5xx，必须排查"
    return ok


def find_id(payload):
    if not isinstance(payload, dict):
        return None
    for key in ("id", "playlistId", "playlist_id"):
        if isinstance(payload.get(key), str) and payload[key]:
            return payload[key]
    for container in ("data", "item", "playlist"):
        nested = payload.get(container)
        if isinstance(nested, dict):
            found = find_id(nested)
            if found:
                return found
    return None


def main():
    global BASE
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default=BASE)
    args = parser.parse_args()
    BASE = args.base.rstrip("/")

    username = os.environ.get("SMOKE_ADMIN_USERNAME", "")
    password = os.environ.get("SMOKE_ADMIN_PASSWORD", "")
    have_credentials = bool(username and password)

    # 1) 健康检查
    status, raw = call("GET", "/health")
    record("健康检查", "GET", "/health", "200 + status=ok", status, raw, {200})
    payload = json_body(raw) or {}
    if not (status == 200 and payload.get("status") == "ok"):
        RESULTS[-1].ok = False
        RESULTS[-1].note = "响应体缺少 status=ok"

    # 2) 匿名访问管理端点必须被拒
    status, raw = call("GET", "/api/v1/admin/library/import-items?page=1&pageSize=1")
    record("匿名访问管理端点被拒", "GET", "/api/v1/admin/library/import-items",
           "401/403", status, raw, {401, 403})

    token = None
    if have_credentials:
        # 3) 正确凭据登录
        status, raw = call("POST", "/api/v1/auth/login",
                           {"username": username, "password": password})
        record("管理员登录", "POST", "/api/v1/auth/login", "200 + accessToken", status, raw, {200})
        body = json_body(raw) or {}
        token = body.get("accessToken") if isinstance(body, dict) else None
        if not token:
            RESULTS[-1].ok = False
            RESULTS[-1].note = "未取得 accessToken"

        # 4) 错误密码必须 401（反例，证明登录校验真的生效）
        status, raw = call("POST", "/api/v1/auth/login",
                           {"username": username, "password": "definitely-wrong-" + "x" * 8})
        record("错误密码被拒（反例）", "POST", "/api/v1/auth/login", "401", status, raw, {401})

        # 5) 管理端业务查询
        admin_reads = [
            ("导入条目列表", "/api/v1/admin/library/import-items?page=1&pageSize=5"),
            ("导入批次列表", "/api/v1/admin/library/imports?page=1&pageSize=5"),
            ("音源任务列表", "/api/v1/admin/source-downloads/tasks?page=1&pageSize=5"),
            ("未匹配清单", "/api/v1/admin/source-downloads/unmatched?page=1&pageSize=5"),
            ("消息队列监控", "/api/v1/admin/mq/queues"),
        ]
        for name, path in admin_reads:
            status, raw = call("GET", path, token=token)
            record(name, "GET", path, "200 或非 5xx", status, raw,
                   {200, 400, 401, 403, 404, 409, 429, 502, 503} if name == "消息队列监控" else {200},
                   "依赖型端点允许降级" if name == "消息队列监控" else "")

        # 6) 歌单写 → 读 → 删闭环（验证真实数据库写入）
        playlist_name = "E2E-Smoke-Playlist"
        status, raw = call("POST", "/api/v1/me/playlists",
                           {"name": playlist_name, "description": "smoke test"}, token=token)
        record("创建歌单", "POST", "/api/v1/me/playlists", "200/201", status, raw, {200, 201})
        created_id = find_id(json_body(raw))
        if not created_id:
            RESULTS[-1].ok = False
            RESULTS[-1].note = "响应中找不到歌单 Id，无法继续读删闭环"

        if created_id:
            status, raw = call("GET", "/api/v1/me/playlists?page=1&pageSize=20", token=token)
            listed = json_body(raw)
            found = playlist_name in raw
            record("歌单列表可读回新建项", "GET", "/api/v1/me/playlists",
                   "200 且包含新建歌单", status, raw, {200})
            if not found:
                RESULTS[-1].ok = False
                RESULTS[-1].note = "新建歌单未出现在列表"

            status, raw = call("DELETE", f"/api/v1/me/playlists/{created_id}", token=token)
            record("清理测试歌单", "DELETE", f"/api/v1/me/playlists/{created_id}",
                   "200/204", status, raw, {200, 204})

        # 7) 上传预检（空批次，验证端点与模型绑定）
        status, raw = call("POST", "/api/v1/me/uploads/precheck", {"files": []}, token=token)
        record("上传预检", "POST", "/api/v1/me/uploads/precheck", "200/400", status, raw, {200, 400})

        # 8) 管理端音源搜索（#32 收口后的首次真机调用；允许外网超时/降级）
        status, raw = call("GET", "/api/v1/admin/source-downloads/search?query=%E6%99%B4%E5%A4%A9&limit=3",
                           token=token, timeout=35)
        record("管理端聚合搜索", "GET", "/api/v1/admin/source-downloads/search",
               "200 或非 5xx", status, raw, {200, 400, 401, 403, 404, 429, 502, 503, 504},
               "依赖外部音源，允许降级")

    # 9) 曲库查询（真实连库）
    status, raw = call("GET", "/api/v1/tracks?page=1&pageSize=5")
    record("曲库分页查询", "GET", "/api/v1/tracks", "200 + total", status, raw, {200})
    body = json_body(raw) or {}
    if not (status == 200 and isinstance(body, dict) and "total" in body):
        RESULTS[-1].ok = False
        RESULTS[-1].note = "缺少分页 total 字段"

    print(f"\n真机 API 回归：{BASE}")
    print("-" * 96)
    print(f"{'步骤':<26}{'方法':<8}{'期望':<22}{'实际':<8}{'结论':<8}路径")
    print("-" * 96)
    for item in RESULTS:
        verdict = "PASS" if item.ok else "FAIL"
        print(f"{item.name:<26}{item.method:<8}{item.expected:<22}{item.actual:<8}{verdict:<8}{item.path}")
        if item.note:
            print(f"{'':<64}↳ {item.note}")
    passed = sum(1 for item in RESULTS if item.ok)
    failed = len(RESULTS) - passed
    server_errors = [item for item in RESULTS if item.actual >= 500]
    print("-" * 96)
    print(f"合计 {len(RESULTS)} 步：通过 {passed} / 失败 {failed}；5xx {len(server_errors)} 处")
    if not have_credentials:
        print("提示：未设置 SMOKE_ADMIN_USERNAME / SMOKE_ADMIN_PASSWORD，已跳过全部鉴权后步骤。")
        return 2
    return 0 if failed == 0 and not server_errors else 1


if __name__ == "__main__":
    sys.exit(main())
