#!/usr/bin/env python3
"""生成金丝雀测试基准值 vectors.json。

基准值直接执行 .refs/source-refs/ 下的原 Python 实现（stub 掉网络与内部依赖）
得到，保证 C# 实现与原实现逐字节一致。

注意：本脚本依赖本地 `.refs/`（不随仓库分发）；提交进仓库的
vectors.json 才是冻结产物，测试只读取该产物。
"""

import json
import sys
import types
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / ".refs" / "source-refs"


def load_reference(filename: str, extra_globals: dict | None = None) -> dict:
    """以 stub 依赖加载参照源，返回其模块命名空间。"""
    if "requests" not in sys.modules:
        requests = types.ModuleType("requests")
        requests.get = lambda *a, **k: None
        sys.modules["requests"] = requests

        backend = types.ModuleType("backend")
        backend.__path__ = []
        sys.modules["backend"] = backend

        data = types.ModuleType("backend.data")
        data.header = {}
        data.bullet_data = {}
        sys.modules["backend.data"] = data

        util = types.ModuleType("backend.util")
        sys.modules["backend.util"] = util

        get_wbi = types.ModuleType("backend.get_wbi")
        get_wbi.get_w_rid_and_wts = lambda d: ({}, "")
        sys.modules["backend.get_wbi"] = get_wbi

        sys.modules["backend.dm_pb2"] = types.ModuleType("backend.dm_pb2")

    namespace: dict = {"__name__": "reference"}
    source = (SRC / filename).read_text(encoding="utf-8")
    exec(compile(source, filename, "exec"), namespace)
    # 必须在 exec 之后覆盖，否则会被模块内的 import 语句重置
    if extra_globals:
        namespace.update(extra_globals)
    return namespace


class FixedTime:
    """将参照源中的 time.time() 固定为确定值。"""

    def __init__(self, ts: float) -> None:
        self._ts = ts

    def time(self) -> float:
        return self._ts


def main() -> None:
    # --- APP 签名基准 ---
    api_ns = load_reference("bilibili_api.py")
    api_cls = api_ns["BilibiliApi"]
    api = api_cls.__new__(api_cls)

    app_sign_cases = {
        "basic": {"room_id": "1", "ts": "1700000000"},
        # 含 URL 特殊字符与中文（无空格：Python quote_plus 与 C# EscapeDataString 在无空格时逐字节一致）
        "special": {"room_id": "42", "title": "测试&直播=问答?#1", "ts": "1700000001"},
    }
    app_sign = {
        name: {"input": values, "expected": api._appsign(dict(values))}
        for name, values in app_sign_cases.items()
    }

    # --- WBI 签名基准 ---
    WTS = 1700000000
    wbi_ns = load_reference("get_wbi.py", {"time": FixedTime(WTS)})
    img_key = "7cd084941338484aae1ad9425b84077c"
    sub_key = "4932caff0ff746eab6f01bf08b70ac45"

    wbi_cases = {
        # 对应 send_danmu 的调用点
        "webLocation": {"web_location": "444.8"},
        # 值中含 !'()*，验证过滤语义
        "filter": {"web_location": "444.8", "note": "a!b'c(d)e*f"},
        # 对应 getDanmuInfo 的调用点
        "danmuInfo": {"id": "12345", "type": "0"},
    }
    wbi = {
        name: {
            "input": values,
            "imgKey": img_key,
            "subKey": sub_key,
            "wts": WTS,
            "expected": wbi_ns["encWbi"](dict(values), img_key, sub_key),
        }
        for name, values in wbi_cases.items()
    }
    mixin_key = {
        "orig": img_key + sub_key,
        "expected": wbi_ns["getMixinKey"](img_key + sub_key),
    }

    output = {
        "_comment": "由 generate_vectors.py 从 .refs/source-refs 参照源执行生成，勿手改。",
        "appSign": app_sign,
        "mixinKey": mixin_key,
        "wbi": wbi,
    }
    out_path = Path(__file__).with_name("vectors.json")
    out_path.write_text(
        json.dumps(output, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"written: {out_path}")


if __name__ == "__main__":
    main()
