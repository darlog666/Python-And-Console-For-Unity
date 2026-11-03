import sys
import json
import socket
import threading
import contextlib
import io
import traceback

COMMAND_PREFIX = "unity-internal-command:"


def emit(kind, data, ident=0):
    payload = {"evt": kind, "id": ident, "data": str(data)}
    sys.__stdout__.write(json.dumps(payload) + "\n")
    sys.__stdout__.flush()


def connect_ipc(port):
    try:
        channel = socket.create_connection(("127.0.0.1", port), timeout=5)
        channel.sendall(b'{"evt":"ready","id":0,"data":"ipc-handshake"}\n')
        channel.close()
    except Exception:
        pass


def read_port():
    if "--ipc-port" in sys.argv:
        return int(sys.argv[sys.argv.index("--ipc-port") + 1])
    return int(__import__("os").environ.get("UNITY_IPC_PORT", "0"))


def run_command(src, ident, scope):
    token = src.split(":", 1)[1]
    for line in token.split("\n"):
        stripped = line.strip()
        if not stripped:
            continue
        name, _, value = stripped.partition("=")
        scope[name.strip()] = eval(value.strip(), scope)
    emit("out", f"unity command applied: {token}", ident)


def main():
    scope = {"__name__": "__unity_terminal__"}
    port = read_port()

    if port:
        threading.Thread(target=connect_ipc, args=(port,), daemon=True).start()

    emit("ready", "bootstrap-online")

    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue

        try:
            frame = json.loads(raw)
        except ValueError:
            emit("err", f"malformed frame: {raw}")
            continue

        ident = frame.get("id", 0)
        src = frame.get("src", "")

        if src.startswith(COMMAND_PREFIX):
            run_command(src, ident, scope)
            continue

        capture = io.StringIO()
        try:
            with contextlib.redirect_stdout(capture):
                exec(compile(src, "<unity-terminal>", "exec"), scope)
            emit("out", capture.getvalue(), ident)
        except Exception:
            emit("err", capture.getvalue() + traceback.format_exc(), ident)

        for key, value in list(scope.items()):
            if key.startswith("__"):
                continue
            if callable(value):
                continue
            emit("var", f"{key}={value!r}")


main()
