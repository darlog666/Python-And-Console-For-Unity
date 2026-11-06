import sys
import os
import traceback
import io
import json
import base64

_scope = {"__name__": "__main__"}

def _send_json(evt, exec_id=0, data=""):
    b64 = base64.b64encode(data.encode('utf-8')).decode('ascii')
    msg = {"evt": evt, "id": exec_id, "data_b64": b64}
    print(json.dumps(msg))
    sys.stdout.flush()

def main():
    _send_json("ready")

    for raw_line in sys.stdin:
        raw_line = raw_line.strip()
        if not raw_line:
            continue

        try:
            frame = json.loads(raw_line)
            cmd = frame.get("cmd")
            exec_id = frame.get("id", 0)
            
            if cmd == "exec":
                b64_src = frame.get("src_b64", "")
                src = base64.b64decode(b64_src).decode('utf-8')
                
                out_capture = io.StringIO()
                err_capture = io.StringIO()
                old_out, old_err = sys.stdout, sys.stderr
                sys.stdout, sys.stderr = out_capture, err_capture
                
                try:
                    compiled_code = compile(src, "<string>", "exec")
                    exec(compiled_code, _scope)
                except Exception:
                    traceback.print_exc(file=err_capture)
                finally:
                    sys.stdout, sys.stderr = old_out, old_err
                    
                out_val = out_capture.getvalue()
                err_val = err_capture.getvalue()
                
                if out_val:
                    _send_json("out", exec_id, out_val)
                if err_val:
                    _send_json("err", exec_id, err_val)
                    
                _send_json("done", exec_id)
                
        except Exception as e:
            _send_json("err", 0, f"Bootstrap parse error: {str(e)}")

if __name__ == "__main__":
    main()