"""Loopback-only API fixture. Never contacts a model provider or uses real keys."""
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def send_json(self, status, data):
        raw = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def event(self, obj, name=None):
        if name:
            self.wfile.write(("event: " + name + "\n").encode())
        data = obj if isinstance(obj, str) else json.dumps(obj, ensure_ascii=False)
        self.wfile.write(("data: " + data + "\n\n").encode("utf-8"))
        self.wfile.flush()

    def do_GET(self):
        if self.headers.get("Authorization") != "Bearer local-test-key":
            self.send_json(401, {"error": {"message": "invalid fixture key"}})
            return
        if self.path == "/v1/models":
            self.send_json(200, {"object": "list", "data": [{"id": "mock-translator"}, {"id": "mock-alternative"}]})
        else:
            self.send_json(404, {"error": {"message": "fixture route not found"}})

    def do_POST(self):
        try:
            self.handle_request()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            pass

    def handle_request(self):
        if self.path not in ("/v1/chat/completions", "/v1/responses"):
            self.send_json(404, {"error": {"message": "fixture route not found"}})
            return
        data = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        model = data["model"]
        assert self.headers.get("Authorization") == "Bearer local-test-key"
        if "messages" in data:
            assert len(data["messages"]) == 2
            assert data["messages"][0]["role"] == "system"
            assert data["messages"][1]["role"] == "user"
        else:
            assert data["store"] is False
        if model in ("error401", "error429"):
            self.send_json(int(model[5:]), {"error": {"message": "test error local-test-key"}})
            return
        if model == "redirect":
            self.send_response(302)
            self.send_header("Location", "/redirect-must-not-be-followed")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if model == "json-chat":
            self.send_json(200, {"choices": [{"message": {"content": "你好"}, "finish_reason": "stop"}], "usage": {"prompt_tokens": 12, "completion_tokens": 2}})
            return
        if model == "wrapped-chat":
            self.send_json(200, {"success": True, "data": {"choices": [{"message": {"content": "封装译文"}, "finish_reason": "stop"}], "usage": {"prompt_tokens": 20, "completion_tokens": 8, "completion_tokens_details": {"reasoning_tokens": 5}}}})
            return
        if model == "json-responses":
            self.send_json(200, {"status": "completed", "output": [{"type": "reasoning"}, {"type": "message", "content": [{"type": "output_text", "text": "完整译文"}]}], "usage": {"input_tokens": 11, "output_tokens": 4}})
            return
        if model == "headers-slow":
            time.sleep(2)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        if model == "slow":
            self.wfile.write(b": keepalive\n\n")
            self.wfile.flush()
            time.sleep(3)
            return
        if model == "malformed":
            self.event("{oops}")
            return
        if model == "stream-error":
            self.event({"error": {"message": "test stream failure"}})
            return
        if model == "empty":
            self.event("[DONE]")
            return
        if model == "refusal":
            self.event({"choices": [{"index": 0, "delta": {"refusal": "refused"}}]})
            self.event("[DONE]")
            return
        if model in ("stream-responses", "incomplete-responses"):
            text = "你好，世界。" if model == "stream-responses" else "部分响应"
            for part in (text[:2], text[2:]):
                self.event({"type": "response.output_text.delta", "delta": part}, "response.output_text.delta")
            ending = "response.completed" if model == "stream-responses" else "response.incomplete"
            self.event({"type": ending, "response": {"status": "completed" if model == "stream-responses" else "incomplete", "output": [{"type": "message", "content": [{"type": "output_text", "text": text}]}], "usage": {"input_tokens": 33, "output_tokens": 6}}}, ending)
            return
        if model == "multiline-sse":
            self.wfile.write(b': comment\r\nid: 1\r\nevent: message\r\ndata: {"choices":\r\ndata: [{"index":0,"delta":{"content":"\\u4f60\\u597d"}}]}\r\n\r\n')
            self.event("[DONE]")
            return
        text = "模型从数据中学习。"
        if model == "truncated":
            text = "部分"
        elif model in ("no-usage", "headers-slow"):
            text = "你好"
        elif model in ("mock-translator", "mock-alternative"):
            original = data.get("messages", [{}, {"content": data.get("input", "")}])[1]["content"]
            if "Look up" in data["messages"][0]["content"]:
                text = original + "\n英 /ˈlɜːnɪŋ/  美 /ˈlɝːnɪŋ/\nn. 学习；知识\nv. 学习（learn 的现在分词）"
            else:
                text = "备用模型完成翻译。" if model == "mock-alternative" else "模型从数据中学习，并逐步改善预测结果。"
        for i in range(0, len(text), 3):
            self.event({"choices": [{"index": 0, "delta": {"content": text[i:i+3]}, "finish_reason": None}]})
            if model == "mock-translator":
                time.sleep(0.04)
        if model == "broken":
            return
        self.event({"choices": [{"index": 0, "delta": {}, "finish_reason": "length" if model == "truncated" else "error" if model == "finish-error" else "stop"}]})
        if model != "no-usage":
            self.event({"choices": [], "usage": {"prompt_tokens": 123, "completion_tokens": 12}})
        self.event("[DONE]")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port-file", required=True)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    Path(args.port_file).write_text(str(server.server_port), encoding="ascii")
    print(f"Local fixture listening on 127.0.0.1:{server.server_port}", flush=True)
    server.serve_forever()
